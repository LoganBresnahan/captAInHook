using CaptainHook.Actors;
using CaptainHook.Wire;

namespace CaptainHook.Tests;

// The golden cross-emitter test (ADR-0004 decision 7 amendment,
// wire-jsonl-logger): the trail has ONE schema and two emitters — F#
// LogEvent.ToJson() (engine) and WireJsonl.Render (AOT shim). This suite is
// the only place that sees both assemblies, so it is where the schema is
// pinned: the same event through both renderers must produce IDENTICAL BYTES.
// A red test here means an emitter moved alone; move both in one commit.

public class WireJsonlTests
{
    private static readonly DateTime Ts = new(2026, 7, 6, 3, 4, 5, 789, DateTimeKind.Utc);

    /// Build the SAME event in both worlds and render both ways.
    private static (string FSharp, string Wire) RenderBoth(
        string lvl, string src, string evt,
        string? dispatchId = null, string? sessionId = null, string? hookEvent = null,
        string? actorId = null, double? durMs = null, string? msg = null,
        Dictionary<string, object>? data = null)
    {
        var ff = new LogFields
        {
            DispatchId = dispatchId!, SessionId = sessionId!, HookEvent = hookEvent!,
            ActorId = actorId!, DurMs = durMs ?? default(double?), Msg = msg!, Data = data!,
        };
        var fsharp = new LogEvent(Ts, lvl, src, evt, ff).ToJson();

        var wf = new WireLogFields
        {
            DispatchId = dispatchId, SessionId = sessionId, HookEvent = hookEvent,
            ActorId = actorId, DurMs = durMs, Msg = msg, Data = data,
        };
        var wire = WireJsonl.Render(new WireLogEvent(Ts, lvl, src, evt, wf));

        return (fsharp, wire);
    }

    [Fact]
    public void MinimalEvent_IdenticalBytes()
    {
        var (fsharp, wire) = RenderBoth("info", "shim", "shim.answered");
        Assert.Equal(fsharp, wire);
        Assert.StartsWith("{\"ts\":\"2026-07-06T03:04:05.789Z\"", wire);   // and the schema is what we think
    }

    [Fact]
    public void EveryField_IdenticalBytes()
    {
        var (fsharp, wire) = RenderBoth("warn", "shim", "shim.fallback",
            dispatchId: "abc12345", sessionId: "s-1", hookEvent: "UserPromptSubmit",
            actorId: "echo-1", durMs: 13.4, msg: "connect: ConnectionRefused",
            data: new Dictionary<string, object> { ["exit"] = 0, ["stdoutBytes"] = 42 });
        Assert.Equal(fsharp, wire);
    }

    [Theory]
    [InlineData(13.4449999)]   // rounds to 13.445
    [InlineData(2.0)]          // renders as 2, not 2.0
    [InlineData(99.9999)]      // rounds to 100
    [InlineData(1.2345)]       // midpoint: both sides must use the same Math.Round
    [InlineData(0.0004)]       // rounds to 0
    public void DurMsRounding_IdenticalBytes(double durMs)
    {
        var (fsharp, wire) = RenderBoth("info", "shim", "shim.answered", durMs: durMs);
        Assert.Equal(fsharp, wire);
    }

    [Theory]
    [InlineData("café ✓ — naïve")]                    // non-ASCII: default encoder escapes both sides
    [InlineData("say \"hi\" \\ back")]                // quotes + backslash
    [InlineData("line1\nline2\ttabbed")]              // control whitespace
    [InlineData("<script>&amp;</script>")]            // HTML-sensitive chars
    [InlineData("nulbyte")]                     // low control char
    [InlineData("")]                                  // empty is present, not omitted
    public void MsgEscaping_IdenticalBytes(string msg)
    {
        var (fsharp, wire) = RenderBoth("error", "shim", "shim.deliveryFailed", msg: msg);
        Assert.Equal(fsharp, wire);
    }

    /// `mail.append`'s provenance field set (ADR-0016 d14) as ONE golden line.
    /// Only the engine emits mail events today — the shim has no mail code and
    /// aot-boundary rule 1 keeps it that way — but the trail is ONE SCHEMA
    /// across two emitters, and a consumer (the mail canvas's reducer) is about
    /// to read these keys by name. So the shape is pinned literally here, where
    /// a field rename shows up as a byte diff, rather than trusted to the call
    /// site. The `from` object is the nested-dict wire case in its real use;
    /// there is deliberately no `body` key to pin — see MailAppendProvenanceTests.
    [Fact]
    public void MailAppendProvenance_IdenticalBytes_AndCarriesNoBody()
    {
        var (fsharp, wire) = RenderBoth("debug", "mail", "mail.append",
            data: new Dictionary<string, object>
            {
                ["id"] = "m-01",
                ["to"] = "reviewer",
                ["offset"] = 4_096L,
                ["bytes"] = 233,
                ["from"] = new Dictionary<string, object>
                {
                    ["agent"] = "intent-watcher", ["harness"] = "claude-code", ["session"] = "s-77",
                },
                ["kind"] = "status",
                ["topic"] = "build",
                ["priority"] = "urgent",
                ["ttlDeliveries"] = 3,
            });

        Assert.Equal(fsharp, wire);
        Assert.Equal(
            """
            {"ts":"2026-07-06T03:04:05.789Z","lvl":"debug","src":"mail","evt":"mail.append","data":{"id":"m-01","to":"reviewer","offset":4096,"bytes":233,"from":{"agent":"intent-watcher","harness":"claude-code","session":"s-77"},"kind":"status","topic":"build","priority":"urgent","ttlDeliveries":3}}
            """,
            wire);
        Assert.DoesNotContain("body", wire);
    }

    /// The cursor's side of the choreography (ADR-0016 d14, slice
    /// `mail-reducer`): `mail.cursorAdvance` and `mail.expire` name the cursor
    /// they are about — the session on the trail's first-class `sessionId`
    /// column (d10's rule for `mail.deliver`), the role in data — and carry
    /// OFFSETS beside counts and ids, because ids are not unique on the bus
    /// and a count cannot say which. A reducer reproduces the digest's held
    /// set from exactly these keys; pinned literally so a rename is a byte
    /// diff here, not a cursor that quietly stops moving on a canvas.
    [Fact]
    public void MailCursorAdvance_IdenticalBytes_NamesTheCursorAndItsOffsets()
    {
        var (fsharp, wire) = RenderBoth("debug", "mail", "mail.cursorAdvance",
            sessionId: "s-1",
            data: new Dictionary<string, object>
            {
                ["role"] = "main",
                ["offset"] = 812L,
                ["delivered"] = 2,
                ["deliveredOffsets"] = new List<long> { 0, 406 },
                ["held"] = 1,
                ["expired"] = 0,
                ["deliveries"] = 4L,
            });

        Assert.Equal(fsharp, wire);
        Assert.Equal(
            """
            {"ts":"2026-07-06T03:04:05.789Z","lvl":"debug","src":"mail","evt":"mail.cursorAdvance","sessionId":"s-1","data":{"role":"main","offset":812,"delivered":2,"deliveredOffsets":[0,406],"held":1,"expired":0,"deliveries":4}}
            """,
            wire);
    }

    [Fact]
    public void MailExpire_IdenticalBytes_NamesTheCursorAndTheOffset()
    {
        var (fsharp, wire) = RenderBoth("info", "mail", "mail.expire",
            sessionId: "s-1",
            msg: "mail expired undelivered: passed over at its full ttlDeliveries of opportunities",
            data: new Dictionary<string, object>
            {
                ["id"] = "m-amb", ["to"] = "main", ["offset"] = 203L,
                ["ttlDeliveries"] = 2, ["seenAt"] = 1L,
            });

        Assert.Equal(fsharp, wire);
        Assert.Equal(
            """
            {"ts":"2026-07-06T03:04:05.789Z","lvl":"info","src":"mail","evt":"mail.expire","sessionId":"s-1","msg":"mail expired undelivered: passed over at its full ttlDeliveries of opportunities","data":{"id":"m-amb","to":"main","offset":203,"ttlDeliveries":2,"seenAt":1}}
            """,
            wire);
    }

    /// `mail.reap` (ADR-0018 d6, slice `reap-verb`) — the row that says a
    /// mailbox's standing ended. It names the mailbox in the SAME two columns
    /// `mail.cursorAdvance` and `mail.deliver` use (`role`, plus `instance`
    /// when the address has one), never a joined `address`, so a reader that
    /// learned the advance's spelling of "which mailbox" needs nothing new.
    /// There is no `sessionId`: a reap is performed on an address, not by a
    /// window — `by` names who decided, and both it and `instance` are absent
    /// rather than blank when there is nothing to say.
    [Fact]
    public void MailReap_IdenticalBytes_NamesTheMailboxAndWhatItStranded()
    {
        var (fsharp, wire) = RenderBoth("info", "mail", "mail.reap",
            msg: "mailbox reaped: its standing is gone, its mail stays on the ledger",
            data: new Dictionary<string, object>
            {
                ["role"] = "main",
                ["pendingIds"] = new List<string> { "m-02", "m-03" },
                ["instance"] = "laptop-a",
                ["by"] = "reaper@daemon",
            });

        Assert.Equal(fsharp, wire);
        Assert.Equal(
            """
            {"ts":"2026-07-06T03:04:05.789Z","lvl":"info","src":"mail","evt":"mail.reap","msg":"mailbox reaped: its standing is gone, its mail stays on the ledger","data":{"role":"main","pendingIds":["m-02","m-03"],"instance":"laptop-a","by":"reaper@daemon"}}
            """,
            wire);
    }

    [Fact]
    public void DataValueKinds_IdenticalBytes()
    {
        // The wire contract's data value set: primitives, nested dict, sequence.
        var (fsharp, wire) = RenderBoth("info", "daemon", "daemon.listening",
            data: new Dictionary<string, object>
            {
                ["string"] = "value with ✓",
                ["int"] = 42,
                ["long"] = 5_000_000_000L,
                ["double"] = 99.5,
                ["bool"] = true,
                ["null"] = null!,
                ["nested"] = new Dictionary<string, object> { ["k"] = "v", ["n"] = 7 },
                ["seq"] = new object[] { "a", 1, false },
            });
        Assert.Equal(fsharp, wire);
    }

    [Fact]
    public void EmptyData_OmittedByBothSides()
    {
        var (fsharp, wire) = RenderBoth("info", "shim", "shim.answered",
            data: new Dictionary<string, object>());
        Assert.Equal(fsharp, wire);
        Assert.DoesNotContain("data", wire);
    }

    [Fact]
    public void DefaultLogPath_MirrorsTheFSharpResolution()
    {
        // Same env var, same fallback — shim and engine append to ONE file.
        // (CAPTAINHOOK_LOG is unset under the test runner unless a test sets
        // it; guard by setting it explicitly and restoring.)
        var prior = Environment.GetEnvironmentVariable("CAPTAINHOOK_LOG");
        try
        {
            Environment.SetEnvironmentVariable("CAPTAINHOOK_LOG", "/tmp/x/trail.jsonl");
            Assert.Equal("/tmp/x/trail.jsonl", WireJsonl.DefaultLogPath());

            Environment.SetEnvironmentVariable("CAPTAINHOOK_LOG", null);
            Assert.Equal(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".captainHook", "logs", "captainHook.jsonl"),
                WireJsonl.DefaultLogPath());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CAPTAINHOOK_LOG", prior);
        }
    }

    [Fact]
    public void Append_WritesOneLine_AndSurvivesUnwritablePaths()
    {
        var dir = Path.Combine(Path.GetTempPath(), "chk-wirejsonl-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var path = Path.Combine(dir, "nested", "trail.jsonl");   // dir does not exist: Append creates it
            WireJsonl.Append(path, """{"ts":"x"}""");
            WireJsonl.Append(path, """{"ts":"y"}""");
            Assert.Equal(new[] { """{"ts":"x"}""", """{"ts":"y"}""" }, File.ReadAllLines(path));

            // Unwritable: swallowed, never thrown — logging is never the hook's problem.
            WireJsonl.Append("/proc/definitely/not/writable/trail.jsonl", "{}");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }
}
