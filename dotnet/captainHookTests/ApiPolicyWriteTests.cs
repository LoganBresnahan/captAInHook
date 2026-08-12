using System.Net;
using System.Text;
using System.Text.Json;
using CaptainHook.Api;
using CaptainHook.Core;
using CaptainHook.Wire;
using static CaptainHook.Tests.TestUtil;

namespace CaptainHook.Tests;

// put-policy-write (ADR-0007 decision 4, Phase 6): the API as EDITOR OF THE
// FILE. Two layers, tested separately:
//   * ApiPolicyWriter — the sharp core: strict validation (reuse of the daemon's
//     own parser), If-Match preconditions, and the ATOMIC temp+rename in the
//     target's OWN directory. Driven directly, no HTTP.
//   * PUT /api/v1/policy — the HTTP shell: the tri-state → status mapping
//     (200/422/412/413), the ETag round-trip, and the inherited auth gate.
// The adversarial-verify surface the ADR names is atomicity + the mapping; the
// atomicity test drives a concurrent reader against the exact hazard (a hook
// stat-gating the file mid-write) rather than trusting the happy path.
public class ApiPolicyWriteTests
{
    private const string ValidPolicy =
        """{"version":1,"default":"allow","rules":[{"event":"user-prompt-submit","decision":"deny"}]}""";

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    // A fresh empty directory under /tmp; the caller deletes it. Mirrors the
    // policy-file idiom in ApiReadEndpointsTests (nothing under ~/.captainHook is
    // ever touched).
    private static string FreshDir()
    {
        var dir = Path.Combine("/tmp", "chk-polw-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ---- ApiPolicyWriter: validation ---------------------------------------

    [Fact]
    public void Write_ValidPolicy_InstallsFile_AndReturnsRoundTripEtag()
    {
        var dir = FreshDir();
        var path = Path.Combine(dir, "dispatch.json");
        try
        {
            var outcome = new ApiPolicyWriter(path).Write(Utf8(ValidPolicy), ifMatch: null);

            var written = Assert.IsType<PolicyWriteOutcome.Written>(outcome);
            Assert.True(File.Exists(path));
            Assert.Equal(ValidPolicy, File.ReadAllText(path));
            // The returned tag is exactly what a subsequent GET computes over the
            // file it reads back — the round-trip the If-Match chain depends on.
            Assert.Equal(ApiReadModel.Etag(File.ReadAllText(path)), written.Etag);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Write_MalformedJson_Invalid_FileUntouched()
    {
        var dir = FreshDir();
        var path = Path.Combine(dir, "dispatch.json");
        File.WriteAllText(path, ValidPolicy);   // a prior good file
        try
        {
            var outcome = new ApiPolicyWriter(path).Write(Utf8("{ this is not json "), null);

            Assert.IsType<PolicyWriteOutcome.Invalid>(outcome);
            Assert.Equal(ValidPolicy, File.ReadAllText(path));   // untouched — never write junk
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Write_WellFormedButInvalidPolicy_Invalid_CarriesViolations()
    {
        var dir = FreshDir();
        var path = Path.Combine(dir, "dispatch.json");
        try
        {
            // Valid JSON, wrong version — the daemon would refuse to load it, so
            // the API refuses to write it.
            var outcome = new ApiPolicyWriter(path).Write(Utf8("""{"version":2}"""), null);

            var inv = Assert.IsType<PolicyWriteOutcome.Invalid>(outcome);
            Assert.Contains(inv.Violations, v => v.Contains("version"));
            Assert.False(File.Exists(path));   // nothing written
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Write_LoneSurrogateEscape_Invalid_Never500()
    {
        var dir = FreshDir();
        var path = Path.Combine(dir, "dispatch.json");
        try
        {
            // "\ud800" is valid UTF-8 BYTES (the escape is ASCII) and parses as a
            // document — the unreadable string only surfaces at GetString inside
            // TryParse. Must land as a 422-shaped Invalid, not escape Write as an
            // InvalidOperationException → opaque 500 (skeptic pass, 2026-08-11).
            var outcome = new ApiPolicyWriter(path).Write(
                Utf8("""{ "version": 1, "rules": [ { "session": "\ud800", "decision": "deny" } ] }"""), null);

            var inv = Assert.IsType<PolicyWriteOutcome.Invalid>(outcome);
            Assert.Contains(inv.Violations, v => v.Contains("session"));
            Assert.False(File.Exists(path));   // nothing written
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Write_InvalidUtf8_Invalid()
    {
        var dir = FreshDir();
        var path = Path.Combine(dir, "dispatch.json");
        try
        {
            var outcome = new ApiPolicyWriter(path).Write(new byte[] { 0xff, 0xfe, 0x00 }, null);

            var inv = Assert.IsType<PolicyWriteOutcome.Invalid>(outcome);
            Assert.Contains(inv.Violations, v => v.Contains("UTF-8"));
            Assert.False(File.Exists(path));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Write_BomPrefixedBody_Accepted_WritesNoBom_RoundTripEtag()
    {
        // The daemon's loader (File.ReadAllText) strips a leading BOM, so it would
        // happily load a BOM'd file — the writer must agree, not 422. And the
        // written file must be BOM-free so GET-etag == PUT-etag round-trips.
        // (put-policy-write adversarial verify, 2026-07-08.)
        var dir = FreshDir();
        var path = Path.Combine(dir, "dispatch.json");
        try
        {
            var raw = Utf8(ValidPolicy);
            var bytes = new byte[3 + raw.Length];
            bytes[0] = 0xEF; bytes[1] = 0xBB; bytes[2] = 0xBF;   // UTF-8 BOM
            Array.Copy(raw, 0, bytes, 3, raw.Length);

            var w = Assert.IsType<PolicyWriteOutcome.Written>(new ApiPolicyWriter(path).Write(bytes, null));

            Assert.NotEqual(0xEF, File.ReadAllBytes(path)[0]);            // no BOM on disk
            Assert.Equal(ValidPolicy, File.ReadAllText(path));           // exactly the policy
            Assert.Equal(w.Etag, ApiReadModel.Etag(File.ReadAllText(path)));   // round-trip
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ---- ApiPolicyWriter: If-Match preconditions ---------------------------

    [Fact]
    public void Write_IfMatch_CurrentTag_Writes()
    {
        var dir = FreshDir();
        var path = Path.Combine(dir, "dispatch.json");
        File.WriteAllText(path, ValidPolicy);
        var etag = ApiReadModel.Etag(ValidPolicy);
        try
        {
            var next = """{"version":1,"default":"deny"}""";
            var outcome = new ApiPolicyWriter(path).Write(Utf8(next), ifMatch: etag);

            Assert.IsType<PolicyWriteOutcome.Written>(outcome);
            Assert.Equal(next, File.ReadAllText(path));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Write_IfMatch_StaleTag_Mismatch_FileUntouched()
    {
        var dir = FreshDir();
        var path = Path.Combine(dir, "dispatch.json");
        File.WriteAllText(path, ValidPolicy);
        try
        {
            var outcome = new ApiPolicyWriter(path).Write(
                Utf8("""{"version":1,"default":"deny"}"""), ifMatch: "\"stale00000000000000000000000000000000\"");

            var m = Assert.IsType<PolicyWriteOutcome.Mismatch>(outcome);
            Assert.Equal(ApiReadModel.Etag(ValidPolicy), m.Current);   // the tag now on disk
            Assert.Equal(ValidPolicy, File.ReadAllText(path));         // not overwritten
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Write_IfMatch_Star_OnExisting_Writes_OnAbsent_Mismatches()
    {
        var dir = FreshDir();
        var present = Path.Combine(dir, "present.json");
        var absent = Path.Combine(dir, "absent.json");
        File.WriteAllText(present, ValidPolicy);
        try
        {
            // "*" = "if the resource exists".
            Assert.IsType<PolicyWriteOutcome.Written>(
                new ApiPolicyWriter(present).Write(Utf8(ValidPolicy), ifMatch: "*"));
            var m = Assert.IsType<PolicyWriteOutcome.Mismatch>(
                new ApiPolicyWriter(absent).Write(Utf8(ValidPolicy), ifMatch: "*"));
            Assert.Null(m.Current);
            Assert.False(File.Exists(absent));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Write_IfMatch_ConcreteTag_OnAbsentFile_Mismatch()
    {
        var dir = FreshDir();
        var path = Path.Combine(dir, "dispatch.json");   // never created
        try
        {
            var outcome = new ApiPolicyWriter(path).Write(Utf8(ValidPolicy), ifMatch: ApiReadModel.Etag(ValidPolicy));

            var m = Assert.IsType<PolicyWriteOutcome.Mismatch>(outcome);
            Assert.Null(m.Current);
            Assert.False(File.Exists(path));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Write_NoIfMatch_OverwritesBlindly()
    {
        // Guarded, not locked (d4): If-Match is honored WHEN SUPPLIED; without it
        // a PUT is a blind overwrite, exactly like a hand-edit.
        var dir = FreshDir();
        var path = Path.Combine(dir, "dispatch.json");
        File.WriteAllText(path, ValidPolicy);
        try
        {
            var next = """{"version":1,"default":"deny"}""";
            Assert.IsType<PolicyWriteOutcome.Written>(new ApiPolicyWriter(path).Write(Utf8(next), null));
            Assert.Equal(next, File.ReadAllText(path));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ---- ApiPolicyWriter: the atomic write (the ADR's named trap) -----------

    [Fact]
    public void Write_CreatesTargetDirectory_WhenMissing()
    {
        var dir = FreshDir();
        var path = Path.Combine(dir, "nested", "sub", "dispatch.json");   // dirs don't exist
        try
        {
            Assert.IsType<PolicyWriteOutcome.Written>(new ApiPolicyWriter(path).Write(Utf8(ValidPolicy), null));
            Assert.True(File.Exists(path));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Write_LeavesNoTempLitter_AcrossManyWrites()
    {
        var dir = FreshDir();
        var path = Path.Combine(dir, "dispatch.json");
        var writer = new ApiPolicyWriter(path);
        try
        {
            for (var i = 0; i < 25; i++)
                Assert.IsType<PolicyWriteOutcome.Written>(
                    writer.Write(Utf8($$"""{"version":1,"rules":[{"session":"s{{i}}","decision":"deny"}]}"""), null));

            // The dir holds ONLY the target — the sibling temp is renamed away or
            // cleaned on every write, never accumulated.
            Assert.Equal(new[] { path }, Directory.GetFiles(dir));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Write_IsAtomic_ConcurrentReaderNeverSeesTornOrAbsent()
    {
        // The hazard the ADR names: a hook resolving the policy WHILE a PUT lands.
        // A non-atomic write (truncate-in-place, or a cross-device copy+delete)
        // would flash a torn (Malformed) or missing (Absent) file and transiently
        // Noop/deny every hook. rename(2) — a same-dir Move — forbids that: a
        // reader sees the old whole file or the new whole file, never a seam.
        var dir = FreshDir();
        var path = Path.Combine(dir, "dispatch.json");
        File.WriteAllText(path, ValidPolicy);   // present from t0 — every read must stay Loaded
        var writer = new ApiPolicyWriter(path);
        try
        {
            var stop = false;
            var offenders = new System.Collections.Concurrent.ConcurrentBag<string>();
            var reader = Task.Run(() =>
            {
                while (!Volatile.Read(ref stop))
                {
                    var res = PolicyResolution.Resolve(path);
                    if (res is not PolicyResolution.Loaded) offenders.Add(res.GetType().Name);
                }
            });

            for (var i = 0; i < 400; i++)
                writer.Write(Utf8($$"""{"version":1,"default":"{{(i % 2 == 0 ? "allow" : "deny")}}"}"""), null);

            Volatile.Write(ref stop, true);
            await reader;

            Assert.True(offenders.IsEmpty,
                $"a concurrent reader observed a non-Loaded state mid-write: {string.Join(",", offenders)}");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ---- PUT /api/v1/policy: the HTTP mapping -------------------------------

    // A daemon-less ApiHost with a real writer (and a matching read model) over a
    // file path — the daemon wiring itself is covered elsewhere; this pins the
    // route + status mapping.
    private static ApiHost HostFor(string policyPath) =>
        ApiHost.Start(FreeTcpPort(),
            readModel: new ApiReadModel("testver", new ServeStats(),
                new Dispatcher(new Registry(), TimeSpan.FromSeconds(2)),
                new ReloadingHarnessRegistry(NoHarnessDir()), new ReloadingPolicy(policyPath), policyPath,
                clock: () => 6000, startTick: 1000),
            writer: new ApiPolicyWriter(policyPath));

    [Fact]
    public async Task Put_ValidPolicy_200_WithEtagHeader_ThenGetReflectsIt()
    {
        var dir = FreshDir();
        var path = Path.Combine(dir, "dispatch.json");
        try
        {
            using var api = HostFor(path);
            var (status, body, etag) = await ApiPutAsync(api.Port, api.Token, "/api/v1/policy", ValidPolicy);

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.False(string.IsNullOrEmpty(etag));
            Assert.Equal("loaded", JsonDocument.Parse(body).RootElement.GetProperty("state").GetString());

            // Editor-of-the-file: the write is effective on the next read via the
            // SAME stat-gated resolver the dispatch path uses.
            var (gs, gbody) = await ApiGetAsync(api.Port, api.Token, "/api/v1/policy");
            var g = JsonDocument.Parse(gbody).RootElement;
            Assert.Equal(HttpStatusCode.OK, gs);
            Assert.Equal("loaded", g.GetProperty("state").GetString());
            Assert.Equal(etag, g.GetProperty("etag").GetString());   // header ↔ body round-trip
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Put_MalformedPolicy_422_WithViolations()
    {
        var dir = FreshDir();
        var path = Path.Combine(dir, "dispatch.json");
        try
        {
            using var api = HostFor(path);
            var (status, body, _) = await ApiPutAsync(api.Port, api.Token, "/api/v1/policy", """{"version":2}""");

            Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
            var r = JsonDocument.Parse(body).RootElement;
            Assert.Equal("invalid_policy", r.GetProperty("error").GetString());
            Assert.NotEmpty(r.GetProperty("violations").EnumerateArray());
            Assert.False(File.Exists(path));   // refused, nothing written
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Put_IfMatch_Mismatch_412_ThenCorrectTag_200()
    {
        var dir = FreshDir();
        var path = Path.Combine(dir, "dispatch.json");
        try
        {
            using var api = HostFor(path);
            // Seed a file through the API so we hold its real ETag.
            var (_, _, etag) = await ApiPutAsync(api.Port, api.Token, "/api/v1/policy", ValidPolicy);

            // A stale If-Match is refused with 412 and does not overwrite.
            var (bad, _, _) = await ApiPutAsync(api.Port, api.Token, "/api/v1/policy",
                """{"version":1,"default":"deny"}""", ifMatch: "\"00000000000000000000000000000000\"");
            Assert.Equal(HttpStatusCode.PreconditionFailed, bad);
            Assert.Equal(ValidPolicy, File.ReadAllText(path));   // unchanged

            // The current tag succeeds.
            var (ok, _, _) = await ApiPutAsync(api.Port, api.Token, "/api/v1/policy",
                """{"version":1,"default":"deny"}""", ifMatch: etag);
            Assert.Equal(HttpStatusCode.OK, ok);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Put_PayloadTooLarge_413()
    {
        var dir = FreshDir();
        var path = Path.Combine(dir, "dispatch.json");
        try
        {
            using var api = HostFor(path);
            var huge = "{\"version\":1,\"pad\":\"" + new string('x', (1 << 20) + 16) + "\"}";
            var (status, _, _) = await ApiPutAsync(api.Port, api.Token, "/api/v1/policy", huge);

            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, status);
            Assert.False(File.Exists(path));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Put_NoToken_401_InheritsAuthGate()
    {
        var dir = FreshDir();
        var path = Path.Combine(dir, "dispatch.json");
        try
        {
            using var api = HostFor(path);
            using var client = new HttpClient();
            using var req = new HttpRequestMessage(HttpMethod.Put, $"http://127.0.0.1:{api.Port}/api/v1/policy")
            {
                Content = new StringContent(ValidPolicy, Encoding.UTF8, "application/json"),
            };
            var resp = await client.SendAsync(req);
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
            Assert.False(File.Exists(path));   // gate blocks before the writer runs
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Put_NoWriter_404()
    {
        // A read model but no writer (a null policy path in the daemon): the write
        // verb 404s, exactly as the reads report the policy "absent".
        using var api = ApiHost.Start(FreeTcpPort(),
            readModel: new ApiReadModel("testver", new ServeStats(),
                new Dispatcher(new Registry(), TimeSpan.FromSeconds(2)),
                new ReloadingHarnessRegistry(NoHarnessDir()), new ReloadingPolicy(null), null,
                clock: () => 6000, startTick: 1000));
        var (status, _, _) = await ApiPutAsync(api.Port, api.Token, "/api/v1/policy", ValidPolicy);
        Assert.Equal(HttpStatusCode.NotFound, status);
    }

    // ---- end-to-end: the PUT mutates the LIVE dispatch hot path -------------

    [Fact]
    public async Task Put_DenyPolicy_ShortCircuitsTheLiveDispatchPath()
    {
        // The ADR's mandatory end-to-end verify (put-policy-write mutates the live
        // dispatch.json that governs whether every hook is worked): a real daemon,
        // a real UDS hook, a real handler — the PUT must be visible on the NEXT
        // dispatch, not just on disk. A marker-injecting handler makes the
        // difference observable: allow-all runs it (marker in stdout), deny-all
        // short-circuits the whole dispatch to a valid Noop (no marker).
        using var dir = new TempRuntimeDir();
        using var trail = new TempTrail();
        using var stop = new CancellationTokenSource();
        var policyPath = Path.Combine("/tmp", "chk-e2e-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        var apiPort = FreeTcpPort();
        const string marker = "POLICY-E2E-MARKER";

        var registry = new Registry()
            .On("UserPromptSubmit", TestHandler.Returning("injector", new Effect.Inject(marker)));
        var daemon = Task.Run(() => DaemonHost.RunAsync(dir.Paths, NoHarnessDir(), stop.Token,
            registry, drainDeadline: TimeSpan.FromSeconds(5), idleWindow: TimeSpan.FromMinutes(5),
            policyPath: policyPath, apiPort: apiPort, sse: new SseOptions(trail.Path)));
        try
        {
            await PollUntilAsync(async () =>
                await ShimClient.TryForwardAsync(dir.Paths.SocketPath,
                    new HookRequest("warm", "session-start", "claude-code", "{}"u8.ToArray()))
                    is ForwardOutcome.Answered,
                TimeSpan.FromSeconds(15), "daemon up");

            // Policy absent → allow-all: the handler runs, the marker reaches stdout.
            Assert.Contains(marker, await ForwardPrompt(dir, "u1"));

            // PUT deny-all through the management API.
            var token = ApiDiscovery.TryRead(dir.Paths.ApiJsonPath)!.Token;
            var (status, _, _) = await ApiPutAsync(apiPort, token, "/api/v1/policy",
                """{"version":1,"default":"deny"}""");
            Assert.Equal(HttpStatusCode.OK, status);

            // The SAME hook is now short-circuited on the live path: ReloadingPolicy
            // stat-gated the file the PUT wrote and denied the dispatch. No marker.
            Assert.DoesNotContain(marker, await ForwardPrompt(dir, "u2"));
        }
        finally
        {
            stop.Cancel();
            await daemon.WaitAsync(TimeSpan.FromSeconds(15));
            File.Delete(policyPath);
        }
    }

    private static async Task<string> ForwardPrompt(TempRuntimeDir dir, string id)
    {
        var outcome = await ShimClient.TryForwardAsync(dir.Paths.SocketPath,
            new HookRequest(id, "user-prompt-submit", "claude-code", "{}"u8.ToArray()));
        var answered = Assert.IsType<ForwardOutcome.Answered>(outcome);
        return Encoding.UTF8.GetString(answered.StdoutBytes);
    }
}

/// ADR-0016 decision 12 (policy-content-hash), the API half: a PUT that changes
/// the rules is a first-class ledger event carrying the document's identity.
/// The test that matters is AGREEMENT — the write and the daemon's own reload of
/// that same file must stamp one document with one hash, or the ledger shows two
/// documents where a human made one change.
public class PolicyWriteLedgerTests
{
    private const string Policy =
        """{"version":1,"default":"allow","rules":[{"event":"Stop","decision":"deny"}]}""";

    private static string FreshDir()
    {
        var dir = Path.Combine("/tmp", "chk-polhash-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Write_EmitsPolicyWrite_WithContentIdentity()
    {
        var dir = FreshDir();
        try
        {
            using var captured = new CapturedLog();
            var path = Path.Combine(dir, "dispatch.json");

            Assert.IsType<PolicyWriteOutcome.Written>(
                new ApiPolicyWriter(path).Write(Encoding.UTF8.GetBytes(Policy), ifMatch: null));

            var ev = Assert.Single(captured.Events.ToArray(), e => e.Evt == "policy.write");
            Assert.Equal("policy", ev.Src);   // one src filter shows a document's whole life
            Assert.Equal(path, ev.Fields.Data!["path"]);
            Assert.Equal(PolicyContent.Of(Policy).Hash, ev.Fields.Data["hash"]);
            Assert.Equal(PolicyContent.Of(Policy).Bytes, ev.Fields.Data["bytes"]);

            // The ETag and the ledger hash are different surfaces over the same
            // input, so an operator can join them by prefix.
            Assert.Equal($"\"{PolicyContent.Of(Policy).Hash[..32]}\"", ApiReadModel.Etag(Policy));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void WriteThenDaemonReload_ReportTheSameHash()
    {
        // End to end across BOTH emit sites: the API installs a document and the
        // daemon's stat-gate picks it up. One change, one hash — which is the
        // whole point of d12 (match a ledger hash against a known document).
        var dir = FreshDir();
        try
        {
            using var captured = new CapturedLog();
            var path = Path.Combine(dir, "dispatch.json");
            var rp = new ReloadingPolicy(path);          // absent at construction
            Assert.IsType<PolicyResolution.Absent>(rp.Current);

            Assert.IsType<PolicyWriteOutcome.Written>(
                new ApiPolicyWriter(path).Write(Encoding.UTF8.GetBytes(Policy), ifMatch: null));
            Assert.IsType<PolicyResolution.Loaded>(rp.Current);

            var events = captured.Events.ToArray();
            var write = Assert.Single(events, e => e.Evt == "policy.write");
            var reload = Assert.Single(events, e => e.Evt == "policy.reload");
            Assert.Equal(write.Fields.Data!["hash"], reload.Fields.Data!["hash"]);
            Assert.Equal(write.Fields.Data["bytes"], reload.Fields.Data["bytes"]);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void BomPrefixedBody_StampsAsTheBomlessDocument()
    {
        // The writer strips the BOM before installing, so the bytes on disk (and
        // therefore the daemon's reload hash) are BOM-free. Hashing the REQUEST
        // bytes instead would make the same document arrive with two identities
        // depending on the client's editor.
        var dir = FreshDir();
        try
        {
            using var captured = new CapturedLog();
            var path = Path.Combine(dir, "dispatch.json");
            var body = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetPreamble()
                .Concat(Encoding.UTF8.GetBytes(Policy)).ToArray();

            Assert.IsType<PolicyWriteOutcome.Written>(new ApiPolicyWriter(path).Write(body, ifMatch: null));

            var ev = Assert.Single(captured.Events.ToArray(), e => e.Evt == "policy.write");
            Assert.Equal(PolicyContent.Of(Policy).Hash, ev.Fields.Data!["hash"]);
            Assert.Equal(PolicyContent.Of(Policy).Bytes, ev.Fields.Data["bytes"]);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void RejectedWrites_EmitNothing()
    {
        // The ledger records what was IN FORCE, not what was attempted: a 422 or
        // a 412 changed no rules, and a phantom policy.write for one would put a
        // document on the ledger that never existed on disk.
        var dir = FreshDir();
        try
        {
            using var captured = new CapturedLog();
            var path = Path.Combine(dir, "dispatch.json");
            var writer = new ApiPolicyWriter(path);

            Assert.IsType<PolicyWriteOutcome.Invalid>(writer.Write(Encoding.UTF8.GetBytes("{ nope"), null));
            Assert.IsType<PolicyWriteOutcome.Mismatch>(
                writer.Write(Encoding.UTF8.GetBytes(Policy), ifMatch: "\"deadbeef\""));

            Assert.DoesNotContain(captured.Events.ToArray(), e => e.Evt == "policy.write");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
