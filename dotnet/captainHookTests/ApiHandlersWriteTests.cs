using System.Net;
using System.Text;
using System.Text.Json;
using CaptainHook.Api;
using CaptainHook.Core;
using CaptainHook.Wire;
using static CaptainHook.Tests.TestUtil;

namespace CaptainHook.Tests;

// PUT /api/v1/handlers (ADR-0011 decision 3): the API as EDITOR of
// handlers.json, on ApiPolicyWriteTests' exact two-layer shape:
//   * ApiHandlersWriter — validation via the daemon's OWN ExecHandlersFile
//     parser, the d3 STRICTNESS SPLIT (a warn-and-skip entry is refused at the
//     write though registration would tolerate it), If-Match preconditions,
//     and the atomic same-dir rename. Driven directly, no HTTP.
//   * PUT /api/v1/handlers — the HTTP shell: outcome → status mapping, the
//     ETag round-trip against GET /handlers' new header, the inherited gate.
// The adversarial-verify surface ADR-0011's plan names: the atomicity
// interleave (a stat-gated resolver mid-write) and the daemon E2E (a PUT
// visible on the NEXT dispatch — reconcile, not eventually).
public class ApiHandlersWriteTests
{
    private const string ValidHandlers =
        """{"version":1,"handlers":[{"name":"greet","command":"/bin/sh","args":["-c","printf '{\"effect\":\"noop\"}'"],"events":["UserPromptSubmit"],"mode":"oneshot"}]}""";

    // Parses FILE-valid, but the entry is missing its required command — the
    // exact shape ADR-0010 d4 warns-and-skips at registration and ADR-0011 d3
    // refuses at the write.
    private const string SkipWorthyHandlers =
        """{"version":1,"handlers":[{"name":"broken","events":["UserPromptSubmit"]}]}""";

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    private static string FreshDir()
    {
        var dir = Path.Combine("/tmp", "chk-hndw-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ---- ApiHandlersWriter: validation --------------------------------------

    [Fact]
    public void Write_ValidHandlers_InstallsFile_AndReturnsRoundTripEtag()
    {
        var dir = FreshDir();
        var path = Path.Combine(dir, "handlers.json");
        try
        {
            var outcome = new ApiHandlersWriter(path).Write(Utf8(ValidHandlers), ifMatch: null);

            var written = Assert.IsType<HandlersWriteOutcome.Written>(outcome);
            Assert.Equal(ValidHandlers, File.ReadAllText(path));
            Assert.Equal(ApiReadModel.Etag(File.ReadAllText(path)), written.Etag);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Write_MalformedJson_Invalid_FileUntouched()
    {
        var dir = FreshDir();
        var path = Path.Combine(dir, "handlers.json");
        try
        {
            var outcome = new ApiHandlersWriter(path).Write(Utf8("{nope"), ifMatch: null);
            var inv = Assert.IsType<HandlersWriteOutcome.Invalid>(outcome);
            Assert.Contains(inv.Violations, v => v.Contains("not valid JSON"));
            Assert.False(File.Exists(path));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Write_FileLevelInvalid_Invalid_CarriesParserViolations()
    {
        var dir = FreshDir();
        var path = Path.Combine(dir, "handlers.json");
        try
        {
            var outcome = new ApiHandlersWriter(path)
                .Write(Utf8("""{"version":2,"handlers":[]}"""), ifMatch: null);
            var inv = Assert.IsType<HandlersWriteOutcome.Invalid>(outcome);
            Assert.Contains(inv.Violations, v => v.Contains("'version' must be 1"));
            Assert.False(File.Exists(path));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Write_SkipWorthyEntry_Refused_ThoughRegistrationWouldTolerate()
    {
        // THE d3 strictness split, pinned as a contrast: the SAME bytes that
        // the write path refuses are entry-lenient at registration —
        // ExecHandlersFile.Resolve loads the file and merely skips the entry.
        // The API must never install a known-broken entry; a hand edit keeps
        // ADR-0010 d4's tolerance. Both behaviors asserted on one body so the
        // split can never silently converge in either direction.
        var dir = FreshDir();
        var path = Path.Combine(dir, "handlers.json");
        try
        {
            var outcome = new ApiHandlersWriter(path).Write(Utf8(SkipWorthyHandlers), ifMatch: null);
            var inv = Assert.IsType<HandlersWriteOutcome.Invalid>(outcome);
            // Violations are LABELED per entry so the client can fix the exact one.
            Assert.Contains(inv.Violations, v => v.Contains("broken") && v.Contains("command"));
            Assert.False(File.Exists(path));   // refused: nothing installed

            // The registration side of the split: the same content on disk is
            // Loaded (not Malformed) with the entry skipped.
            File.WriteAllText(path, SkipWorthyHandlers);
            var res = Assert.IsType<ExecHandlersResolution.Loaded>(ExecHandlersFile.Resolve(path));
            Assert.Empty(res.Entries);
            Assert.Single(res.Skipped);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Write_InvalidUtf8_Invalid()
    {
        var dir = FreshDir();
        var path = Path.Combine(dir, "handlers.json");
        try
        {
            var outcome = new ApiHandlersWriter(path)
                .Write([0xC3, 0x28, (byte)'{', (byte)'}'], ifMatch: null);
            var inv = Assert.IsType<HandlersWriteOutcome.Invalid>(outcome);
            Assert.Contains(inv.Violations, v => v.Contains("UTF-8"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Write_BomPrefixedBody_Accepted_WritesNoBom_RoundTripEtag()
    {
        // The put-policy lesson, inherited: the loader (File.ReadAllText)
        // strips a leading BOM, so the writer must accept what the loader
        // would load — and write it BOM-free so the returned tag equals what
        // GET computes over the file.
        var dir = FreshDir();
        var path = Path.Combine(dir, "handlers.json");
        try
        {
            var body = Encoding.UTF8.GetPreamble().Concat(Utf8(ValidHandlers)).ToArray();
            var written = Assert.IsType<HandlersWriteOutcome.Written>(
                new ApiHandlersWriter(path).Write(body, ifMatch: null));

            var bytes = File.ReadAllBytes(path);
            Assert.False(bytes is [0xEF, 0xBB, 0xBF, ..], "written file must be BOM-free");
            Assert.Equal(ApiReadModel.Etag(File.ReadAllText(path)), written.Etag);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ---- ApiHandlersWriter: If-Match ----------------------------------------

    [Fact]
    public void Write_IfMatch_CurrentTag_Writes_StaleTag_Mismatch()
    {
        var dir = FreshDir();
        var path = Path.Combine(dir, "handlers.json");
        try
        {
            var writer = new ApiHandlersWriter(path);
            var first = Assert.IsType<HandlersWriteOutcome.Written>(
                writer.Write(Utf8(ValidHandlers), null));

            // Stale tag: refused, file untouched, current tag carried for re-seed.
            const string empty = """{"version":1,"handlers":[]}""";
            var stale = Assert.IsType<HandlersWriteOutcome.Mismatch>(
                writer.Write(Utf8(empty), "\"00000000000000000000000000000000\""));
            Assert.Equal(first.Etag, stale.Current);
            Assert.Equal(ValidHandlers, File.ReadAllText(path));

            // Current tag: succeeds.
            Assert.IsType<HandlersWriteOutcome.Written>(writer.Write(Utf8(empty), first.Etag));
            Assert.Equal(empty, File.ReadAllText(path));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Write_IfMatch_Star_OnExisting_Writes_OnAbsent_Mismatches()
    {
        var dir = FreshDir();
        var path = Path.Combine(dir, "handlers.json");
        try
        {
            var writer = new ApiHandlersWriter(path);
            // Absent + "*": the client believed content existed — refused.
            var m = Assert.IsType<HandlersWriteOutcome.Mismatch>(
                writer.Write(Utf8(ValidHandlers), "*"));
            Assert.Null(m.Current);
            Assert.False(File.Exists(path));

            writer.Write(Utf8(ValidHandlers), null);
            Assert.IsType<HandlersWriteOutcome.Written>(
                writer.Write(Utf8("""{"version":1,"handlers":[]}"""), "*"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Write_LeavesNoTempLitter_AcrossManyWrites()
    {
        var dir = FreshDir();
        var path = Path.Combine(dir, "handlers.json");
        try
        {
            var writer = new ApiHandlersWriter(path);
            for (var i = 0; i < 50; i++)
            {
                writer.Write(Utf8(ValidHandlers), null);
                writer.Write(Utf8("{broken"), null);   // Invalid path must not litter either
            }
            Assert.Equal(["handlers.json"],
                Directory.GetFiles(dir).Select(Path.GetFileName).OrderBy(n => n).ToArray());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ---- the atomicity interleave (adversarial surface #1) ------------------

    [Fact]
    public async Task Write_IsAtomic_ConcurrentResolverNeverSeesTornOrAbsent()
    {
        // The exact hazard: ReloadingHandlers stat-gates this file per dispatch;
        // a torn or momentarily-absent write would flash Malformed (kill every
        // resident child — no keep-last-good) or Absent (same). The reader here
        // IS the daemon's resolver, not a File.Exists proxy, so this interleave
        // fails on any non-rename write strategy.
        var dir = FreshDir();
        var path = Path.Combine(dir, "handlers.json");
        File.WriteAllText(path, ValidHandlers);   // present from t0
        var writer = new ApiHandlersWriter(path);
        try
        {
            var stop = false;
            var offenders = new System.Collections.Concurrent.ConcurrentBag<string>();
            var reader = Task.Run(() =>
            {
                while (!Volatile.Read(ref stop))
                {
                    var res = ExecHandlersFile.Resolve(path);
                    if (res is not ExecHandlersResolution.Loaded) offenders.Add(res.GetType().Name);
                }
            });

            for (var i = 0; i < 400; i++)
                writer.Write(Utf8(i % 2 == 0 ? ValidHandlers : """{"version":1,"handlers":[]}"""), null);

            Volatile.Write(ref stop, true);
            await reader;

            Assert.True(offenders.IsEmpty,
                $"a concurrent resolver observed a non-Loaded state mid-write: {string.Join(",", offenders)}");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ---- PUT /api/v1/handlers: the HTTP mapping -----------------------------

    private static ApiHost HostFor(string handlersPath) =>
        ApiHost.Start(FreeTcpPort(),
            readModel: new ApiReadModel("testver", new ServeStats(),
                new Dispatcher(new Registry(), TimeSpan.FromSeconds(2)),
                new ReloadingHarnessRegistry(NoHarnessDir()), new ReloadingPolicy(null), null,
                clock: () => 6000, startTick: 1000, handlersPath: handlersPath),
            handlersWriter: new ApiHandlersWriter(handlersPath));

    [Fact]
    public async Task Put_ValidHandlers_200_WithEtagHeader_ThenGetCarriesSameTag()
    {
        var dir = FreshDir();
        var path = Path.Combine(dir, "handlers.json");
        try
        {
            using var api = HostFor(path);
            var (status, body, etag) = await ApiPutAsync(api.Port, api.Token, "/api/v1/handlers", ValidHandlers);

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.False(string.IsNullOrEmpty(etag));
            var echo = JsonDocument.Parse(body).RootElement;
            Assert.Equal("loaded", echo.GetProperty("source").GetString());
            Assert.Contains(echo.GetProperty("expected").EnumerateArray(),
                e => e.GetProperty("name").GetString() == "greet");

            // GET /handlers now carries the same tag in header AND body — the
            // round-trip the editor's If-Match chain depends on.
            var (gs, gbody) = await ApiGetAsync(api.Port, api.Token, "/api/v1/handlers");
            var g = JsonDocument.Parse(gbody).RootElement;
            Assert.Equal(HttpStatusCode.OK, gs);
            Assert.Equal(etag, g.GetProperty("etag").GetString());
            Assert.Equal(ValidHandlers, g.GetProperty("raw").GetString());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Put_SkipWorthyEntry_422_WithLabeledViolations_NothingWritten()
    {
        var dir = FreshDir();
        var path = Path.Combine(dir, "handlers.json");
        try
        {
            using var api = HostFor(path);
            var (status, body, _) = await ApiPutAsync(api.Port, api.Token, "/api/v1/handlers", SkipWorthyHandlers);

            Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
            var r = JsonDocument.Parse(body).RootElement;
            Assert.Equal("invalid_handlers", r.GetProperty("error").GetString());
            Assert.Contains(r.GetProperty("violations").EnumerateArray(),
                v => v.GetString()!.Contains("broken"));
            Assert.False(File.Exists(path));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Put_IfMatch_Mismatch_412_ThenCorrectTag_200()
    {
        var dir = FreshDir();
        var path = Path.Combine(dir, "handlers.json");
        try
        {
            using var api = HostFor(path);
            var (_, _, etag) = await ApiPutAsync(api.Port, api.Token, "/api/v1/handlers", ValidHandlers);

            const string empty = """{"version":1,"handlers":[]}""";
            var (bad, _, _) = await ApiPutAsync(api.Port, api.Token, "/api/v1/handlers",
                empty, ifMatch: "\"00000000000000000000000000000000\"");
            Assert.Equal(HttpStatusCode.PreconditionFailed, bad);
            Assert.Equal(ValidHandlers, File.ReadAllText(path));

            var (ok, _, _) = await ApiPutAsync(api.Port, api.Token, "/api/v1/handlers", empty, ifMatch: etag);
            Assert.Equal(HttpStatusCode.OK, ok);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Put_NoToken_401_InheritsAuthGate()
    {
        var dir = FreshDir();
        var path = Path.Combine(dir, "handlers.json");
        try
        {
            using var api = HostFor(path);
            using var client = new HttpClient();
            using var req = new HttpRequestMessage(HttpMethod.Put, $"http://127.0.0.1:{api.Port}/api/v1/handlers")
            {
                Content = new StringContent(ValidHandlers, Encoding.UTF8, "application/json"),
            };
            var resp = await client.SendAsync(req);
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
            Assert.False(File.Exists(path));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Put_NoWriter_404()
    {
        // No handlers path (tests, or a daemon run with none): the write verb
        // 404s, exactly as the reads report the file "absent".
        using var api = ApiHost.Start(FreeTcpPort(),
            readModel: new ApiReadModel("testver", new ServeStats(),
                new Dispatcher(new Registry(), TimeSpan.FromSeconds(2)),
                new ReloadingHarnessRegistry(NoHarnessDir()), new ReloadingPolicy(null), null,
                clock: () => 6000, startTick: 1000));
        var (status, _, _) = await ApiPutAsync(api.Port, api.Token, "/api/v1/handlers", ValidHandlers);
        Assert.Equal(HttpStatusCode.NotFound, status);
    }

    // ---- end-to-end: the PUT reconciles the LIVE daemon (adversarial #2) ----

    [Fact]
    public async Task Put_Handlers_LiveOnTheNextDispatch_BothDirections()
    {
        // ADR-0011's mandated hot-path verify, the vacuous-pass trap called out
        // by the plan: this must assert NEXT-dispatch visibility (the reconcile
        // rides the per-dispatch stat-gate), not eventual — so each PUT is
        // followed by exactly ONE forward whose answer must already reflect it.
        // Both directions: install (marker appears) then uninstall via the API
        // (marker gone), through a real daemon over the real UDS wire.
        using var dir = new TempRuntimeDir();
        using var trail = new TempTrail();
        using var stop = new CancellationTokenSource();
        var handlersPath = Path.Combine("/tmp", "chk-hnde2e-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        var apiPort = FreeTcpPort();
        const string marker = "HANDLERS-E2E-MARKER";

        var daemon = Task.Run(() => DaemonHost.RunAsync(dir.Paths, NoHarnessDir(), stop.Token,
            registry: null,   // default registry => ReloadingHandlers is live
            drainDeadline: TimeSpan.FromSeconds(8), idleWindow: TimeSpan.FromMinutes(5),
            apiPort: apiPort, sse: new SseOptions(trail.Path), handlersPath: handlersPath));
        try
        {
            await PollUntilAsync(async () =>
                await ShimClient.TryForwardAsync(dir.Paths.SocketPath,
                    new HookRequest("warm0000", "session-start", "claude-code", "{}"u8.ToArray()))
                    is ForwardOutcome.Answered,
                TimeSpan.FromSeconds(15), "daemon up");

            // No file → the injector doesn't exist yet.
            Assert.DoesNotContain(marker, await ForwardPrompt(dir, "pre00001"));

            // INSTALL through the API: an oneshot injector on UserPromptSubmit.
            var token = ApiDiscovery.TryRead(dir.Paths.ApiJsonPath)!.Token;
            var install = $$"""
                {"version":1,"handlers":[{"name":"e2e-inject","command":"/bin/sh",
                 "args":["-c","printf '{\"effect\":\"inject\",\"text\":\"{{marker}}\"}'"],
                 "events":["UserPromptSubmit"],"mode":"oneshot"}]}
                """;
            var (status, _, etag) = await ApiPutAsync(apiPort, token, "/api/v1/handlers", install);
            Assert.Equal(HttpStatusCode.OK, status);

            // The NEXT dispatch must already run the child (stat-gate + reconcile).
            Assert.Contains(marker, await ForwardPrompt(dir, "post0001"));

            // UNINSTALL through the API (If-Match: the editor's own discipline).
            var (rm, _, _) = await ApiPutAsync(apiPort, token, "/api/v1/handlers",
                """{"version":1,"handlers":[]}""", ifMatch: etag);
            Assert.Equal(HttpStatusCode.OK, rm);
            Assert.DoesNotContain(marker, await ForwardPrompt(dir, "gone0001"));
        }
        finally
        {
            stop.Cancel();
            await daemon.WaitAsync(TimeSpan.FromSeconds(20));
            File.Delete(handlersPath);
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
