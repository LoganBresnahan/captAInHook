using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CaptainHook.Core;
using CaptainHook.Handlers;
using CaptainHook.Mail;
using CaptainHook.Wire;
using static CaptainHook.Tests.TestUtil;

namespace CaptainHook.Tests;

// first-members-dogfood (roadmap item 20 / ADR-0016 phase 5, the last slice
// before the docs capstone): the two committed starter MEMBERS of the mailbox
// bus, run as real processes.
//
// Two things are proven here that no unit can reach:
//
//   1. the REENTRANCY GUARD is present and effective in the model-backed
//      member — proven by a stub `claude` that EXITS NONZERO when
//      `--setting-sources ""` is missing, so the guard is proven PASSED rather
//      than merely present in the file (ADR-0010 N7). A payload that spawns
//      the same agent it hooks is an infinite regress the engine CANNOT
//      detect, so the only available proof is a child that refuses.
//
//   2. two agent loops on ONE daemon reach each other and NOT themselves —
//      the hub position's whole claim. Roles are static in handlers.json, so
//      what separates two agents is DISPATCH POLICY (ADR-0016's "swarm
//      activation is a dispatch-policy flip"): handler-named rules AND a
//      project path-prefix, which is exercised here rather than described.
//
// Everything runs against sandbox HOMEs; the live ~/.captainHook tree is never
// touched (house rule — a test that writes the real sinks pollutes the
// maintainer's own logs).

/// Shared plumbing for driving a committed payload script as a real process.
public static class DogfoodInfra
{
    /// Walk up to the committed examples/payloads/ — the starters are run
    /// VERBATIM, never copied, so a test can only pass against what ships.
    public static string PayloadsDir()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var cand = Path.Combine(dir, "examples", "payloads");
            if (File.Exists(Path.Combine(cand, "starter-mail-observer.sh"))) return cand;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        throw new DirectoryNotFoundException("examples/payloads not found above the test assembly");
    }

    /// The child apphost's runtime (MailDigestDaemonSmokeTests' reasoning:
    /// the env is a stripped allowlist and a user-local install is invisible
    /// without DOTNET_ROOT).
    public static string DotnetRoot()
    {
        var corelib = typeof(object).Assembly.Location;
        return Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(
            Path.GetDirectoryName(corelib)!)!)!)!;
    }

    public static string EngineBin() => Path.Combine(AppContext.BaseDirectory, "captainHook");
}

/// The reentrancy guard, proven by a child that refuses. This is the new test
/// pattern the plan asks for: the stub stands in for the second model, and its
/// EXIT CODE is the assertion — a payload that forgets the guard cannot get a
/// usable answer out of it.
public class MailWatcherReentrancyTests : IDisposable
{
    private readonly TempRuntimeDir _tmp = new();
    private readonly string _home, _views, _stubDir;

    public MailWatcherReentrancyTests()
    {
        Directory.CreateDirectory(_tmp.Path);
        _home = Path.Combine(_tmp.Path, "home");
        _views = Path.Combine(_home, ".captainHook", "observer-views");
        _stubDir = Path.Combine(_tmp.Path, "stubbin");
        Directory.CreateDirectory(_views);
        Directory.CreateDirectory(_stubDir);
    }

    public void Dispose() => _tmp.Dispose();

    /// A stand-in for the second model that ENFORCES the guard: it scans its
    /// own argv for `--setting-sources` followed by an empty string and exits
    /// 1 if it is absent. That is exactly the shape of the real hazard — the
    /// child would otherwise start WITH hook configuration and fire the very
    /// event that spawned it.
    private string WriteStubClaude(string answer = "Re-read the parser before you touch the writer.")
    {
        var stub = Path.Combine(_stubDir, "claude");
        File.WriteAllText(stub, $"""
            #!/bin/sh
            # stub `claude` — the reentrancy guard's enforcer (see MailDogfoodTests).
            guard=no
            prev=""
            for a in "$@"; do
              if [ "$prev" = "--setting-sources" ] && [ -z "$a" ]; then guard=yes; fi
              prev="$a"
            done
            if [ "$guard" != yes ]; then
              echo "stub-claude: REENTRANCY GUARD MISSING (--setting-sources \"\")" >&2
              exit 1
            fi
            cat >/dev/null
            printf '%s\n' {Sh(answer)}
            """.Replace("\r\n", "\n"));
        File.SetUnixFileMode(stub,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return stub;
    }

    private static string Sh(string s) => "'" + s.Replace("'", "'\\''") + "'";

    /// Seed the shared observer view files so the watcher's deterministic gate
    /// (my edits ∩ the peer's reads) opens — otherwise it exits without ever
    /// reaching the model, which is the on-demand discipline working.
    private void SeedOverlap(string path = "/repo/src/parser.cs")
    {
        File.WriteAllText(Path.Combine(_views, "alpha.edits"), path + "\n");
        File.WriteAllText(Path.Combine(_views, "beta"), path + "\n");
    }

    private async Task<(int Exit, string Stdout, string Stderr)> RunWatcherAsync(string script)
    {
        var psi = new ProcessStartInfo(script)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // PATH puts the stub FIRST so `command -v claude` finds it; a real
        // claude on the developer's machine must never be reachable here.
        psi.Environment["PATH"] = _stubDir + ":" + "/usr/bin:/bin";
        psi.Environment["HOME"] = _home;
        psi.Environment["MAIL_ROLE"] = "alpha";
        psi.Environment["MAIL_PEER"] = "beta";
        psi.Environment["CAPTAINHOOK_BIN"] = DogfoodInfra.EngineBin();
        psi.Environment["DOTNET_ROOT"] = DogfoodInfra.DotnetRoot();

        using var p = Process.Start(psi)!;
        await p.StandardInput.WriteLineAsync(
            """{"v":1,"dispatchId":"d-watch","event":{"type":"Stop","sessionId":"s-alpha","cwd":"/repo","payload":{}}}""");
        p.StandardInput.Close();
        var stdout = await p.StandardOutput.ReadToEndAsync();
        var stderr = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token);
        return (p.ExitCode, stdout, stderr);
    }

    private IReadOnlyList<MailEnvelope> Delivered()
    {
        var store = new MailStore(Path.Combine(_home, ".captainHook", "mail"));
        return store.Read().Select(l => l.Envelope).ToList();
    }

    /// The guard is present in the shipped starter, so the stub answers and
    /// the model's words reach the bus. The payload still answers `noop` —
    /// a bus member must not touch the loop it observes.
    [Fact]
    public async Task ShippedWatcher_PassesTheGuard_ModelWordsReachTheBus()
    {
        WriteStubClaude();
        SeedOverlap();

        var (exit, stdout, _) = await RunWatcherAsync(
            Path.Combine(DogfoodInfra.PayloadsDir(), "starter-mail-watcher.sh"));

        Assert.Equal(0, exit);
        Assert.Contains("\"effect\":\"noop\"", stdout);

        var env = Assert.Single(Delivered());
        Assert.Equal("beta", env.To);
        Assert.Equal("alpha", env.From.Agent);
        Assert.Equal(MailPriority.Urgent, env.Priority);
        Assert.Equal(MailKind.Alert, env.Kind);
        Assert.Contains("Re-read the parser", env.Body);
        // `ts` is absent at the sender and stamped by the verb (ADR-0016 d2).
        Assert.False(string.IsNullOrWhiteSpace(env.Ts));
    }

    /// THE MUTATION that gives the test above its meaning: strip the guard
    /// from a copy and the stub refuses. The member must still not break its
    /// agent — it degrades to the ungarnished handoff, because losing the
    /// WARNING because the prose was unavailable is the wrong way to fail.
    ///
    /// If this test ever passes with the model's words present, the stub has
    /// stopped enforcing and the proof above is worthless.
    [Fact]
    public async Task GuardStripped_StubRefuses_AndTheMemberDegradesWithoutModelWords()
    {
        WriteStubClaude();
        SeedOverlap();

        var shipped = File.ReadAllText(
            Path.Combine(DogfoodInfra.PayloadsDir(), "starter-mail-watcher.sh"));
        Assert.Contains("--setting-sources \"\"", shipped);      // the mutation must have a target

        var stripped = Path.Combine(_tmp.Path, "watcher-no-guard.sh");
        File.WriteAllText(stripped, shipped.Replace("-p --setting-sources \"\"", "-p"));
        File.SetUnixFileMode(stripped,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var (exit, stdout, stderr) = await RunWatcherAsync(stripped);

        Assert.Equal(0, exit);                                   // fail-open: the agent is untouched
        Assert.Contains("\"effect\":\"noop\"", stdout);
        Assert.Contains("model gave nothing", stderr);           // the refusal was noticed and said

        var env = Assert.Single(Delivered());
        Assert.DoesNotContain("Re-read the parser", env.Body);   // the stub never answered
        Assert.Contains("/repo/src/parser.cs", env.Body);        // but the overlap still shipped
    }

    /// The on-demand discipline: no overlap ⇒ the model is never spawned and
    /// nothing reaches the bus. A member that wakes a model every turn taxes
    /// every turn, so "cheap when there is nothing to say" is a contract.
    [Fact]
    public async Task NoOverlap_NeverSpawnsTheModel_AndWritesNoMail()
    {
        // A stub that FAILS unconditionally: if the gate is broken and the
        // model is reached at all, the degrade path would still write mail —
        // so the assertion below (no mail at all) is what proves it was not.
        WriteStubClaude();
        File.WriteAllText(Path.Combine(_views, "alpha.edits"), "/repo/src/parser.cs\n");
        File.WriteAllText(Path.Combine(_views, "beta"), "/repo/docs/unrelated.md\n");

        var (exit, stdout, _) = await RunWatcherAsync(
            Path.Combine(DogfoodInfra.PayloadsDir(), "starter-mail-watcher.sh"));

        Assert.Equal(0, exit);
        Assert.Contains("\"effect\":\"noop\"", stdout);
        Assert.False(Directory.Exists(Path.Combine(_home, ".captainHook", "mail")),
            "a gate that never opened must not have written mail");
    }
}

/// The hub claim, end to end: two agent loops, one daemon, mail that reaches
/// the OTHER agent and not the sender. This is the first dogfood target named
/// in the roadmap — both agents' PostToolUse streams into one edit log with
/// stale-view warnings — driven through real spawned payloads.
public class MailSwarmDaemonSmokeTests : IDisposable
{
    private readonly TempRuntimeDir _tmp = new();
    private readonly string _home, _alphaProj, _betaProj;

    public MailSwarmDaemonSmokeTests()
    {
        Directory.CreateDirectory(_tmp.Path);
        _home = Path.Combine(_tmp.Path, "home");
        Directory.CreateDirectory(Path.Combine(_home, ".captainHook"));
        _alphaProj = Path.Combine(_tmp.Path, "alpha-repo");
        _betaProj = Path.Combine(_tmp.Path, "beta-repo");
        Directory.CreateDirectory(_alphaProj);
        Directory.CreateDirectory(_betaProj);
        ChildRecords.OverrideDir = Path.Combine(_tmp.Path, "children");
    }

    public void Dispose()
    {
        ChildRecords.OverrideDir = null;
        _tmp.Dispose();
    }

    private object Observer(string role, string peer) => new
    {
        name = $"mail-observer-{role}",
        command = Path.Combine(DogfoodInfra.PayloadsDir(), "starter-mail-observer.sh"),
        events = new[] { "PostToolUse" },
        mode = "resident",
        failMode = "open",
        budgetMs = 10000,
        readinessTimeoutMs = 15000,
        env = new Dictionary<string, string>
        {
            ["HOME"] = _home,
            ["MAIL_ROLE"] = role,
            ["MAIL_PEER"] = peer,
            ["CAPTAINHOOK_BIN"] = DogfoodInfra.EngineBin(),
            ["DOTNET_ROOT"] = DogfoodInfra.DotnetRoot(),
        },
    };

    private object Digest(string role) => new
    {
        name = $"mail-digest-{role}",
        command = DogfoodInfra.EngineBin(),
        args = new[] { "mail", "digest", "--role", role, "--seam", "ambient" },
        events = new[] { "UserPromptSubmit" },
        mode = "oneshot",
        failMode = "open",
        budgetMs = 20000,
        env = new Dictionary<string, string>
        {
            ["HOME"] = _home,
            ["DOTNET_ROOT"] = DogfoodInfra.DotnetRoot(),
        },
    };

    private string WriteHandlers()
    {
        var handlers = new
        {
            version = 1,
            handlers = new object[]
            {
                Observer("alpha", "beta"), Observer("beta", "alpha"),
                Digest("alpha"), Digest("beta"),
            },
        };
        var path = Path.Combine(_tmp.Path, "handlers.json");
        File.WriteAllText(path, JsonSerializer.Serialize(handlers));
        return path;
    }

    /// ADR-0016's swarm activation, as DATA: handler-named rules AND a project
    /// path-prefix, so each member runs only in its own agent's window. Without
    /// this, both roles' members fire for both agents and every observer
    /// reports the same role.
    private string WritePolicy()
    {
        var policy = new
        {
            version = 1,
            @default = "allow",
            rules = new object[]
            {
                new { handler = "mail-observer-alpha", project = _betaProj, decision = "deny" },
                new { handler = "mail-digest-alpha", project = _betaProj, decision = "deny" },
                new { handler = "mail-observer-beta", project = _alphaProj, decision = "deny" },
                new { handler = "mail-digest-beta", project = _alphaProj, decision = "deny" },
            },
        };
        var path = Path.Combine(_tmp.Path, "dispatch.json");
        File.WriteAllText(path, JsonSerializer.Serialize(policy));
        return path;
    }

    private static string Body(ForwardOutcome o) =>
        Encoding.UTF8.GetString(Assert.IsType<ForwardOutcome.Answered>(o).StdoutBytes);

    private static byte[] Tool(string tool, string filePath, string cwd, string session) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            session_id = session,
            cwd,
            tool_name = tool,
            tool_input = new { file_path = filePath },
        }));

    [Fact]
    public async Task TwoAgents_OneDaemon_PeerHearsTheStaleView_AndTheSenderDoesNot()
    {
        if (!ProcessGroup.Prefix.Pgroup) return;   // xunit 2.x: no dynamic skip

        var shared = Path.Combine(_tmp.Path, "shared", "parser.cs");
        var handlersPath = WriteHandlers();
        var policyPath = WritePolicy();

        using var stop = new CancellationTokenSource();
        var daemon = Task.Run(() => DaemonHost.RunAsync(
            _tmp.Paths, NoHarnessDir(), stop.Token,
            policyPath: policyPath, handlersPath: handlersPath));
        await PollUntilAsync(async () =>
            await ShimClient.TryForwardAsync(_tmp.Paths.SocketPath,
                new HookRequest("warmup00", "session-start", "claude-code", "{}"u8.ToArray()))
                is ForwardOutcome.Answered,
            TimeSpan.FromSeconds(20), "daemon up");

        // --- agent BETA reads the shared file ------------------------------
        // Its observer records the path in beta's view. Poll: the resident
        // child may still be reaching {"ready":1} on the first tool call.
        await PollUntilAsync(async () =>
        {
            await ShimClient.TryForwardAsync(_tmp.Paths.SocketPath,
                new HookRequest("beta-read", "post-tool-use", "claude-code",
                    Tool("Read", shared, _betaProj, "s-beta")));
            var view = Path.Combine(_home, ".captainHook", "observer-views", "beta");
            return File.Exists(view) && File.ReadAllText(view).Contains(shared);
        }, TimeSpan.FromSeconds(30), "beta's observer recorded the read");

        // --- agent ALPHA edits the same file -------------------------------
        // Only alpha's observer runs here (policy denied beta's in this
        // project), and it is the only party that can see BOTH facts.
        await ShimClient.TryForwardAsync(_tmp.Paths.SocketPath,
            new HookRequest("alpha-edit", "post-tool-use", "claude-code",
                Tool("Edit", shared, _alphaProj, "s-alpha")));

        var store = new MailStore(Path.Combine(_home, ".captainHook", "mail"));
        await PollUntilAsync(() =>
            Task.FromResult(store.Read().Any(l => l.Envelope.To == "beta")),
            TimeSpan.FromSeconds(30), "the stale-view alert reached the bus");

        var alert = Assert.Single(store.Read().Select(l => l.Envelope));
        Assert.Equal("beta", alert.To);                       // addressed to the PEER
        Assert.Equal("alpha", alert.From.Agent);
        Assert.Equal("s-alpha", alert.From.Session);
        Assert.Equal(MailPriority.Urgent, alert.Priority);    // escalated: beta is holding it
        Assert.Equal(MailKind.Alert, alert.Kind);
        Assert.Contains("stale view", alert.Topic);
        Assert.Contains(shared, alert.Body);

        // --- beta's next turn start reads it -------------------------------
        var betaPrompt = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new { session_id = "s-beta", cwd = _betaProj }));
        var betaFirst = Body(await ShimClient.TryForwardAsync(_tmp.Paths.SocketPath,
            new HookRequest("beta-p1", "user-prompt-submit", "claude-code", betaPrompt)));
        Assert.Contains("captAInHook mail", betaFirst);
        Assert.Contains("stale view", betaFirst);
        Assert.Contains("alpha", betaFirst);                  // provenance names the sender

        // THE HUB CLAIM'S OTHER HALF: alpha must NOT be told about its own
        // edit. Peer-addressing is what prevents it — a shared role would
        // deliver every member its own traffic.
        var alphaPrompt = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new { session_id = "s-alpha", cwd = _alphaProj }));
        var alphaFirst = Body(await ShimClient.TryForwardAsync(_tmp.Paths.SocketPath,
            new HookRequest("alpha-p1", "user-prompt-submit", "claude-code", alphaPrompt)));
        Assert.DoesNotContain("captAInHook mail", alphaFirst);
        Assert.DoesNotContain("stale view", alphaFirst);

        // Exactly once, across a process boundary and on real files.
        var betaSecond = Body(await ShimClient.TryForwardAsync(_tmp.Paths.SocketPath,
            new HookRequest("beta-p2", "user-prompt-submit", "claude-code", betaPrompt)));
        Assert.DoesNotContain("stale view", betaSecond);

        // The chain the whole store rests on still verifies after two real
        // members wrote to it through the daemon.
        Assert.Empty(store.VerifyChain());   // no chain faults after live traffic

        stop.Cancel();
        Assert.Equal(0, await daemon.WaitAsync(TimeSpan.FromSeconds(20)));
    }

    /// The unescalated half: an edit to a file NOBODY is holding is still
    /// reported, but ambient — the bus stays useful without becoming a
    /// mid-turn interrupt for every write.
    [Fact]
    public async Task EditNobodyIsHolding_IsAmbient_NotUrgent()
    {
        if (!ProcessGroup.Prefix.Pgroup) return;

        var handlersPath = WriteHandlers();
        var policyPath = WritePolicy();
        using var stop = new CancellationTokenSource();
        var daemon = Task.Run(() => DaemonHost.RunAsync(
            _tmp.Paths, NoHarnessDir(), stop.Token,
            policyPath: policyPath, handlersPath: handlersPath));
        await PollUntilAsync(async () =>
            await ShimClient.TryForwardAsync(_tmp.Paths.SocketPath,
                new HookRequest("warmup00", "session-start", "claude-code", "{}"u8.ToArray()))
                is ForwardOutcome.Answered,
            TimeSpan.FromSeconds(20), "daemon up");

        var lonely = Path.Combine(_tmp.Path, "shared", "nobody-reads-this.md");
        var store = new MailStore(Path.Combine(_home, ".captainHook", "mail"));
        await PollUntilAsync(async () =>
        {
            await ShimClient.TryForwardAsync(_tmp.Paths.SocketPath,
                new HookRequest("alpha-edit2", "post-tool-use", "claude-code",
                    Tool("Write", lonely, _alphaProj, "s-alpha")));
            return Directory.Exists(Path.Combine(_home, ".captainHook", "mail"))
                && store.Read().Count > 0;
        }, TimeSpan.FromSeconds(30), "the ambient notice reached the bus");

        var note = store.Read().Select(l => l.Envelope).First();
        Assert.Equal(MailPriority.Ambient, note.Priority);
        Assert.Equal(MailKind.Status, note.Kind);
        Assert.Contains("edited", note.Topic);

        stop.Cancel();
        Assert.Equal(0, await daemon.WaitAsync(TimeSpan.FromSeconds(20)));
    }
}
