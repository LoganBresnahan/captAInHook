using System.Text.Json;

namespace CaptainHook.Api;

// The read surface's response shapes (ADR-0007 decision 3), rendered by ApiJson
// with Web (camelCase) options — the shape item 6's GUI consumes. Plain records,
// projected from the SAME live objects the dispatch path uses (ApiReadModel), so
// the API view can never drift from daemon behavior. Reflection STJ, no source-
// gen: the host is JIT.

/// GET /status — who this daemon is and how busy it has been.
public sealed record StatusDto(
    string Version, int Pid, long UptimeMs,
    int Active, long Served, int BackgroundPending);

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

/// GET /handlers — every registered handler with its fail mode and live
/// supervision state (generation, dead), across the C#/F# boundary.
public sealed record HandlersDto(IReadOnlyList<HandlerDto> Handlers);

public sealed record HandlerDto(
    string Event, string Name, string FailMode, int Generation, bool Dead);
