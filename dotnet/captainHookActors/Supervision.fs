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
    // Instance generation, bumped by every Swap. Late signals from an
    // ABANDONED instance (a wedged worker's stuck task finally dying) carry
    // the generation they belonged to, so the supervisor can recognize them
    // as stale instead of restarting the healthy replacement (ADR-0004 d5).
    let generation = ref 0
    // Set once by the supervisor on escalation; read by ask paths to fail
    // fast instead of burning the full ask timeout on a corpse (carry-in b).
    let dead = ref false

    member _.Id = id
    member internal _.Swap(agent: MailboxProcessor<'Msg>) =
        current <- agent
        Volatile.Write(&generation.contents, generation.Value + 1)
    member _.Generation = Volatile.Read(&generation.contents)
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
    | ChildExit of id: string * gen: int * error: exn
    | ChildWedged of id: string * gen: int * correlationId: string

/// What the supervisor needs to act on a child by id, registered at Spawn.
/// Closures, not types: the supervisor stays 'Msg-agnostic.
type private ChildEntry =
    { CurrentGen: unit -> int
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
    let children = ConcurrentDictionary<string, ChildEntry>()

    let agent =
        MailboxProcessor.Start(fun inbox ->
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
                    // pointless. Stop this child for good, mark its handle dead
                    // (asks fail fast from here on) and tell the host.
                    Log.Error(
                        sprintf "sup:%s" name, "actor.escalate",
                        LogFields(
                            ActorId = id, Msg = err.Message,
                            Data = dict [ "kind", box kind
                                          "maxRestarts", box maxRestarts
                                          "windowMs", box window.TotalMilliseconds ]))
                    entry.MarkDead()
                    onEscalated.Invoke(id, err)
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
                    entry.Restart()   // factory re-runs -> fresh state, new mailbox
                    Map.add id attempts history

            let rec loop (history: Map<string, int64 list>) =
                async {
                    match! inbox.Receive() with
                    | ChildExit (id, gen, err) ->
                        match children.TryGetValue id with
                        | true, entry when entry.CurrentGen() = gen && not (entry.IsDead()) ->
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
                                entry.Restart()
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
                    | ChildWedged (id, gen, correlationId) ->
                        match children.TryGetValue id with
                        | true, entry when entry.CurrentGen() = gen && not (entry.IsDead()) ->
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
    member _.OnEscalated
        with get () = onEscalated
        and set v = onEscalated <- v

    /// The ask layer's narrow channel (ADR-0004 decision 5): report a wedge —
    /// an ask that was received but never answered within budget + grace — for
    /// this generation of the child. The supervisor owns the counting; a stale
    /// generation is ignored.
    member _.ReportWedge(id: string, gen: int, correlationId: string) =
        agent.Post(ChildWedged(id, gen, correlationId))

    /// Spawn a supervised child. `factory` runs once now and once per restart.
    member _.Spawn<'Msg>(id: string, factory: Func<MailboxProcessor<'Msg>>) : ActorRef<'Msg> =
        Log.Info(sprintf "sup:%s" name, "actor.spawn", LogFields(ActorId = id))
        let handle = ActorRef<'Msg>(id)
        let rec start () =
            let child = factory.Invoke()
            // The generation this instance WILL have once swapped in. Spawn and
            // restart both run sequentially (restarts inside the supervisor
            // loop), so no concurrent Swap can interleave.
            let gen = handle.Generation + 1
            // A MailboxProcessor whose body throws dies SILENTLY unless someone
            // subscribes .Error — the supervisor is that someone. Crash ->
            // message, tagged with the instance's generation.
            child.Error.Add(fun ex -> agent.Post(ChildExit(id, gen, ex)))
            handle.Swap child
        children[id] <-
            { CurrentGen = fun () -> handle.Generation
              IsDead = fun () -> handle.IsDead
              Restart = start
              MarkDead = fun () -> handle.MarkDead() }
        start ()
        handle
