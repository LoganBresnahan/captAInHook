using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CaptainHook.Actors;
using CaptainHook.Core;

namespace CaptainHook.Mail;

// Roadmap item 20 / ADR-0016 decisions 5, 7, 10 — `captainHook mail digest`,
// the READ PATH and the bus's semantic core: an exec-wire handler command that
// turns "this recipient's pending mail" into one ordinary loop effect at a
// seam the recipient's harness declares. Everything upstream already exists —
// the store (phase 2) knows what was written, the cursor (phase 3) knows what
// this recipient has consumed — and this file decides the one remaining
// question: WHAT, of the pending mail, may be surfaced HERE, NOW, and how.
//
// THE SEAM CLASS IS REGISTRATION DATA, NOT HARNESS CODE. d5's planner is a
// pure function (priority, seam being dispatched, recipient's HarnessSpec
// verbs) → deliver | hold | degrade, with no per-harness code paths — but
// "is PostToolUse a mid-turn seam?" is a loop-position fact no HarnessSpec
// field carries and no event NAME can answer without hardcoding one harness's
// vocabulary. d7 already names the answer: "which events the digest handler
// is registered on in handlers.json IS the deployment's delivery capability —
// registration is configuration." So the registration says it: `--seam
// ambient|urgent|reconcile` classifies the events the entry covers (one entry
// per seam class), and the planner stays pure over (priority, seam class,
// verbs). A misclassified registration degrades safely: the verb check still
// gates what can actually be rendered, and nothing undeliverable is ever
// advanced past.
//
// THE PLANNER MATRIX (d5, degradation downward only):
//
//                      seam: ambient      urgent        reconcile
//   priority ambient        deliver       hold          deliver
//   priority reconcile      deliver(↓)    hold          deliver
//   priority urgent         deliver(↓)    deliver       deliver(↓)
//
//   * An AMBIENT-class seam (turn start) delivers EVERYTHING — the cursor's
//     stated planner obligation ("deliver-or-degrade rather than hold at
//     seams that will advance"): once this seam advances the cursor, every
//     held envelope ages, so holding mail at a seam that is advancing anyway
//     buys nothing and burns TTL. Urgent mail arriving here is late, not
//     lost — the next seam is always the earliest seam there is.
//   * An URGENT-class seam (mid-turn, fires per tool call) delivers ONLY
//     urgent mail — d5's strict-budget discipline: most mail must never
//     qualify. When nothing urgent is pending the answer is noop and the
//     cursor does NOT advance, so a quiet mid-turn seam ages nothing (the
//     other half of the same obligation: only advance when writing state
//     worth the opportunity). The honest cost, stated at the cursor and
//     accepted here: a chatty turn of urgent deliveries DOES age held
//     ambient mail — the TTL unit is advances.
//   * A RECONCILE-class seam (turn end) delivers everything as one digest:
//     the `decide`-shaped block whose REASON carries the digest when decide
//     is the event's only loop verb (Stop's shape — the block is what a
//     Stop can express), an ordinary inject whenever inject is declared.
//     Either way the rendered digest is the delivery (d4: delivered means
//     "was rendered into an effect"), which is exactly the Stop-loop guard:
//     the reconcile turn has the mail in context, the cursor is already
//     past it, and the next Stop finds nothing pending and passes clean (N3).
//
// THE VEHICLE is gated by the recipient's declared verbs, downward only:
// ambient/urgent-class delivery rides `inject` and NOTHING ELSE, and the
// reconcile class too prefers `inject`, reaching for `decide` only when
// inject is absent — mail must never escalate itself into a deny to get
// read, and a `--seam reconcile` typo on a decide+inject mid-turn event
// must never turn a status message into a denied tool call. An
// event the spec does not declare delivers NOTHING — the capability gate
// would let an inject through permissively there, but the gate noops AFTER
// the cursor has advanced, and advanced-past-then-swallowed is mail lost
// silently, the one failure this file must never risk. Same reasoning gives
// the ORDER OF OPERATIONS its rule: the cursor advances BEFORE the answer is
// emitted (crash between the two loses that digest, visibly, rather than
// double-injecting it — d4's chosen direction), so everything that could
// prevent the effect from landing must be checked BEFORE the advance.
//
// What this file cannot see, stated rather than hidden: the digest's answer
// still crosses the dispatcher MERGE (a co-registered handler's deny/replace
// outranks an inject) and the capability gate. Both are deployment
// configuration; a deployment that registers a replacing handler on the same
// event as the digest can eat a delivered digest after the cursor moved.
// The registration guidance (examples/payloads/handlers.json) says to give
// the digest its seam events to itself.
//
// RENDERING (d5/d10): deterministic and bounded — priority rank then arrival
// order, provenance on every item (sender, harness, age in delivery
// opportunities — never a wall clock), a hard character cap with WHOLE-ITEM
// granularity (a dropped item stays pending for the next seam; a truncated
// item would misquote its sender), and the one exception that keeps the cap
// from becoming a deadlock: a single item too big to ever fit is delivered
// truncated with an explicit marker, because "held forever" is mail lost and
// the full text stays durable in the store. No summarization, ever (the
// rejected telephone game).
//
// PROVENANCE RUNS BOTH WAYS (d10). The digest tells the recipient who is
// speaking; `mail.deliver` tells the LEDGER what the recipient was shown —
// ids plus a hash of the rendered bytes, emitted from the one branch where
// mail was really consumed. See LogDelivery below for what that costs and why
// each field is the shape it is.

/// The seam CLASS a digest registration assigns to the events it covers
/// (ADR-0016 d5's three rows). Values match MailPriority's wire spellings on
/// purpose: the priority names the class the sender requests, the
/// registration names the class the seam provides.
public enum MailSeam { Ambient, Urgent, Reconcile }

/// How a planned delivery reaches the loop. None means "nothing may be
/// delivered at this seam" — the answer is noop and the cursor must not move.
public enum MailVehicle { None, Inject, Decide }

/// The planner's verdict for one seam: what to deliver (ordered for
/// rendering), by which vehicle, and how much eligible-but-held mail remains.
public sealed record MailPlan(
    IReadOnlyList<PendingMail> Deliver,
    MailVehicle Vehicle,
    int HeldByPlan);

/// What the renderer produced: the digest text, the items actually rendered
/// (the cap may hold back a tail of the plan — ONLY these offsets advance),
/// and how many planned items the cap held for a later seam.
public sealed record MailRender(
    string Text,
    IReadOnlyList<PendingMail> Delivered,
    int HeldByCap);

/// Parsed `mail digest` registration arguments.
public sealed record MailDigestOptions(
    string Role,
    string Harness,
    MailSeam Seam,
    int MaxChars,
    bool Resident,
    string? Instance = null)
{
    /// The mailbox this registration reads, given the session it was dispatched
    /// for: `--as` when it names one, else the window's own session (ADR-0018
    /// d3). An UNNAMED reader is therefore exactly today's reader — one
    /// ephemeral cursor per window — and a NAMED one has a mailbox that
    /// outlives every window that ever serves it.
    ///
    /// Two windows registered under one name share one cursor, which is the
    /// correct meaning of "one agent" as opposed to "one window": first pickup
    /// consumes, and `Advance`'s per-cursor flock already decides who wins.
    public string? CursorKey(string? sessionId) => Instance ?? sessionId;

    /// This registration's ADDRESS — the spelling a sender writes to reach it,
    /// and the thing that decides what it may read (ADR-0018 d4). A named
    /// registration reads its role's broadcast AND its own unicast; an unnamed
    /// one reads the broadcast alone. Carried rather than re-derived per call
    /// site, so the cursor key and the entitlement can never come from two
    /// different readings of the same flags.
    public MailAddress Mailbox => new(Role, Instance);

    /// Per-seam default render caps, in characters (the deterministic proxy
    /// for d5's "hard token cap"): the mid-turn class fires on every tool
    /// call, so its budget is a quarter of the session-edge classes'.
    public const int DefaultMaxChars = 4096;
    public const int DefaultUrgentMaxChars = 1024;
}

/// One exec-wire envelope as the digest receives it on stdin — the engine's
/// own encoder (ExecWire.Envelope) is the only writer, so the strict parse
/// mirrors exactly what it emits.
public sealed record DigestRequest(string DispatchId, string EventType, string? SessionId);

public static class MailDigest
{
    private const string Usage =
        "usage: captainHook mail digest --role <role> [--as <instance>] [--harness <name>] "
        + "[--seam ambient|urgent|reconcile] [--max-chars <n>] [--resident]  (exec-wire envelope(s) on stdin)";

    /// Strict argv parse: every violation collected, all-or-nothing (the
    /// house parser shape, applied to flags). A bad registration must fail
    /// LOUDLY at dispatch — exit 1 → handler failure → visible in the trail
    /// and counted by supervision — never quietly read the wrong mailbox.
    public static MailDigestOptions? TryParseArgs(
        IReadOnlyList<string> args, out IReadOnlyList<string> errors)
    {
        var errs = new List<string>();
        errors = errs;

        string? role = null, harness = null, instance = null;
        MailSeam seam = MailSeam.Ambient;
        int? maxChars = null;
        var resident = false;

        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--role" when i + 1 < args.Count:
                    role = args[++i];
                    break;
                case "--as" when i + 1 < args.Count:
                    instance = args[++i];
                    break;
                case "--harness" when i + 1 < args.Count:
                    harness = args[++i];
                    break;
                case "--seam" when i + 1 < args.Count:
                {
                    // Explicit name match (MailEnvelope.TryEnum's reasoning):
                    // Enum.TryParse also accepts "0", comma lists, and padded
                    // spellings — none are wire spellings we advertise.
                    var s = args[++i];
                    var name = Enum.GetNames<MailSeam>()
                        .FirstOrDefault(n => string.Equals(n, s, StringComparison.OrdinalIgnoreCase));
                    if (name is null)
                        errs.Add($"--seam must be ambient, urgent, or reconcile (got '{s}')");
                    else
                        seam = Enum.Parse<MailSeam>(name);
                    break;
                }
                case "--max-chars" when i + 1 < args.Count:
                    if (int.TryParse(args[++i], out var n) && n > 0) maxChars = n;
                    else errs.Add($"--max-chars must be a positive integer (got '{args[i]}')");
                    break;
                case "--resident":
                    resident = true;
                    break;
                default:
                    errs.Add($"unknown or incomplete argument '{args[i]}'");
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(role))
            errs.Add("--role is required: a digest without a role would have to guess whose mail to read");

        // Both halves of the address obey ONE grammar (ADR-0018 d2), checked
        // against the same predicate the envelope parser uses — never a second
        // spelling of it. A registration naming a role no sender could address
        // would be a mailbox nothing can ever reach, and a registration whose
        // `--as` is ungrammatical would be a mailbox no `to: role@instance`
        // could ever name: both are silent forever, which is the failure this
        // whole ADR exists to refuse. Loud at dispatch instead.
        if (!string.IsNullOrWhiteSpace(role) && !MailAddress.IsRole(role))
            errs.Add($"--role must match [a-z0-9][a-z0-9-]* (got '{role}') — "
                + "no sender could address a role spelled this way");
        if (instance is not null && !MailAddress.IsRole(instance))
            errs.Add($"--as must match [a-z0-9][a-z0-9-]* (got '{instance}') — "
                + $"no sender could address '{role}@{instance}'");
        // And the WHOLE address obeys the grammar's one length bound, for the
        // same reason: past it, no `to` can name this mailbox.
        if (role is not null && (instance is null ? role : $"{role}@{instance}") is { Length: > MailAddress.MaxChars } spelled)
            errs.Add($"the address '{spelled}' is {spelled.Length} characters; an address is at most "
                + $"{MailAddress.MaxChars} — no sender could write it");

        return errs.Count > 0
            ? null
            : new MailDigestOptions(
                role!, harness ?? "claude-code", seam,
                maxChars ?? (seam == MailSeam.Urgent
                    ? MailDigestOptions.DefaultUrgentMaxChars
                    : MailDigestOptions.DefaultMaxChars),
                resident, instance);
    }

    /// Strict parse of the exec-wire stdin envelope. The only writer is
    /// ExecWire.Envelope, so unknown or duplicate fields mean a skewed or
    /// corrupted wire — malformed, never guessed at. `payload` is accepted
    /// and ignored (the digest plans from the store, not the event body).
    public static DigestRequest? TryParseRequest(string line, out IReadOnlyList<string> errors)
    {
        var errs = new List<string>();
        errors = errs;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch (JsonException ex)
        {
            errs.Add($"not valid JSON: {ex.Message}");
            return null;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                errs.Add("envelope must be a JSON object");
                return null;
            }

            CheckFields(root, EnvelopeFields, "", errs);

            if (!root.TryGetProperty("v", out var v)
                || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var ver) || ver != ExecWire.Version)
                errs.Add($"'v' is required and must be the number {ExecWire.Version}");

            // dispatchId may be EMPTY: the engine writes `dispatchId:""` when
            // a collapsed run has no id (sentId = ctx.DispatchId ?? ""), and
            // the echo rule compares literal values — echoing "" matches "".
            string? dispatchId = null;
            if (!root.TryGetProperty("dispatchId", out var did)
                || did.ValueKind != JsonValueKind.String
                || (dispatchId = TryReadString(did)) is null)
                errs.Add("'dispatchId' is required and must be a string");

            string? type = null, sessionId = null;
            if (!root.TryGetProperty("event", out var ev) || ev.ValueKind != JsonValueKind.Object)
                errs.Add("'event' is required and must be an object");
            else
            {
                CheckFields(ev, EventFields, "event.", errs);
                if (!ev.TryGetProperty("type", out var t)
                    || t.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(type = TryReadString(t)))
                    errs.Add("'event.type' is required and must be a non-empty string");
                if (ev.TryGetProperty("sessionId", out var sid))
                {
                    if (sid.ValueKind != JsonValueKind.String
                        || (sessionId = TryReadString(sid)) is null)
                        errs.Add("'event.sessionId' must be a string when present");
                }
            }

            return errs.Count > 0 ? null : new DigestRequest(dispatchId!, type!, sessionId);
        }
    }

    private static readonly IReadOnlySet<string> EnvelopeFields =
        new HashSet<string> { "v", "dispatchId", "event" };
    private static readonly IReadOnlySet<string> EventFields =
        new HashSet<string> { "type", "sessionId", "cwd", "payload" };

    /// The pure planner (d5). `verbs` is the recipient spec's declared effect
    /// kinds for the event being dispatched — null when the spec does not
    /// declare the event at all, which delivers NOTHING (see the header: the
    /// gate is permissive there, but it noops after the advance, and that
    /// direction loses mail).
    public static MailPlan Plan(
        IReadOnlyList<PendingMail> pending, MailSeam seam, IReadOnlyList<string>? verbs)
    {
        var vehicle = VehicleFor(seam, verbs);
        if (vehicle == MailVehicle.None || pending.Count == 0)
            return new MailPlan([], MailVehicle.None, pending.Count);

        var eligible = seam == MailSeam.Urgent
            ? pending.Where(p => p.Envelope.Priority == MailPriority.Urgent).ToList()
            : pending.ToList();

        // Priority rank first (urgent > reconcile > ambient — the sender's
        // requested class is also its salience), then ARRIVAL order (offset
        // ascending): mail reads chronologically within a class, and held
        // items — which sit before the frontier by construction — surface
        // ahead of fresh ones, closest-to-expiry first.
        var deliver = eligible
            .OrderByDescending(p => Rank(p.Envelope.Priority))
            .ThenBy(p => p.Offset)
            .ToList();

        return deliver.Count == 0
            ? new MailPlan([], MailVehicle.None, pending.Count)
            : new MailPlan(deliver, vehicle, pending.Count - deliver.Count);
    }

    private static int Rank(MailPriority p) => p switch
    {
        MailPriority.Urgent => 2,
        MailPriority.Reconcile => 1,
        _ => 0,
    };

    private static MailVehicle VehicleFor(MailSeam seam, IReadOnlyList<string>? verbs)
    {
        if (verbs is null) return MailVehicle.None;
        return seam switch
        {
            // Turn-start / mid-turn content rides inject and nothing else:
            // degradation is DOWNWARD only, and a digest that answered
            // `decide` to get itself read would be steering the loop, not
            // informing it.
            MailSeam.Ambient or MailSeam.Urgent =>
                verbs.Contains("inject") ? MailVehicle.Inject : MailVehicle.None,
            // Turn end: inject FIRST, decide only when inject is absent. The
            // block shape (the reason IS the digest) is for events like Stop
            // whose ONLY loop verb is decide — that is what makes the block
            // the non-escalating choice there. Preferring decide would let a
            // `--seam reconcile` typo on a decide+inject mid-turn event
            // (PreToolUse) turn a status message into a DENIED tool call
            // (the skeptic pass's finding 3): mail must never escalate
            // itself into a deny when a plain inject can carry it.
            _ => verbs.Contains("inject") ? MailVehicle.Inject
                : verbs.Contains("decide") ? MailVehicle.Decide
                : MailVehicle.None,
        };
    }

    /// Deterministic bounded rendering (d5/d10). `maxChars` bounds the sum of
    /// ITEM blocks (bodies are the unbounded part) at whole-item granularity —
    /// an item the cap excludes is NOT delivered and stays pending, converging
    /// FIFO across seams. Two guarantees the skeptic pass forced into words:
    ///
    ///   * A PROVENANCE HEAD ALWAYS RENDERS WHOLE. When the first item alone
    ///     exceeds the cap it is delivered truncated with an explicit marker
    ///     (held-forever is mail lost; the full text stays durable in the
    ///     store) — but only the BODY is ever cut. Cutting from the block's
    ///     front could erase the id and sender (a sender-controlled topic
    ///     longer than the cap, or a pathological `--max-chars`), advancing
    ///     the cursor past mail the recipient could never even look up.
    ///   * EVERYTHING OUTSIDE THE CAP IS BOUNDED. Head fields are
    ///     sender-controlled strings up to the 128KiB line cap, so each is
    ///     display-clamped; the expired parenthetical names a count plus at
    ///     most three clamped ids, never the whole list. The cap may
    ///     therefore be exceeded only by a bounded constant, never by data.
    public static MailRender Render(
        MailPendingView view, MailPlan plan, int maxChars)
    {
        const string marker = "\n   [truncated to fit the delivery budget — full text is durable in the mail store]";

        var sb = new StringBuilder();
        var delivered = new List<PendingMail>();
        var used = 0;

        foreach (var (item, index) in plan.Deliver.Select((p, i) => (p, i)))
        {
            var (head, bodyPart) = ItemBlock(item, index + 1, view.Deliveries);
            var block = head + bodyPart;
            if (used + block.Length > maxChars)
            {
                if (index > 0) break;   // whole-item cap: the tail stays pending
                // The head survives whole; the body is cut to what remains.
                var allowance = Math.Max(0, maxChars - head.Length - marker.Length);
                if (allowance > 0 && allowance < bodyPart.Length
                    && char.IsHighSurrogate(bodyPart[allowance - 1])) allowance--;
                block = head + bodyPart[..Math.Min(allowance, bodyPart.Length)] + marker;
            }
            sb.Append(sb.Length > 0 ? "\n\n" : "").Append(block);
            used += block.Length;
            delivered.Add(item);
        }

        var heldByCap = plan.Deliver.Count - delivered.Count;
        var heldTotal = plan.HeldByPlan + heldByCap;

        var text = new StringBuilder();
        text.Append($"[captAInHook mail] {delivered.Count} message(s) for '{view.Role}':\n\n");
        text.Append(sb);
        if (view.Expired.Count > 0)
        {
            text.Append($"\n\n({view.Expired.Count} expired undelivered after their full ttlDeliveries of opportunities: ")
                .Append(string.Join(", ", view.Expired.Take(3).Select(e => Clamp(e.Envelope.Id))));
            if (view.Expired.Count > 3) text.Append(", …");
            text.Append(')');
        }
        if (heldTotal > 0)
            text.Append($"\n\n(+{heldTotal} more message(s) pending, held for a later delivery opportunity)");

        return new MailRender(text.ToString(), delivered, heldByCap);
    }

    /// One rendered item as (provenance head, body part). The head (d10 — the
    /// recipient always sees who is speaking) is bounded by construction:
    /// every sender-controlled field is display-clamped. The id sits BEFORE
    /// the topic so no rendering path can lose it — it is the join key from a
    /// digest line back to the durable store line (d10's causality chain) and
    /// the handle an answer quotes in `inReplyTo`. Age is measured in delivery
    /// opportunities — the only clock this layer has (d3).
    ///
    /// `reply to <address>` (ADR-0018 d4, `answer-by-address`) is rendered
    /// whenever the sender set one, and rendered in the HEAD rather than left
    /// to the body: the reader that has to answer is very often a model, and
    /// the return address it should write into `to` has to be where its eye
    /// lands, in the grammar it should copy verbatim. NOT clamped, on
    /// purpose: the grammar bounds the alphabet and not the length, so a legal
    /// address can be long — but a truncated return address is worse than
    /// none, because a model will copy it exactly as shown and the answer
    /// goes to a mailbox that does not exist. The head is bounded anyway by
    /// the whole-item budget in `Render` (an item that cannot fit is cut with
    /// a marker), so an absurd address costs its own delivery, not the digest.
    private static (string Head, string BodyPart) ItemBlock(
        PendingMail item, int ordinal, long deliveries)
    {
        var e = item.Envelope;
        var age = item.SeenAt is { } seen
            ? $"waited {deliveries - seen + 1} opportunit{(deliveries - seen + 1 == 1 ? "y" : "ies")}"
            : "new";
        var head = $"{ordinal}. from {Clamp(e.From.Agent)} ({Clamp(e.From.Harness)}) · "
            + $"{Wire(e.Kind)}/{Wire(e.Priority)} · id {Clamp(e.Id)} · topic: {Clamp(e.Topic)} · {age}"
            + (e.ReplyTo is not null ? $" · reply to {e.ReplyTo}" : "");
        if (e.Body.Length == 0) return (head, "");
        var body = string.Join('\n', e.Body.Split('\n').Select(l => "   " + l));
        return (head, "\n" + body);
    }

    /// Display clamp for sender-controlled head fields — the head must be
    /// bounded whatever a sender wrote (an id or topic can legally run to the
    /// 128KiB line cap). Shared with the store's `mail.append` provenance so
    /// the two surfaces clamp identically and their ids join verbatim.
    private static string Clamp(string s) => MailEnvelope.ClampField(s);

    /// Run the verb: oneshot (one envelope line, one answer, exit) unless
    /// `--resident`, which speaks ADR-0010 d3's lock-step protocol —
    /// `{"ready":1}` first, then one answer line per envelope line until EOF.
    /// The digest is stateless between envelopes (the cursor is disk), so the
    /// resident form is the same body in a loop — it exists because an
    /// urgent-class registration fires per tool call, where a cold JIT start
    /// per dispatch is exactly the tax ADR-0004 d7 killed.
    public static int Run(
        IReadOnlyList<string> argv, TextReader stdin, TextWriter stdout, TextWriter stderr,
        string? mailDir = null, string? harnessDir = null)
    {
        var opts = TryParseArgs(argv, out var argErrors);
        if (opts is null)
        {
            stderr.WriteLine("captainHook mail digest: bad arguments:");
            foreach (var e in argErrors) stderr.WriteLine($"  {e}");
            stderr.WriteLine(Usage);
            return 1;
        }

        // Validate the registration LOUDLY at start (a typo'd --harness must
        // escalate, not quietly noop forever) — but resolve the spec again
        // per envelope below: a resident lives across the daemon's own
        // hot-reload contract ("edit a spec, effective next hook"), and a
        // spec view frozen at spawn could plan an inject the daemon-side
        // capability gate then swallows AFTER the cursor advanced — silent
        // mail loss (the skeptic pass's finding 6).
        var harnesses = new ReloadingHarnessRegistry(harnessDir);
        try { harnesses.Current.Get(opts.Harness); }
        catch (InvalidOperationException ex)
        {
            stderr.WriteLine($"captainHook mail digest: {ex.Message}");
            return 1;
        }

        var cursors = new MailCursors(new MailStore(MailStore.ResolveDir(mailDir)));

        if (opts.Resident)
        {
            stdout.WriteLine("""{"ready":1}""");
            stdout.Flush();
            string? line;
            while ((line = stdin.ReadLine()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var code = Answer(line, opts, harnesses, cursors, stdout, stderr);
                stdout.Flush();
                // In lock-step a bad envelope must not abort the loop — the
                // engine is mid-ask and an exit would fail EVERY later
                // dispatch of this generation; the addressed noop answer is
                // the failure surface (stderr carries the reason).
                _ = code;
            }
            return 0;
        }

        var one = stdin.ReadLine();
        if (one is null || string.IsNullOrWhiteSpace(one))
        {
            stderr.WriteLine("captainHook mail digest: expected one exec-wire envelope on stdin");
            stderr.WriteLine(Usage);
            return 1;
        }
        return Answer(one, opts, harnesses, cursors, stdout, stderr);
    }

    /// One envelope → one answer line on stdout. THE ORDER IS THE CONTRACT:
    /// plan → render → ADVANCE THE CURSOR → emit. Anything that fails before
    /// the advance answers noop and the cursor has not moved (mail redelivers
    /// at the next seam); a crash after the advance loses that digest rather
    /// than doubling it (d4's chosen direction).
    private static int Answer(
        string envelopeLine, MailDigestOptions opts, ReloadingHarnessRegistry harnesses,
        MailCursors cursors, TextWriter stdout, TextWriter stderr)
    {
        var req = TryParseRequest(envelopeLine, out var reqErrors);
        if (req is null)
        {
            // The error reply must still be ADDRESSED: a resident answer
            // without the dispatchId echo is itself a protocol error the
            // engine kills the conversation over (the skeptic pass's finding
            // 4), so a best-effort id is lifted from the unparseable line —
            // addressing a failure report is not guessing at mail. When even
            // that fails (true garbage), the engine's protocol kill IS the
            // honest outcome for a corrupted wire.
            stderr.WriteLine("captainHook mail digest: envelope on stdin is malformed — answering noop:");
            foreach (var e in reqErrors) stderr.WriteLine($"  {e}");
            stdout.WriteLine(Noop(BestEffortDispatchId(envelopeLine)));
            return 1;
        }

        HarnessSpec spec;
        try { spec = harnesses.Current.Get(opts.Harness); }
        catch (InvalidOperationException ex)
        {
            // The harness vanished mid-flight (an override dir edit): degrade
            // to noop and keep serving — the registration was valid at start,
            // and a resident that exits here fails every later dispatch.
            stderr.WriteLine($"captainHook mail digest: {ex.Message} — answering noop");
            stdout.WriteLine(Noop(req.DispatchId));
            return 0;
        }

        var verbs = spec.Events.TryGetValue(req.EventType, out var declared) ? declared : null;
        // The cursor keys on the INSTANCE; the trail keeps the real session;
        // and the ADDRESS decides what this registration is entitled to read —
        // its role's broadcast, plus its own unicast when it is named (d4).
        var view = cursors.Pending(opts.Mailbox, req.SessionId);
        var plan = Plan(view.Pending, opts.Seam, verbs);

        if (plan.Vehicle == MailVehicle.None || plan.Deliver.Count == 0)
        {
            // Nothing deliverable HERE — no advance, no TTL burn: a quiet
            // seam must not age mail (the cursor's planner obligation).
            stdout.WriteLine(Noop(req.DispatchId));
            return 0;
        }

        var render = Render(view, plan, opts.MaxChars);

        switch (cursors.Advance(view, render.Delivered.Select(p => p.Offset).ToList()))
        {
            case MailCursorWrite.Failed f:
                // A lost lock race or a stale view is a legitimate concurrent
                // delivery, not a defect: the mail is (or is being) handled;
                // answer noop and let the next seam re-read.
                stderr.WriteLine($"captainHook mail digest: cursor did not advance — answering noop: {f.Error}");
                stdout.WriteLine(Noop(req.DispatchId));
                return 0;

            case MailCursorWrite.Written:
                stdout.WriteLine(AnswerLine(plan.Vehicle, render.Text, req.DispatchId));
                LogDelivery(req, opts, view, plan, render);
                return 0;

            default: throw new InvalidOperationException("MailCursorWrite is a closed set");
        }
    }

    /// The exec-wire answer, built by writer (never string concat): inject
    /// carries the digest as `text`; the reconcile block is decide/deny with
    /// the digest as `reason` (deny at a Stop-class seam means "do not stop
    /// yet" — the event-appropriate rendering is the adapter's job, phase 5).
    /// The dispatchId is echoed on every answer: resident MUST, oneshot may.
    private static string AnswerLine(MailVehicle vehicle, string digest, string dispatchId)
    {
        var buf = new ArrayBufferWriter<byte>(digest.Length + 64);
        using (var w = new Utf8JsonWriter(buf))
        {
            w.WriteStartObject();
            if (vehicle == MailVehicle.Inject)
            {
                w.WriteString("effect", "inject");
                w.WriteString("text", digest);
            }
            else
            {
                w.WriteString("effect", "decide");
                w.WriteString("verdict", "deny");
                w.WriteString("reason", digest);
            }
            w.WriteString("dispatchId", dispatchId);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buf.WrittenSpan);
    }

    /// `mail.deliver` (d10) — delivery as a FIRST-CLASS ledger event, the
    /// join that closes the cross-agent causality chain: envelope
    /// (`from`/`id`/`inReplyTo`, durable in the store) → this event (envelope
    /// ids ↔ dispatchId ↔ recipient session) → the recipient's own later hook
    /// events in that session. "Why did agent A do X" becomes answerable by
    /// reconstructing exactly what A was shown.
    ///
    /// FOUR things this emitter decides, each with a direction of failure:
    ///
    ///   * ONLY ON A REAL DELIVERY. Every earlier return — no vehicle, nothing
    ///     eligible, a malformed envelope, a cursor that refused to advance —
    ///     answered noop and consumed nothing, and an event there would put a
    ///     delivery on the ledger that no recipient ever saw. The one
    ///     `Written` branch is the only place mail has actually been consumed.
    ///   * AFTER the answer is written, never before — the `mail.expire`
    ///     ordering rule (the ledger states facts): a crash between the two
    ///     leaves the ledger SILENT about a delivery rather than asserting one
    ///     that never reached stdout. The ledger may under-claim; it may never
    ///     claim falsely.
    ///   * IDS AND HASH, NOT BODIES (d10). The bodies are already durable in
    ///     the mail store — the trail stays lean, and `renderHash` (SHA-256
    ///     over the exact UTF-8 the effect carries, `PolicyContent.Of`'s
    ///     stamp shape) makes the RENDERING tamper-evident: the store proves
    ///     what was written, this proves what was shown. It hashes
    ///     `render.Text` — what the cap actually produced, truncation
    ///     included — because a hash of the plan would attest to text nobody
    ///     received.
    ///   * BOUNDED BY DATA, never by a sender. Ids are sender-controlled up to
    ///     the 128KiB line cap, so each is display-clamped by the SAME clamp
    ///     the digest head uses — which also makes the ledger id and the
    ///     digest line's id the identical string, so the two surfaces join to
    ///     each other verbatim. The count is bounded by `--max-chars`
    ///     (deployment configuration), which is the same bound the digest text
    ///     itself carries.
    ///
    /// As-built note on d10's shape: the ADR sketches `recipient: {role,
    /// session}`, but `sessionId` is a first-class trail column that every
    /// existing filter — the JSONL consumers, the API stream, the GUI trace —
    /// reads at the top level; nesting it would make mail delivery the one
    /// event invisible to a session filter. The session therefore rides
    /// `sessionId` and `role` rides data, so the join keys are unchanged and
    /// strictly better connected.
    private static void LogDelivery(
        DigestRequest req, MailDigestOptions opts, MailPendingView view,
        MailPlan plan, MailRender render)
    {
        var bytes = Encoding.UTF8.GetBytes(render.Text);
        var data = new Dictionary<string, object>
        {
            ["role"] = opts.Role,
            ["seam"] = Wire(opts.Seam),
            // The vehicle is not derivable from anything else on the ledger,
            // and it is the difference between informing the loop and blocking
            // it (an inject vs. a reconcile-class block).
            ["vehicle"] = Wire(plan.Vehicle),
            ["envelopeIds"] = render.Delivered.Select(p => Clamp(p.Envelope.Id)).ToList(),
            ["renderHash"] = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            ["bytesInjected"] = bytes.Length,
        };
        // WHICH MAILBOX took it (ADR-0018 d4). `sessionId` names the window and
        // `role` the lane; neither says which of a role's mailboxes this
        // delivery landed in, and for a unicast envelope that is the entire
        // fact — the whole point of the address was that ONE box got it.
        //
        // Spelled as `instance` beside `role`, exactly as `mail.cursorAdvance`
        // spells it, rather than as a joined `address` field: two spellings of
        // "which mailbox" on one trail is the second implementation this
        // subsystem keeps refusing to grow (N8), and a reader that already
        // learned the advance's columns needs nothing new here. Written ONLY
        // when the mailbox is named, so every delivery line a pre-ADR-0018
        // reader has seen keeps its exact shape.
        if (view.Named) data["instance"] = view.Session!;

        Log.Info("mail", "mail.deliver", new LogFields
        {
            // An empty dispatchId is what a collapsed run puts on the wire; as
            // a join key it joins nothing, so it is absent rather than blank.
            DispatchId = req.DispatchId.Length == 0 ? null : req.DispatchId,
            SessionId = req.SessionId,
            HookEvent = req.EventType,
            Msg = "mail delivered into the recipient's context",
            Data = data,
        });
    }

    /// Lift a dispatchId out of a line the STRICT parse rejected, so the
    /// error reply can still be addressed (see the malformed branch above).
    /// Lenient on purpose and used for NOTHING but the echo.
    private static string? BestEffortDispatchId(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("dispatchId", out var d)
                && d.ValueKind == JsonValueKind.String)
                return TryReadString(d);
        }
        catch (JsonException) { }
        return null;
    }

    private static string Noop(string? dispatchId) =>
        dispatchId is null
            ? """{"effect":"noop"}"""
            : $$"""{"effect":"noop","dispatchId":"{{JsonEncodedText.Encode(dispatchId)}}"}""";

    private static void CheckFields(
        JsonElement obj, IReadOnlySet<string> known, string prefix, List<string> errs)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var prop in obj.EnumerateObject())
        {
            if (!seen.Add(prop.Name)) errs.Add($"duplicate field '{prefix}{prop.Name}'");
            else if (!known.Contains(prop.Name)) errs.Add($"unknown field '{prefix}{prop.Name}'");
        }
    }

    /// The deferred-unescape guard (doc/scratch.md's pending consolidation).
    private static string? TryReadString(JsonElement e)
    {
        try { return e.GetString(); }
        catch (InvalidOperationException) { return null; }
    }

    private static string Wire<T>(T value) where T : struct, Enum =>
        value.ToString()!.ToLowerInvariant();
}
