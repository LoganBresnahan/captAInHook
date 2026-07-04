using CaptainHook.Actors;
using CaptainHook.Core;

namespace CaptainHook.Handlers;

/// v0 flagship. Proves the round-trip: event in -> effect out -> visible in
/// the agent's context. Fail-open by default (inherits IHandler.OnFailure).
public sealed class EchoHandler : IHandler
{
    public string Name => "echo";

    public async Task<Effect> HandleAsync(HookEvent e, HandlerContext ctx)
    {
        await Task.Delay(10, ctx.Ct); // token-aware: the budget can cancel us
        var ts = DateTimeOffset.UtcNow.ToString("HH:mm:ss.fff");
        return e.Type switch
        {
            "SessionStart" or "UserPromptSubmit" =>
                new Effect.Inject($"captAInHook: {e.Type} seen @ {ts}"),
            "PostToolUse" =>
                new Effect.Background(async _ =>
                {
                    await Task.Delay(5);
                    // Structured audit event instead of a raw stderr line — the
                    // Log sinks decide where it lands (JSONL file + pretty stderr).
                    Log.Info("audit", "audit.echo",
                        new LogFields { HookEvent = e.Type, SessionId = e.SessionId,
                                        Msg = $"echo audited PostToolUse @ {ts}" });
                }),
            _ => new Effect.Noop(),
        };
    }
}

/// Demo handler so fan-out is observable. Register it alongside echo to watch
/// Task.WhenAll run them concurrently — and to feel the budget bite when the
/// delay approaches the deadline.
public sealed class LatencyProbeHandler(TimeSpan delay) : IHandler
{
    public string Name => "latency-probe";

    public async Task<Effect> HandleAsync(HookEvent e, HandlerContext ctx)
    {
        await Task.Delay(delay, ctx.Ct);
        return new Effect.Inject($"latency-probe: waited {delay.TotalMilliseconds:F0}ms concurrently");
    }
}
