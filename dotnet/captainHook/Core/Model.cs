using System.Text.Json;

namespace CaptainHook.Core;

/// A normalized lifecycle event from the agent host.
public sealed record HookEvent(string Type, string? SessionId, string? Cwd, JsonElement Payload);

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

public sealed record HandlerContext(DateTimeOffset Deadline, CancellationToken Ct);

/// A unit of work spliced into the loop at a lifecycle event.
public interface IHandler
{
    string Name { get; }
    FailMode OnFailure => FailMode.Open;   // default interface member
    Task<Effect> HandleAsync(HookEvent e, HandlerContext ctx);
}
