namespace CaptainHook.Actors

open System
open System.Threading.Tasks

/// Stable handle callers keep across restarts. The supervisor swaps the live
/// MailboxProcessor underneath on every restart, so a reference held by the C#
/// host never goes stale — this is the fix for the classic gotcha where callers
/// keep posting into a dead mailbox after a crash.
type ActorRef<'Msg> internal (id: string) =
    // Written by the supervisor on (re)start, read by callers. Swap happens
    // before the ref is ever handed out, and again on each restart.
    let mutable current: MailboxProcessor<'Msg> = Unchecked.defaultof<_>

    member _.Id = id
    member internal _.Swap(agent: MailboxProcessor<'Msg>) = current <- agent

    /// tell — fire-and-forget. Never blocks (MailboxProcessor is UNBOUNDED —
    /// use the Channels shape in HotPath.fs when you need a bounded mailbox).
    member _.Post(msg: 'Msg) = current.Post msg

    /// ask — request/reply surfaced as a Task so C# can `await` it directly.
    /// A default timeout makes a reply lost to a crash fail loudly (TimeoutException)
    /// instead of hanging the caller forever.
    member _.Ask<'Reply>(build: Func<AsyncReplyChannel<'Reply>, 'Msg>, ?timeoutMs: int) : Task<'Reply> =
        let t = defaultArg timeoutMs 2000
        Async.StartAsTask(current.PostAndAsyncReply((fun rc -> build.Invoke rc), timeout = t))

/// The supervisor's own protocol. A crash is REIFIED AS A MESSAGE (the
/// Node/BEAM idiom): it queues in this mailbox and is handled one at a time,
/// so the supervisor never juggles two crashes concurrently.
type private SupMsg =
    | ChildExit of id: string * error: exn * restart: (unit -> unit)

/// one_for_one supervisor, itself just another MailboxProcessor.
/// Children are (re)created from factories — the OTP child-spec idea: restart
/// means "run the factory again", which yields fresh state + a new mailbox by
/// construction. A sliding restart-intensity window stops crash loops: blow the
/// budget and the supervisor escalates (OnEscalated) instead of looping forever.
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

    let agent =
        MailboxProcessor.Start(fun inbox ->
            let rec loop (history: Map<string, int64 list>) =
                async {
                    match! inbox.Receive() with
                    | ChildExit (id, err, restart) ->
                        let now = clock.Invoke()
                        let attempts =
                            history
                            |> Map.tryFind id
                            |> Option.defaultValue []
                            |> List.filter (fun t -> now - t <= windowMs)   // prune outside window
                            |> fun recent -> now :: recent

                        if List.length attempts > maxRestarts then
                            // Budget blown: the fault is persistent, restarting is
                            // pointless. Stop this child for good and tell the host.
                            Log.Error(
                                sprintf "sup:%s" name, "actor.escalate",
                                LogFields(
                                    ActorId = id, Msg = err.Message,
                                    Data = dict [ "maxRestarts", box maxRestarts
                                                  "windowMs", box window.TotalMilliseconds ]))
                            onEscalated.Invoke(id, err)
                            return! loop (Map.remove id history)
                        else
                            Log.Warn(
                                sprintf "sup:%s" name, "actor.restart",
                                LogFields(
                                    ActorId = id, Msg = err.Message,
                                    Data = dict [ "restart", box (List.length attempts)
                                                  "maxRestarts", box maxRestarts
                                                  "windowMs", box window.TotalMilliseconds ]))
                            restart ()   // factory re-runs -> fresh state, new mailbox
                            return! loop (Map.add id attempts history)
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

    /// Spawn a supervised child. `factory` runs once now and once per restart.
    member _.Spawn<'Msg>(id: string, factory: Func<MailboxProcessor<'Msg>>) : ActorRef<'Msg> =
        Log.Info(sprintf "sup:%s" name, "actor.spawn", LogFields(ActorId = id))
        let handle = ActorRef<'Msg>(id)
        let rec start () =
            let child = factory.Invoke()
            // A MailboxProcessor whose body throws dies SILENTLY unless someone
            // subscribes .Error — the supervisor is that someone. Crash -> message.
            child.Error.Add(fun ex -> agent.Post(ChildExit(id, ex, start)))
            handle.Swap child
        start ()
        handle
