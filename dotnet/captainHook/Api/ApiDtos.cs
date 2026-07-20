using System.Text.Json;

namespace CaptainHook.Api;

// The read surface's response shapes (ADR-0007 decision 3), rendered by ApiJson
// with Web (camelCase) options — the shape item 6's GUI consumes. Plain records,
// projected from the SAME live objects the dispatch path uses (ApiReadModel), so
// the API view can never drift from daemon behavior. Reflection STJ, no source-
// gen: the host is JIT.

/// GET /status — who this daemon is and how busy it has been. OpenStreams is
/// the live SSE subscription count (the idle-defer signal, ADR-0007 d7).
/// ShimPath is THIS daemon's deploy dir's shim executable — the resolved
/// command the GUI's wiring hint renders into the harness install template
/// (ADR-0011 d5: shown, never written); null when no shim is co-located
/// (dev runs of the bare engine).
public sealed record StatusDto(
    string Version, int Pid, long UptimeMs,
    int Active, long Served, int BackgroundPending, int OpenStreams,
    string? ShimPath);

/// GET /policy — the resolved dispatch-policy tri-state (ADR-0006 decision 4)
/// plus the raw file and a content-hash ETag (the token put-policy-write's
/// If-Match consumes). `State` is "absent" | "malformed" | "loaded"; `Error` is
/// set only for malformed, `Policy` only for loaded, `Raw`/`Etag` only when a
/// file is present.
public sealed record PolicyDto(
    string State, string? Error, PolicyDocDto? Policy,
    string? Raw, string? Path, string? Etag);

public sealed record PolicyDocDto(string Default, IReadOnlyList<PolicyRuleDto> Rules);

public sealed record PolicyRuleDto(
    string? Event, string? Handler, string? Project, string? Session, string Decision);

/// GET /harnesses — the registry view (ADR-0003): every known spec, its adapter,
/// request field mapping, per-event effect capabilities, and opaque install data.
public sealed record HarnessesDto(IReadOnlyList<HarnessDto> Harnesses);

public sealed record HarnessDto(
    string Name, string ResponseAdapter, HarnessRequestDto Request,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Events, JsonElement? Install);

public sealed record HarnessRequestDto(string EventNameField, string SessionIdField, string CwdField);

/// GET /handlers — every registered handler with its fail mode, live
/// supervision state (generation, dead), and resident CHILD state; plus the
/// handlers.json file tri-state (`Source`: "absent" | "malformed" | "loaded")
/// and its EXPECTED entries — config-declared vs live-registered (ADR-0010 d8).
/// A warn-and-skip entry appears in `Expected` with `Registered:false`, NEVER
/// as a live `Handlers` row (the N2 caution: a skipped fail-closed gate must
/// never read as live).
/// `Raw`/`Etag` mirror PolicyDto: the file's current text and its content-hash
/// tag (null when absent/unreadable) — the If-Match token PUT /handlers
/// consumes (ADR-0011 d3).
public sealed record HandlersDto(
    IReadOnlyList<HandlerDto> Handlers,
    string Source, string? Error, string? Path,
    IReadOnlyList<ExpectedHandlerDto> Expected,
    string? Raw, string? Etag);

/// A live registered handler. `ChildState` ("spawning"|"ready"|"failed") and
/// `ChildPid` are set only for a resident exec handler — null for oneshot and
/// coded handlers, which own no persistent child.
public sealed record HandlerDto(
    string Event, string Name, string FailMode, int Generation, bool Dead,
    string? ChildState, int? ChildPid);

/// A handlers.json entry as the file DECLARES it, joined to whether it actually
/// registered. Valid entries: `Registered:true`, full fields. A skipped entry:
/// `Registered:false` + `SkipReason` (its violations), and Mode/FailMode null
/// (they may be exactly what failed to parse).
public sealed record ExpectedHandlerDto(
    string Name, IReadOnlyList<string> Events, string? Mode, string? FailMode,
    bool Registered, string? SkipReason);
