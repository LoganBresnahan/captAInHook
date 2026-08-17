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

/// GET /mail — the mailbox bus as one read-only snapshot (ADR-0016 d14): the
/// chain's status, the ledger's lines from `?since=`, one view per cursor on
/// disk, and the inferred presence of the sessions behind them. Nothing here
/// can be written back: the projection is built over a `MailReadPort`, which
/// carries no append or advance handle at all, and no non-GET method answers
/// under /api/v1/mail.
///
/// `Since` echoes the request's offset (0 = the whole retained store, which is
/// what a fresh snapshot asks for); `SinceAligned` is false when that offset
/// rests on no line boundary — the client's idea of where it had read is stale
/// (a truncation or a replaced chain), so it must re-snapshot from 0 rather
/// than splice. `Frontier` is the end of the last COMPLETE line: an append in
/// flight is visible in `Lines` (as `Terminated: false`) and deliberately NOT
/// behind the frontier, exactly as a cursor read sees it.
///
/// `TrailEventId` is the SSE id space's answer to a hazard the reducer would
/// otherwise have to absorb on every mount. A live view needs BOTH this
/// snapshot and the `mail.*` stream, and whatever happens between acquiring
/// them is either lost or duplicated. Snapshot-then-subscribe LOSES: a fresh
/// subscription anchors at the trail's current end (ADR-0007 d5), so events in
/// the window are gone, and a vanished `mail.cursorAdvance` leaves an envelope
/// pending forever with nothing flagged — the one silent-wrong picture no
/// screenshot catches. Subscribe-then-snapshot cannot lose, but replays the
/// window; the reducer survives that (`deliveries` is a per-cursor sequence
/// number, a `mail.deliver` record's identity is its content), except that a
/// replayed FIRST advance is indistinguishable from a deleted-and-restarted
/// cursor lineage, so the honest answer there is a flag and a re-snapshot —
/// fired routinely, since the window is exactly where a first advance replays.
/// So the snapshot carries the trail offset it was taken at, and the client
/// subscribes with `Last-Event-ID: <trailEventId>`: the id IS the byte offset
/// after a line, so the resume starts exactly where this picture's knowledge
/// ends — zero loss, zero duplicate. It is read BEFORE the store below, which
/// makes the residual window (two in-process reads, microseconds) able to
/// duplicate but never to lose, and the replay rules go back to being what they
/// were written for: reconnects, gaps, rotation. Null when the daemon serves no
/// trail (no stream to align to) — a client that finds it absent must fall back
/// to subscribe-then-snapshot and fold the overlap as replay, NEVER to "0",
/// which in this id space means "from the earliest still-reachable point"
/// (ADR-0009 d2) and would replay the whole trail as live.
///
/// A STRING, though today it is a number, for the same reason `Last-Event-ID`
/// is: ADR-0009 d2 makes the resume id an OPAQUE monotonic cursor a client
/// stores and echoes and never interprets, precisely so d4 can silently
/// redefine it as a cross-segment global offset. A numeric field would invite
/// the arithmetic that contract forbids, and would put "0" and absent on the
/// same side of a falsy test — the one comparison that must never collapse,
/// since "0" means replay everything and absent means do not resume at all.
///
/// `Deliveries` is the delivery-record PRELOAD (6a). A `mail.deliver` line is
/// the only thing that makes an envelope delivered (d14 pin iii) and the store
/// cannot contain one, so before this field a picture could only ever say
/// *before cursor · no record* about a pickup that predated the page — true,
/// and misleading about mail that was demonstrably read. These are the trail's
/// own lines, folded server-side because the client cannot ask for them: the
/// resume id is opaque (ADR-0009 d2), so "an id older than the stamp" is
/// arithmetic the contract forbids. `DeliveriesComplete` false means the fold
/// saw only a suffix of the history (scan window or record cap), which is what
/// lets the view distinguish "nobody read it" from "further back than I can
/// see". Records carry NO ledger offsets: placing an id on the ledger is the
/// reducer's arithmetic, and a second implementation of it here is N8 again.
public sealed record MailDto(
    string Dir, MailChainDto Chain, long Since, bool SinceAligned, long Frontier,
    string? TrailEventId,
    IReadOnlyList<MailLineDto> Lines,
    IReadOnlyList<MailCursorDto> Cursors,
    IReadOnlyList<MailPresenceDto> Presence,
    IReadOnlyList<MailDeliveryDto> Deliveries,
    bool DeliveriesComplete);

/// One `mail.deliver` ledger line as the trail stated it — the columns d10
/// gives it, and nothing derived. `Session` is the recipient session (absent
/// for a sessionless reader), `Ts` the wall-clock stamp the line carries; the
/// browser uses it for display only, never for control flow.
public sealed record MailDeliveryDto(
    string Role, string? Session, string? DispatchId, string? HookEvent,
    string Seam, string Vehicle,
    IReadOnlyList<string> EnvelopeIds,
    string? RenderHash, long? BytesInjected, string? Ts);

/// The store's health as one object: `Ok` iff `VerifyChain` reported nothing.
/// `Gen` is the store generation cursors compare against (rotation is ADR-0016
/// N4's future work, so it is 1 today); `Head` is the first complete line's
/// hash — the chain-native rotation check. `DirMode`/`FileMode` are octal
/// (`"700"` / `"600"`), null when the path is absent or unreadable: the three
/// stores' owner-only discipline (d13) is a fact an operator should be able to
/// SEE, not one they have to trust.
public sealed record MailChainDto(
    bool Ok, string? Head, int Gen, int Lines, long Bytes,
    string? DirMode, string? FileMode,
    IReadOnlyList<MailChainFaultDto> Faults);

/// One link that does not hold. `Kind` is the accusation, lowercased
/// ("genesis" | "prevMismatch" | "prevMissing" | "unreadable"); the store never
/// guesses between them and neither does this.
public sealed record MailChainFaultDto(long Offset, string Kind, string Detail);

/// One line as it exists on disk. `Envelope` is null for a line the strict
/// parser rejected — `Errors` then says why, and the line is still reported
/// because a reader walking offsets must see the bytes it steps over.
/// `Terminated` false is the torn tail (an interrupted write, or an append in
/// flight right now).
public sealed record MailLineDto(
    long Offset, int Bytes, bool Terminated, string Hash,
    MailEnvelopeDto? Envelope, IReadOnlyList<string> Errors);

/// A stored envelope, field for field. The BODY is included: this endpoint
/// reads the archival store itself for the operator's own authenticated GUI,
/// which is the one surface entitled to it — unlike the trail, which is
/// payload-readable and therefore carries provenance only (d14).
public sealed record MailEnvelopeDto(
    string Id, string Ts, MailSenderDto From, string To, string Kind, string Topic,
    string Priority, string? InReplyTo, int TtlDeliveries, string Body, string? Prev);

public sealed record MailSenderDto(string Agent, string Harness, string? Session);

/// One cursor file's view of the store — `MailCursors.Pending` verbatim, which
/// is what keeps the drawn mailbox and the delivered mailbox the same thing.
/// `Offset` is where the cursor IS (its read position; 0 when fresh or
/// re-anchored) and `Frontier` is where the next advance will put it — the
/// store's end of complete lines — so a canvas can draw the cursor and the
/// unread stretch ahead of it without inferring either from the pending list
/// (a role with no fresh mail would leave the position unknowable).
/// `Reanchored` means THIS read distrusted the file on disk (`ReanchorReason`
/// names which rule), so everything retained for the role is pending again.
public sealed record MailCursorDto(
    string Role, string? Session, int Gen, string? Head, long Offset, long Frontier, long Deliveries,
    string? LastDeliveredId, bool Reanchored, string? ReanchorReason,
    IReadOnlyList<MailPendingDto> Pending, IReadOnlyList<MailPendingDto> Expired,
    int SkippedMalformed);

/// One envelope this cursor has not consumed, joined to `Lines` by `Offset`.
/// `SeenAt` null means fresh (no TTL accrued); a value is the delivery
/// opportunity that first passed it over. `Opportunities` is how many have
/// passed since — `deliveries − seenAt + 1`, the exact quantity the cursor
/// compares against `TtlDeliveries`, so the countdown on screen is the
/// engine's arithmetic and not a second implementation of it.
public sealed record MailPendingDto(
    long Offset, string Id, string Priority, int TtlDeliveries,
    long? SeenAt, long Opportunities);

/// An inferred bus participant. `Roles` are the cursors this session holds;
/// `LastDispatchAgeMs` is how long ago this daemon dispatched a hook for it,
/// null when it has not (a cursor-only session — quiet, or the daemon
/// restarted). Presence is INFERRED and says so: there is no registry and no
/// heartbeat, so a stale entry means unknown, never gone.
public sealed record MailPresenceDto(
    string Session, IReadOnlyList<string> Roles, long? LastDispatchAgeMs);

/// A handlers.json entry as the file DECLARES it, joined to whether it actually
/// registered. Valid entries: `Registered:true`, full fields. A skipped entry:
/// `Registered:false` + `SkipReason` (its violations), and Mode/FailMode null
/// (they may be exactly what failed to parse).
public sealed record ExpectedHandlerDto(
    string Name, IReadOnlyList<string> Events, string? Mode, string? FailMode,
    bool Registered, string? SkipReason);
