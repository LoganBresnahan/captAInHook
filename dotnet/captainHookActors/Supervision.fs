namespace CaptainHook.Actors

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks

/// Stable handle callers keep across restarts. The supervisor swaps the live
/// MailboxProcessor underneath on every restart, so a reference held by the C#
/// host never goes stale — this is the fix for the classic gotcha where callers
/// keep posting into a dead mailbox after a crash.
type ActorRef<'Msg> internal (id: string) =
    // Written by the supervisor on (re)start, read by callers. Swap happens
    // before the ref is ever handed out, and again on each restart.
    let mutable current: MailboxProcessor<'Msg> = Unchecked.defaultof<_>
    // Instance generation, bumped by every Swap: the per-handle RESTART COUNT,
    // read by the host's supervision read model (ADR-0007 get-handlers).
    let generation = ref 0
    // The instance's EPOCH: a supervisor-global, monotonic spawn token, set on
    // every Swap. Fault signals (ChildExit/ChildWedged) carry the EPOCH, not
    // the generation — because the generation RESETS to 1 on a fresh handle,
    // and a hot-reload CHANGE retires a worker via Remove + re-Spawn at the
    // SAME id (ADR-0010 phase 7), minting a fresh handle whose generation 1
    // would ALIAS the retired handle's generation 1. That aliasing charged the
    // retired child's death to the fresh replacement — churning it and, under
    // repetition, escalating a healthy worker to permanent-DEAD (the phase-7
    // adversarial-verify HIGH). A monotonic epoch never aliases across
    // Remove+Spawn, so the stale signal is recognized and dropped. (Late
    // signals from an ABANDONED instance — a wedged worker's stuck task finally
    // dying — carry the epoch they belonged to, the same stale-detection ADR-
    // 0004 d5 used the generation for, now alias-proof.)
    let epoch = ref 0
    // Set once by the supervisor on escalation; read by ask paths to fail
    // fast instead of burning the full ask timeout on a corpse (carry-in b).
    let dead = ref false

    member _.Id = id
    member internal _.Swap(agent: MailboxProcessor<'Msg>, ep: int) =
        current <- agent
        Volatile.Write(&epoch.contents, ep)
        Volatile.Write(&generation.contents, generation.Value + 1)
    member _.Generation = Volatile.Read(&generation.contents)
    member _.Epoch = Volatile.Read(&epoch.contents)
    member _.IsDead = Volatile.Read(&dead.contents)
    member internal _.MarkDead() = Volatile.Write(&dead.contents, true)

    /// tell — fire-and-forget. Never blocks (MailboxProcessor is UNBOUNDED —
    /// use the Channels shape in HotPath.fs when you need a bounded mailbox).
    member _.Post(msg: 'Msg) = current.Post msg

    /// ask — request/reply surfaced as a Task so C# can `await` it directly.
    /// A default timeout makes a reply lost to a crash fail loudly (TimeoutException)
    /// instead of hanging the caller forever.
    member _.Ask<'Reply>(build: Func<AsyncReplyChannel<'Reply>, 'Msg>, ?timeoutMs: int) : Task<'Reply> =
        let t = defaultArg timeoutMs 2000
        Async.StartAsTask(current.PostAndAsyncReply((fun rc -> build.Invoke rc), timeout = t))

/// The supervisor's own protocol. Faults are REIFIED AS MESSAGES (the
/// Node/BEAM idiom): they queue in this mailbox and are handled one at a time,
/// so the supervisor never juggles two faults concurrently. Every signal
/// carries the generation of the instance it is about, so late signals from
/// abandoned instances are recognized as stale.
type private SupMsg =
    | ChildExit of id: string * epoch: int * error: exn
    | ChildWedged of id: string * epoch: int * correlationId: string

/// What the supervisor needs to act on a child by id, registered at Spawn.
/// Closures, not types: the supervisor stays 'Msg-agnostic.
type private ChildEntry =
    { CurrentEpoch: unit -> int
      IsDead: unit -> bool
      Restart: unit -> unit
      MarkDead: unit -> unit }

/// one_for_one supervisor, itself just another MailboxProcessor.
/// Children are (re)created from factories — the OTP child-spec idea: restart
/// means "run the factory again", which yields fresh state + a new mailbox by
/// construction. A sliding restart-intensity window stops crash loops: blow the
/// budget and the supervisor escalates (OnEscalated) instead of looping forever.
///
/// FAULT CLASSIFICATION (ADR-0004 decision 5): timeout is not fault. Three
/// classified outcomes drive the counting —
///   * honored cancellation (the exit exception is an OperationCanceledException:
///     the handler respected its budget token) restarts the worker (its mailbox
///     died via reply-then-crash) but does NOT count toward the window — a
///     correct-but-slow handler is never escalated by the fault breaker
///     (carry-in c, changed deliberately);
///   * a crash (any other exception) counts and restarts — unchanged;
///   * a wedge (received but never answered within budget+grace, reported by
///     the ask layer) counts AND abandon-and-respawns: the factory re-runs, the
///     handle swaps, and the stuck computation is leaked — .NET cannot kill
///     user code mid-flight, so each wedge leaks a task; that is exactly why
///     wedges count and a chronic wedger escalates (carry-in a).
/// Escalation marks the handle dead so asks fail fast (carry-in b).
///
/// TIME: window math runs on an injectable MONOTONIC clock (milliseconds).
/// Wall-clock (DateTime.UtcNow) can step under NTP corrections or dual-boot RTC
/// skew, silently stretching or shrinking the window — monotonic time only
/// moves forward. Wall time still appears where humans read it (log event ts);
/// intervals are computed here, on the monotonic clock. Tests inject a fake
/// clock and advance it explicitly instead of sleeping through real windows.
type Supervisor(name: string, maxRestarts: int, window: TimeSpan, clock: Func<int64>) =
    let windowMs = int64 window.TotalMilliseconds
    let mutable onEscalated = Action<string, exn>(fun _ _ -> ())
    // ADDITIVE escalation subscriptions, distinct from the settable OnEscalated:
    // infrastructure (the host Dispatcher's teardown seam) subscribes here so a
    // host later ASSIGNING OnEscalated can never silently clobber it. Invoked on
    // the supervisor's fault loop — subscribers must not block (fire-and-forget
    // any real work).
    let escalationSubs = ResizeArray<Action<string, exn>>()
    let children = ConcurrentDictionary<string, ChildEntry>()
    // Supervisor-global monotonic spawn token (ActorRef.Epoch above): every
    // start() — initial spawn, restart, or a hot-reload re-Spawn at a reused id
    // — takes a fresh epoch, so fault signals can never alias across a
    // Remove+Spawn (the phase-7 generation-aliasing HIGH). Incremented under
    // Interlocked: restarts run on the mailbox loop, Spawn/reconcile on caller
    // threads.
    let epochSeq = ref 0

    let agent =
        MailboxProcessor.Start(fun inbox ->
            // Escalation, in one place: stop the child for good, mark its
            // handle dead (asks fail fast), tell the host, then the additive
            // subscribers (the Dispatcher's teardown seam rides these).
            let escalate (entry: ChildEntry) id (err: exn) (kind: string) =
                Log.Error(
                    sprintf "sup:%s" name, "actor.escalate",
                    LogFields(
                        ActorId = id, Msg = err.Message,
                        Data = dict [ "kind", box kind
                                      "maxRestarts", box maxRestarts
                                      "windowMs", box window.TotalMilliseconds ]))
                entry.MarkDead()
                onEscalated.Invoke(id, err)
                let subs = lock escalationSubs (fun () -> escalationSubs.ToArray())
                for s in subs do s.Invoke(id, err)

            // Restart, guarded: a THROWING user factory would otherwise kill
            // this supervisor's own mailbox body silently — no more restarts
            // or escalations for anyone, the whole escalation seam dark
            // (adversarial-verify find). A factory that cannot construct its
            // handler is escalated on the spot.
            let restartSafely (entry: ChildEntry) id =
                try entry.Restart() with ex -> escalate entry id ex "restartFailed"

            // Count one FAULT (crash or wedge — the counted kinds) against the
            // sliding window; restart under budget, escalate over it.
            let count (entry: ChildEntry) id (err: exn) (kind: string) (history: Map<string, int64 list>) =
                let now = clock.Invoke()
                let attempts =
                    history
                    |> Map.tryFind id
                    |> Option.defaultValue []
                    |> List.filter (fun t -> now - t <= windowMs)   // prune outside window
                    |> fun recent -> now :: recent

                if List.length attempts > maxRestarts then
                    // Budget blown: the fault is persistent, restarting is
                    // pointless.
                    escalate entry id err kind
                    Map.remove id history
                else
                    Log.Warn(
                        sprintf "sup:%s" name, "actor.restart",
                        LogFields(
                            ActorId = id, Msg = err.Message,
                            Data = dict [ "kind", box kind
                                          "counted", box true
                                          "restart", box (List.length attempts)
                                          "maxRestarts", box maxRestarts
                                          "windowMs", box window.TotalMilliseconds ]))
                    restartSafely entry id   // factory re-runs -> fresh state, new mailbox
                    Map.add id attempts history

            let rec loop (history: Map<string, int64 list>) =
                async {
                    match! inbox.Receive() with
                    | ChildExit (id, epoch, err) ->
                        match children.TryGetValue id with
                        | true, entry when entry.CurrentEpoch() = epoch && not (entry.IsDead()) ->
                            match err with
                            | :? OperationCanceledException ->
                                // The handler honored its budget token: this exit
                                // is a TIMEOUT, not a fault. Restart (the mailbox
                                // died via reply-then-crash) without counting —
                                // chronic slowness stays visible through the
                                // dispatcher's handler.timeout warns, not a breaker.
                                Log.Warn(
                                    sprintf "sup:%s" name, "actor.restart",
                                    LogFields(
                                        ActorId = id, Msg = err.Message,
                                        Data = dict [ "kind", box "cancelled"
                                                      "counted", box false ]))
                                restartSafely entry id
                                return! loop history
                            | _ ->
                                return! loop (count entry id err "crash" history)
                        | _ ->
                            // Stale generation (an abandoned instance dying late)
                            // or already-escalated child: its death is not news.
                            Log.Debug(
                                sprintf "sup:%s" name, "actor.staleExit",
                                LogFields(ActorId = id, Msg = err.Message))
                            return! loop history
                    | ChildWedged (id, epoch, correlationId) ->
                        match children.TryGetValue id with
                        | true, entry when entry.CurrentEpoch() = epoch && not (entry.IsDead()) ->
                            Log.Warn(
                                sprintf "sup:%s" name, "actor.wedge",
                                LogFields(
                                    ActorId = id, DispatchId = correlationId,
                                    Msg = "ask received but never answered within budget+grace; abandoning (stuck task leaked)"))
                            let err = TimeoutException(sprintf "worker %s wedged" id) :> exn
                            return! loop (count entry id err "wedge" history)
                        | _ ->
                            return! loop history   // stale or dead: already handled
                }
            loop Map.empty)

    /// Default clock: Environment.TickCount64 — monotonic ms since boot.
    /// Millisecond resolution, zero allocation, immune to clock steps: the
    /// right primitive for interval math (Stopwatch.GetTimestamp would add
    /// sub-ms precision we don't need for multi-second windows).
    new(name: string, maxRestarts: int, window: TimeSpan) =
        Supervisor(name, maxRestarts, window, Func<int64>(fun () -> Environment.TickCount64))

    /// Host-facing escalation callback (an Action so C# can assign a lambda).
    /// Last-writer-wins by design — the one host-owned slot. Infrastructure
    /// that must OBSERVE every escalation uses SubscribeEscalated instead.
    member _.OnEscalated
        with get () = onEscalated
        and set v = onEscalated <- v

    /// Additive escalation subscription (never clobbered by OnEscalated
    /// assignment; invoked after it, in subscription order, on the fault
    /// loop — do not block). The Dispatcher's handler-teardown seam lives on
    /// this: MarkDead never re-runs the factory, so the LAST instance's
    /// disposal has to hang off the escalation signal itself.
    member _.SubscribeEscalated(cb: Action<string, exn>) =
        lock escalationSubs (fun () -> escalationSubs.Add cb)

    /// The ask layer's narrow channel (ADR-0004 decision 5): report a wedge —
    /// an ask that was received but never answered within budget + grace — for
    /// this generation of the child. The supervisor owns the counting; a stale
    /// generation is ignored.
    member _.ReportWedge(id: string, epoch: int, correlationId: string) =
        agent.Post(ChildWedged(id, epoch, correlationId))

    /// Spawn a supervised child. `factory` runs once now and once per restart.
    /// Duplicate ids are REFUSED loudly: a silent `children[id] <- entry`
    /// overwrite would route one child's faults to the other's restart/
    /// MarkDead closures — supervision and any id-keyed host seam (the
    /// Dispatcher's teardown map) silently corrupt (adversarial-verify find).
    member _.Spawn<'Msg>(id: string, factory: Func<MailboxProcessor<'Msg>>) : ActorRef<'Msg> =
        if children.ContainsKey id then
            invalidOp (sprintf "supervisor '%s': duplicate child id '%s'" name id)
        Log.Info(sprintf "sup:%s" name, "actor.spawn", LogFields(ActorId = id))
        let handle = ActorRef<'Msg>(id)
        let rec start () =
            // Retirement re-check (phase-7 adversarial verify, Finding A): a
            // hot-reload REMOVE can MarkDead this handle AFTER the fault loop
            // passed its guard but BEFORE this restart runs (the guard→count→
            // Log→restartSafely window). Re-checking here collapses that window
            // so a retired worker is not resurrected into a live child that
            // outlives its deleted entry (a straggler cleaned only at drain).
            if handle.IsDead then ()
            else
                let child = factory.Invoke()
                // A fresh EPOCH per start() — globally monotonic, so a reused
                // id (Remove+Spawn) never aliases a retired instance's fault
                // signal. Spawn and restart both run sequentially (restarts
                // inside the supervisor loop), so no concurrent Swap interleaves.
                let epoch = System.Threading.Interlocked.Increment(&epochSeq.contents)
                // A MailboxProcessor whose body throws dies SILENTLY unless
                // someone subscribes .Error — the supervisor is that someone.
                // Crash -> message, tagged with the instance's epoch.
                child.Error.Add(fun ex -> agent.Post(ChildExit(id, epoch, ex)))
                handle.Swap(child, epoch)
        children[id] <-
            { CurrentEpoch = fun () -> handle.Epoch
              IsDead = fun () -> handle.IsDead
              Restart = start
              MarkDead = fun () -> handle.MarkDead() }
        start ()
        handle

    /// Remove a supervised child at runtime — the inverse of Spawn, for the
    /// handlers.json hot-reload reconcile (ADR-0010 phase 7). The host has
    /// already (or is about to) tear down the handler instance and kill its
    /// child; this drops the child-spec so a late ChildExit finds no live
    /// entry and never restarts a worker the host has retired, and marks the
    /// handle DEAD FIRST so an in-flight ask fails fast AND a concurrent fault
    /// already in the loop sees IsDead and treats the exit as stale (mark
    /// before remove: the fault loop's guard is `not (IsDead())`, so setting
    /// it first shrinks the restart-vs-remove window that would otherwise
    /// resurrect a retired child). Idempotent — an unknown/already-removed id
    /// is a no-op. The orphaned MailboxProcessor holds no OS resources; its
    /// parked Receive is a self-referential cycle the GC reclaims once the host
    /// drops its Worker reference. (Escalation, by contrast, keeps its entry
    /// forever via MarkDead alone — Remove is the cleaner retirement.)
    member _.Remove(id: string) =
        match children.TryGetValue id with
        | true, entry ->
            entry.MarkDead()
            children.TryRemove id |> ignore
            Log.Info(sprintf "sup:%s" name, "actor.remove", LogFields(ActorId = id))
        | _ -> ()
