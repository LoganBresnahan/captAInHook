using System.Text.Json;

namespace CaptainHook.Core;

/// A normalized lifecycle event from the agent host.
/// `Harness` is the name of the spec that parsed the event (`claude-code`,
/// a user override's name, `internal` for MailNudge) — null only for events a
/// test constructs by hand. It rides the exec-wire envelope as `event.harness`
/// (ADR-0010 d2, 2026-10-14 amendment) so a payload can tell which host's raw
/// `payload` it is reading.
public sealed record HookEvent(
    string Type, string? SessionId, string? Cwd, JsonElement Payload, string? Harness = null);

public enum Verdict { Allow, Deny, Ask }
public enum FailMode { Open, Closed }

/// The bounded set of ways a handler may affect the loop.
/// Records + pattern matching are C#'s take on the discriminated unions
/// you'd reach for in Gleam / Elixir / F#.
public abstract record Effect
{
    public sealed record Inject(string Text) : Effect;
    public sealed record Decide(Verdict Verdict, string? Reason) : Effect;
    public sealed record Replace(string Text) : Effect;
    public sealed record Background(Func<CancellationToken, Task> Run) : Effect;
    public sealed record Noop : Effect;
}

/// DispatchId rides along (optional) so a handler that crosses a process
/// boundary — ExecHandler's child envelope (ADR-0010 d2) — can carry the SAME
/// correlation id the trail uses; in-process handlers ignore it.
public sealed record HandlerContext(DateTimeOffset Deadline, CancellationToken Ct, string? DispatchId = null);

/// A unit of work spliced into the loop at a lifecycle event.
public interface IHandler
{
    string Name { get; }
    FailMode OnFailure => FailMode.Open;   // default interface member
    Task<Effect> HandleAsync(HookEvent e, HandlerContext ctx);
}
