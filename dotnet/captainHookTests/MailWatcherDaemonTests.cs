using System.Text.Json;
using CaptainHook.Actors;
using CaptainHook.Api;
using CaptainHook.Core;
using CaptainHook.Mail;
using CaptainHook.Wire;
using static CaptainHook.Tests.TestUtil;

namespace CaptainHook.Tests;

// ADR-0017 decision 4 / N2, slice `watcher-actor` — the watcher INSIDE a real
// daemon: it is built only when a watch path is handed in, it raises a nudge
// through the daemon's own dispatcher off a trail row, its armed deadline
// defers no idle-exit, and a turn it woke is activity for as long as it runs.
// The idle window is fake-clock seconds; nothing here waits out a real one.
public class MailWatcherDaemonTests
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(10);   // fake-clock seconds

    /// No watch path ⇒ no watcher: the daemon never reads a rules file, never
    /// writes a `nudges.jsonl`, and the trail carries no `watch.start`. This
    /// is what keeps every OTHER daemon test — none of which passes one — off
    /// the operator's live tree.
    [Fact]
    public async Task ADaemonWithNoWatchPath_HasNoWatcher()
    {
        using var log = new CapturedLog();
        using var dir = new TempRuntimeDir();
        using var stop = new CancellationTokenSource();
        var daemon = Task.Run(() => DaemonHost.RunAsync(dir.Paths, NoHarnessDir(), stop.Token, new Registry()));
        await PollUntilAsync(async () =>
            await ShimClient.TryForwardAsync(dir.Paths.SocketPath,
                new HookRequest("warmup00", "session-start", "claude-code", "{}"u8.ToArray()))
                is ForwardOutcome.Answered, TimeSpan.FromSeconds(15), "daemon up");
        stop.Cancel();
        Assert.Equal(0, await daemon.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.DoesNotContain(log.Events, e => e.Evt.StartsWith("watch."));
    }

    /// N2: a watcher with an ARMED deadline (ten minutes out) is not activity.
    /// The idle window passes, the daemon drains, the pump stops with it.
    [Fact]
    public async Task AnArmedDeadline_DefersNoIdleExit()
    {
        using var log = new CapturedLog();
        using var w = new Bus();
        w.Rules(Rule("reviewer", quietFor: "10min"));
        w.Register(TurnPayload("turn-claude"));
        var (daemon, _) = await w.StartAsync(TestHandler.Returning("turn", new Effect.Noop()));

        w.Send("m-01", "reviewer", MailPriority.Urgent);
        w.Trail("""{"evt":"mail.append","data":{"id":"m-01"}}""");
        await PollUntilAsync(() => Task.FromResult(log.Events.Any(e => e.Evt == "watch.evaluate" && Data(e, "trigger") == "Trail")),
            TimeSpan.FromSeconds(10), "the watcher evaluated off the trail row");
        var armed = Assert.Single(log.Events, e => e.Evt == "watch.evaluate" && Data(e, "trigger") == "Trail");
        Assert.Equal("600000", Data(armed, "nextCheckInMs"));   // armed, ten minutes out

        w.Clock.Advance(TimeSpan.FromSeconds(11));   // past the idle window; the deadline is nowhere near
        Assert.Equal(0, await daemon.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Contains(log.Events, e => e.Evt == "daemon.idleExit");
        Assert.Contains(log.Events, e => e.Evt == "watch.stop");
        Assert.DoesNotContain(log.Events, e => e.Evt == "mail.nudge");
    }

    /// A woken turn IS activity: while the turn's handler runs, the idle window
    /// cannot expire under it; when it finishes, the daemon earns a fresh
    /// window and then exits. The nudge itself flowed through the daemon's
    /// dispatcher, and the trail joins `mail.nudge` to the turn's rows.
    [Fact]
    public async Task ATurnTheWatcherWoke_IsActivityForAsLongAsItRuns()
    {
        using var log = new CapturedLog();
        using var w = new Bus();
        w.Rules(Rule("reviewer", quietFor: "0s"));
        w.Register(TurnPayload("turn-claude"));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (daemon, _) = await w.StartAsync(new TestHandler("turn", async (_, ctx) =>
        {
            entered.TrySetResult();
            await gate.Task.WaitAsync(ctx.Ct);
            return new Effect.Noop();
        }), budget: TimeSpan.FromMinutes(1));

        w.Send("m-01", "reviewer", MailPriority.Urgent);
        w.Trail("""{"evt":"mail.append","data":{"id":"m-01"}}""");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));   // the turn is running under the daemon

        w.Clock.Advance(TimeSpan.FromSeconds(11));   // the window would have expired
        await Assert.ThrowsAsync<TimeoutException>(
            () => daemon.WaitAsync(TimeSpan.FromMilliseconds(1500)));   // deterministically alive: Active > 0

        gate.SetResult();
        await PollUntilAsync(() => Task.FromResult(log.Events.Any(e => e.Evt == "nudge.dispatch")),
            TimeSpan.FromSeconds(10), "the turn finished");
        w.Clock.Advance(TimeSpan.FromSeconds(11));   // a fresh window, now spent
        Assert.Equal(0, await daemon.WaitAsync(TimeSpan.FromSeconds(15)));

        var nudge = Assert.Single(log.Events, e => e.Evt == "mail.nudge");
        var dispatch = Assert.Single(log.Events, e => e.Evt == "nudge.dispatch");
        Assert.Equal(nudge.Fields.DispatchId, dispatch.Fields.DispatchId);
        Assert.Contains(log.Events, e => e.Evt == "dispatch.start" && e.Fields.DispatchId == nudge.Fields.DispatchId);
        Assert.Contains(log.Events, e => e.Evt == "daemon.idleExit");
    }

    // ---- fixtures ---------------------------------------------------------------------

    private static string? Data(LogEvent e, string key) =>
        e.Fields.Data is { } d && d.TryGetValue(key, out var v) ? v?.ToString() : null;

    private static object Rule(string role, string quietFor) => new
    {
        role,
        when = new { priority = ">=urgent", quietFor },
        budget = new { perEnvelope = 1, perRoleHour = 4 },
    };

    private static object TurnPayload(string name) => new
    {
        name,
        command = "/usr/bin/turn-claude.sh",
        args = Array.Empty<string>(),
        events = new[] { "mail-nudge" },
        mode = "oneshot",
        failMode = "open",
    };

    /// A sandboxed daemon: throwaway rendezvous, its own mail dir / rules /
    /// registrations / trail, a FakeClock for the idle window and the watcher.
    private sealed class Bus : IDisposable
    {
        public string Home { get; } = Path.Combine(Path.GetTempPath(), "chk-wbus-" + Guid.NewGuid().ToString("N")[..8]);
        public string MailDir => Path.Combine(Home, "mail");
        public string TrailPath => Path.Combine(Home, "trail.jsonl");
        private string HandlersPath => Path.Combine(Home, "handlers.json");
        private string WatchPath => Path.Combine(Home, "watch.json");
        public FakeClock Clock { get; } = new();
        private readonly TempRuntimeDir _dir = new();

        public Bus() => Directory.CreateDirectory(Home);

        public void Register(params object[] handlers) =>
            File.WriteAllText(HandlersPath, JsonSerializer.Serialize(new { version = 1, handlers }));

        public void Rules(params object[] rules) =>
            File.WriteAllText(WatchPath, JsonSerializer.Serialize(new { version = 1, rules }));

        public void Send(string id, string to, MailPriority priority) =>
            MailFixtures.AppendOk(new MailStore(MailDir), MailFixtures.Envelope(id: id, to: to, priority: priority));

        public void Trail(string line) => File.AppendAllText(TrailPath, line + "\n");

        /// `budget` is the turn handler's own (a real turn payload carries one
        /// in `handlers.json`; the daemon's default 2s is for hook handlers).
        public async Task<(Task<int> Daemon, string Socket)> StartAsync(IHandler turn, TimeSpan? budget = null)
        {
            var reg = budget is { } b
                ? new Registry().On(MailNudgeEvent.EventType, turn, b)
                : new Registry().On(MailNudgeEvent.EventType, turn);
            var daemon = Task.Run(() => DaemonHost.RunAsync(_dir.Paths, NoHarnessDir(), CancellationToken.None,
                reg, drainDeadline: TimeSpan.FromSeconds(5), idleWindow: Window, clock: Clock.Now,
                handlersPath: HandlersPath, mailDir: MailDir, watchPath: WatchPath,
                watchPoll: TimeSpan.FromMilliseconds(20),
                sse: new SseOptions(TrailPath, Poll: TimeSpan.FromMilliseconds(50))));
            await PollUntilAsync(async () =>
                await ShimClient.TryForwardAsync(_dir.Paths.SocketPath,
                    new HookRequest("warmup00", "session-start", "claude-code", "{}"u8.ToArray()))
                    is ForwardOutcome.Answered, TimeSpan.FromSeconds(15), "daemon up");
            return (daemon, _dir.Paths.SocketPath);
        }

        public void Dispose()
        {
            _dir.Dispose();
            try { Directory.Delete(Home, recursive: true); } catch { /* best-effort */ }
        }
    }
}
