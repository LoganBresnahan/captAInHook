# Roadmap

Living document — what to build and in what order. Decisions get ADRs
(`doc/adr/`), mechanics get flow docs (`doc/flow/`); this file only orders the
work. Check items off in the commit that lands them; reorder freely.

**The product vision this points at:** a runtime for managing custom
hooks/skills for AI agents — browse, one-click install (writes
`~/.claude/settings.json` / `.claude/skills/`), configure, and *watch them
run live*. The framework underneath is what exists today.

## Now

- [x] **21. The Mail view — watch the bus live, read-only** — item 20 built
  the bus; nothing shows it. Per **ADR-0016 d14** (amended 2026-08-15): a
  sixth GUI view whose body is a zoomable SVG canvas drawing the *mechanism*
  — the ledger as the spine (mail never moves; cursors move past it), each
  `to` role a lane, each session a cursor sliding along it tagged with the
  seam it landed at, held envelopes with TTL countdown, expired greyed;
  semantic zoom (far: roles + pulse; mid: sessions/cursors/frontiers; near:
  envelope cards with provenance, chain link, and their `mail.deliver`
  records). Live from the trail SSE that already exists (`mail.*` events are
  the whole choreography); snapshot from a new read-only
  `GET /api/v1/mail?since=`. **Observation is not delivery** — pinned three
  ways (no append/advance handle in the read model, no non-GET route under
  `/mail`, "delivered" only from a ledger line); presence is inferred with
  fade, never claimed; sending from the GUI is explicitly NOT here (a
  consent decision of its own). ADR-0015 d1 amended for the sixth entry.
  Build order: ADR-0016 § Implementation plan → Addendum (7 slices → 5
  phases, one optional; critical path `mail-read-endpoint → mail-reducer →
  mail-canvas → mail-live-choreography → mail-view-docs`; adversarial verify
  on the reducer only, **fable / high** — the one place a wrong pass paints a
  false picture no screenshot can catch; endpoint + provenance-fields
  opus/medium + opus-or-sonnet/low in one sitting; canvas opus/**high** under
  `/ui-loop`; choreography opus/medium; replay optional opus/low; docs
  opus/low). Standing hazards named: N8 — two implementations of "pending",
  the reducer is an interpolator between authoritative snapshots, never a
  second store; the trail's days-to-weeks lifetime means older envelopes show
  *before cursor · no record*, never "delivered".
  **2026-08-16/17 (dogfood):** the first real exchange rode the bus — a
  second window holding `reviewer` (d8 as-built: a second digest handler,
  cwd-scoped by `dispatch.json`) reviewed 1ee7218 and found three real bugs,
  fixed in d6083f9. Verdict on optional slice 6 `mail-replay` from watching
  it: **build**, as (a) a delivery-record preload (fold `mail.deliver`
  history so cards read ✓ for pickups before page-open — the thing that read
  *"is past it · no delivery record in this picture"* all day) then (b) the
  scrub bar. **As it turned out: 6a landed 2026-08-17 and answered the
  complaint; 6b was DECLINED the same day on the field report's evidence** (the
  trail is queryable JSONL — reading it is how that report was written — and
  ADR-0016's addendum records the reasoning, the unavailable mechanism, and the
  revisit trigger).
  What the exchange asked for beyond this item is **ADR-0017** (item 22).
  Slices landed: `mail-view-docs` (2026-08-17; phase 5, slice 7, the close —
  the field report first, because it is the evidence the optional slice was
  supposed to be decided from. `doc/dogfood/2026-08-17-the-bus-becomes-visible.md`
  records two days of the real bus: the first exchange that did WORK rather than
  demo (request → reply → addressed, three envelopes, no human relay, three real
  SSE bugs fixed in `d6083f9`); 6a proved live (23 delivery records spanning ~37
  hours and naming 75 pickups, folded into a page that had streamed nothing,
  `deliveriesComplete: true`); the human channel deployed and **unplaced** — the
  count renders in the TUI and NOT in the VS Code extension, the maintainer's own
  primary window, which is ADR-0017's "harness with no passive display" hit first
  by the harness in daily use; three readings of one bus all correct (canvas 6
  pending, a fresh terminal 9, this window blank) because pending belongs to a
  CURSOR and not to a role; two things only the canvas showed — observation
  really is not delivery (a terminal read its count for minutes and created no
  cursor; the track appeared on its first prompt, taking all 9 at once) and dead
  cursors hold mail forever (4 of 7), which drove ADR-0018 the same evening; the
  digest's measured per-prompt tax (median 75.0ms, n=92, max 420.6) that the
  status line does not levy; and the intrusion the bus made visible — a
  `mail.deliver` for session `s1` naming 7 envelopes, which was `/shipshape`'s
  stdout-purity probe running the dev shim BARE against the live tree, firing the
  maintainer's own digest and leaving a phantom cursor on a real lane (fixed in
  `495bf5c`; a verification step that dispatches through the operator's hooks is
  a WRITE to their state however read-only its intent). **6b declined on that
  evidence** and recorded as a rejected alternative rather than a silent skip:
  the row as written could not be built anyway (a client cannot ask for an older
  `Last-Event-ID` — the id is opaque, ADR-0009 d2, the same wall 6a hit), the
  debugging case is answered by reading the trail directly, and what a scrub bar
  would still add is PATTERN rather than fact — revisit when ADR-0017's watcher
  runs unattended turns. Docs: `doc/flow/mailbox-bus.md` gains § *The observation
  surface* (the read path as a diagram, the three ways "observation is not
  delivery" is pinned, N8's interpolator rule, the stamp that joins snapshot to
  stream, and what is deliberately NOT built) and ground-truth rows for the
  endpoint and the canvas; ADR-0016's Ground truth gains the four d14 rows its
  reducer row had promised would "land with their slices" — endpoint, canvas,
  live choreography, preload — which had accrued as debt. Every file and symbol
  in the new rows checked mechanically, and three test counts copied from older
  prose were WRONG and are now the measured ones (`MailApiTests` 31 not 30,
  `mailCanvas.test.ts` 40 not 37, `mailStream.test.ts` 11 not 9). **Item 21
  complete: 7 slices planned, 6 built, 1 declined with its reasoning on the
  record.**)
  Slices landed: `mail-replay-preload` (2026-08-17; phase 4, slice 6a — the
  half of `mail-replay` the dogfood pass asked for first. `delivered` comes from
  a `mail.deliver` line and nowhere else (d14 pin iii) and a live stream starts
  NOW, so every pickup older than the page read *before cursor · no record* —
  honest about the picture, and wrong-looking about mail that had plainly been
  read; it is what the whole dogfood day looked at. The rule did not move: the
  daemon now hands the picture the older LINES. `MailDeliveryFold` scans the
  trail file and `MailDto` carries the records, because **the client cannot ask
  for them** — the SSE resume id is an opaque token (ADR-0009 d2), so "subscribe
  from an id older than the stamp" is arithmetic the contract forbids, and the
  one constant a client may spell, `"0"`, replays the entire trail as live,
  which is precisely what `mail-stream-alignment` exists to prevent. Read AFTER
  the stamp on purpose, so the window can only ever duplicate (dropped by
  content identity) and never lose. The fold ships the ledger line's columns
  VERBATIM and no offsets: placing an envelope id on the ledger is the
  reducer's arithmetic, and a second implementation in C# is N8 wearing a
  different hat — so `resolveDelivery` is lifted out of `onDeliver` and the
  preload places records by the SAME rule the live path uses for a record that
  arrives without its advance. `deliveriesComplete` is a narrow, checkable
  claim — this fold read the whole file from byte 0 and trimmed nothing — and
  is false for a scan window, a hit cap, and equally for a trail that is
  absent or unserved, because none of those can prove nobody read anything;
  the detail card spends it on the one sentence that changes ("no record"
  vs "no record — the fold reaches back only so far"). Two rules keep a
  historical fact from becoming a present claim: a preloaded record raises no
  anomaly when it cannot be placed (the fold reaches only as far as the trail
  does, so an unplaceable record is the EXPECTED case, unlike the live path's
  `deliver-unmatched`), and it moves no cursor and claims no presence — the
  live path may infer a cursor from a delivery it watched arrive, a seed may
  not, since its cursors are the daemon's own view. Two traps pinned rather
  than trusted: the substring gate is a filter and never a decision (payload
  stderr puts arbitrary text in this file, so a forged `mail.deliver` inside
  an `exec.stderr` line must contribute nothing — driven), and the read is
  guarded against the deferred-unescape trap the policy skeptic pass found
  (a lone surrogate parses fine and throws at `GetString`, which would turn
  one bad log line into a 500 on the whole snapshot). The e2e is the claim
  itself: a delivery happens, the page that watched it is thrown away, and a
  RELOADED page reads the same envelope as delivered with `data-arrival=
  "snapshot"` — nothing came over the stream to make it so. It also falsified
  a premise two specs had inherited from the old blindness (`delivered` count
  0 on first paint); the seed's own pickups now read ✓ before a single frame
  arrives, which is the fix, so the assertions moved onto the envelope each
  test actually makes. 11 C# tests (`MailDeliveryPreloadTests`), 5 web units,
  1 e2e; C# 960 → 971 green twice, web units 244 → 249, e2e 41 → 42 per
  browser (84 across both) green;
  Mail snapped at all three tiers × both themes and read — the delivered
  glyphs are green on a picture that streamed nothing.)
  Slices landed: `mail-read-endpoint` (2026-08-15; phase 1, slice 1 —
  `GET /api/v1/mail?since=` returns the bus as one snapshot: chain status
  (`VerifyChain`, head, gen, line count, and the 0700/0600 modes d13 promises,
  SHOWN rather than asserted), every line from the offset with its parsed
  envelope, one `MailCursors.Pending` view per cursor file, and inferred
  presence. **The write half is not reachable, by construction**: the API is
  handed a `MailReadPort` — five METHOD-GROUP delegates bound to Read /
  VerifyChain / HeadHash / List / Pending, built by a factory that creates the
  store and the cursors privately — rather than an interface, which the
  writable type would implement and one cast would re-open. Pinned all three of
  d14's ways plus the honest bound: a reflection walk over the read model's
  DECLARED graph (ctor params + fields, transitively through engine types)
  reaches `MailReadPort` and never `MailStore`/`MailCursors` and finds no
  Append/Advance — and because the captured closures DO hold those objects
  where reflection cannot see, a source pin asserts no file under `Api/` NAMES
  either type or verb (which is why the port re-exports `Gen` and `FilePath`:
  the pin is only meaningful if nothing there needs them); a route-table theory
  drives PUT/POST/DELETE/PATCH on `/mail` and two sub-paths, asserting 404 AND
  that the store's bytes are unchanged and no cursor file was created —
  **asking cannot create a mailbox**. `since` is the cursor's own address
  space: absent ⇒ 0 (a fresh snapshot wants everything), and an offset resting
  on no line boundary answers `sinceAligned: false` rather than splicing a
  fresh tail onto a prefix that is gone, while a malformed one is a 400 —
  refused, not defaulted, because a silently-full store looks to a reducer
  exactly like a legitimate resnapshot, the one failure no screenshot catches.
  The torn tail reads the way the digest reads it: reported as a line with
  `terminated: false`, never behind the frontier, pinned equal to
  `Pending().Frontier` on the same store. Presence is cursor files ∪ recent
  dispatch sessions and says which half it knows: `SessionPresence` (ServeStats'
  sibling — bounded at 64, oldest evicted, stamped in `DispatchOneAsync` BEFORE
  the policy gate, since a session whose hooks are denied is still here) reports
  an AGE off the monotonic clock, never a timestamp, so no browser has to
  reconcile two wall clocks; a cursor-only session reads `lastDispatchAgeMs:
  null` (quiet, or the daemon restarted) and a dispatch-only session holds no
  role yet. Cursor listing is a RECOGNITION, not a parse: `MailCursors.List` /
  `TryParseCursorFileName` / `Dec` accept only names `Enc` could have produced
  (round-trip checked), so lock files and atomic-write temps fall out by the
  same rule rather than by a special case — and the drive corrected the test's
  own premise, since a well-formed hand-written cursor file IS a cursor by
  every rule the digest applies to it. Schema codegen gained a
  `TransformSchemaNode` that titles nested DTO nodes, because
  json-schema-to-typescript names an inlined object after its PROPERTY and
  deduped `pending`/`expired` into a hoisted `Items`; titles are stamped on
  plain object nodes only — probed, a titled `["object","null"]` union makes
  the generator reference a type it never declares. 26 tests
  (`MailApiTests`); suite 912 → 938 green twice.)
  `mail-append-provenance-fields` (2026-08-15; phase 1, slice 2 —
  `mail.append` gains `from` (nested agent/harness/session, absent-means-omit
  for the write-only member's missing session), `kind`, `topic`, `priority`
  and `ttlDeliveries`, so the canvas's arrival animation can CHARACTERIZE an
  envelope instead of drawing an anonymous dot. The `body` deliberately does
  NOT travel: the trail is operational-lifetime and payload-readable —
  `exec.stderr` already lands arbitrary process output in it — while the store
  has the lifetime and the modes for content, and the pin is a runtime one
  (every event an append emits is scanned for both the body text and the key,
  so a future field that inlines the envelope fails in the suite rather than
  in someone's trail). The two sender-controlled strings are CLAMPED: an id or
  topic may legally run to the 128KiB line cap, and the clamp is now one
  spelling — `MailEnvelope.ClampField`, lifted out of the digest head's
  private copy — so a clamped id on the ledger and in a rendered digest are the
  same string and join verbatim; the store keeps what the sender wrote, in
  full. Pinned as a golden CROSS-EMITTER line in `WireJsonlTests` (both
  renderings byte-identical, the literal line asserted, no `body` key)
  even though only the engine emits mail today: the trail is one schema across
  two emitters and a reducer is about to read these keys by name, so a rename
  should surface as a byte diff, not a silent field the canvas stops finding.
  4 tests; suite 938 → 942 green twice. **Phase 1 complete.**)
  `mail-reducer` (2026-08-15; phase 2, slice 3 — `web/src/mail.ts`, the pure
  model the canvas will draw: `seedMail(MailDto)` then `reduceMail(state,
  {line|gap|reset})`, selectors `projectCursor` / `lineStatus` /
  `deliveriesFor` / `presenceTier`; no clock, no network, no DOM — the caller
  passes `atMs`, so presence decays in the browser's monotonic clock and never
  reconciles two wall clocks. **The first drive found the trail could not name
  the cursor an advance moved**: `mail.cursorAdvance` / `mail.expire` / the
  re-anchor family carried a role and no session — unattributable the moment a
  role has two sessions, and a canvas that guessed would draw a cursor moving
  that did not (the one wrong picture no screenshot catches). So the engine
  side landed first, as an as-built amendment to d14: the cursor family gains
  the `sessionId` column (d10's rule for `mail.deliver`, so one session filter
  sees the whole choreography); the advance carries `deliveredOffsets` beside
  its count and the expire its `offset` beside its id, because ids are not
  unique on this bus and a count says how many, never which; the re-anchor
  carries the `deliveries` it preserves; `mail.append` carries `bytes` so the
  store's frontier is derivable from the tail alone (an append landing anywhere
  but offset+bytes+1 is a hole the reducer can NAME); and `MailPendingView` /
  `MailCursorDto` gain the cursor's own `offset` — its position, distinct from
  `frontier` (the store's end) — since a role with no fresh mail left the
  position unknowable. Golden trail lines for advance + expire join append's in
  `WireJsonlTests`. **N8's mitigation is now mechanical, not a promise**:
  `MailReducerGoldenTests` drives 15 scenarios through the REAL store, cursors
  and digest verb — first contact, hold-then-expire across three seams, two
  sessions on one role, re-anchor preserving deliveries, a torn tail terminated
  into a counted malformed line, a `?since=` partial ledger (exact, because
  cursor state is materialized from the DTO's held/fresh items with their ttl,
  never re-derived from lines the picture may not have) and its re-anchor
  variant (honestly NOT exact: flagged), a deleted cursor restarting its
  lineage, the sessionless reader, a hold-only advance, a refused stale view, a
  vanished lineage, the reconcile seam's `decide` — capturing (before
  snapshot, trail, after snapshot) per scenario into `web/src/mail.golden.json`
  with `ts` pinned and the temp dir spelled `<mail>`, asserted deterministic
  and pinned as a drift detector on `ApiSchemaTests`' precedent (regenerate
  with `CAPTAINHOOK_SCHEMA_UPDATE=1`); `web/src/mail.test.ts` replays every
  scenario and requires the projected pending set to equal C#'s per cursor,
  per offset, per seenAt — the reducer's truth is re-derived from the engine's
  every time the engine moves. Three rules shape the reducer and each has a
  test: APPLY what an event states, DERIVE what the ledger proves, FLAG what
  neither gives — `deliveries` on an advance is a per-cursor sequence number,
  so a stale replay (the stream opened before the snapshot) is ignored, a
  skipped one marks the cursor `uncertain` and raises `resnapshot`, and a
  first advance (deliveries 1, always from an anchor at 0) is reconstructed
  exactly from a complete ledger — which also recovers d13's quiet corner, the
  deleted-and-silently-re-anchored cursor, that the engine itself cannot be
  loud about; every advance's re-derived held/expired set is CROSS-CHECKED
  against the counts the event carries; an event with a field the reducer
  cannot read is refused whole, never defaulted (a defaulted offset is
  indistinguishable from a legitimate one downstream). DELIVERED comes from a
  `mail.deliver` record and nowhere else (pin iii): an advance without its
  deliver leaves the envelope `passed` — *before cursor · no record* — and
  records carry across a re-seed because they are ledger facts a snapshot
  cannot contain, the one thing that survives the replace. And every
  re-snapshot REPLACES reduced state (`seedMail`), on `Gap`, `Reset`, a
  misaligned `since`, or any of the reducer's own findings. C# 947 (943 + 2
  goldens + 2 wire pins) green twice; web `npm test` 43 in `mail.test.ts`.
  **The adversarial pass** (an independent skeptic, `mail.skeptic.test.ts`,
  34 tests kept as regression pins; a 500-seed differential against a C#-
  semantics model survived) found nine, one of them the exact silent-wrong
  picture the plan feared: a re-anchor whose CAUSE is the store changing (a
  truncation, a replaced chain) rebuilt the fresh set from lines that no
  longer existed, counts matched, nothing flagged. The reason was prose, so
  the engine now says which side broke — `mail.cursorReanchor` gains `cause:
  "cursor" | "store"` (ambiguous held-mismatch cases are `store`, the
  direction that costs a re-read rather than a lie); the reducer rebuilds
  nothing on `store` and raises `resnapshot`; a 16th golden scenario truncates
  a real store under a real cursor. The rest, all fixed and pinned: a replayed
  `mail.deliver` duplicated its record (identity is now its content — a record
  has no sequence number); an old lineage's advance replayed after a restart
  moved the position BACKWARDS (a real advance never does, so anything behind
  the picture is stale whatever its count); a lower count at the same offset
  was flagged where it is stale — except count 1, which is also what a
  deleted-and-restarted cursor with nothing new appended looks like, and the
  reducer keeps the flag there because the two are indistinguishable and the
  wrong reading is silent and lasting; a snapshot that caught an append in
  flight (the torn tail WAS that line's first bytes) refused the line's own
  append instead of completing it; a replayed re-anchor with a lower carried
  count rewound the cursor; `sessionId: ""` made a phantom second cursor for
  the sessionless reader (engine-side `CursorPath` normalizes "" to null); a
  `null` trail frame threw; a partial-ledger first contact set `uncertain`
  without asking for the snapshot that could clear it. Standing hazard for
  slice 5, named here because the replay rules exist for it: the stream must
  open BEFORE the snapshot (or events between are lost), which means the
  region between stream-open and snapshot is folded as replay — cheap to
  avoid entirely if `MailDto` grows the trail's current SSE id so the stream
  can open exactly at the snapshot; decide there. Not wired into the store or
  the SSE fold — that is slice 5's seam; `ui/` is unchanged by construction.)
  `mail-stream-alignment` (2026-08-15; the hazard above, DECIDED and landed
  ahead of slice 5 because the answer is an engine field rather than a client
  tactic — as-built amendment to d14. Snapshot-then-subscribe LOSES (a fresh
  subscription anchors at the trail's end, ADR-0007 d5, so the window's events
  are gone and a vanished `mail.cursorAdvance` draws an envelope pending
  forever with nothing flagged); subscribe-then-snapshot cannot lose but
  replays, and the reducer survives replay except for the one honest
  ambiguity — a replayed FIRST advance is indistinguishable from a
  deleted-and-restarted lineage, so it flags and asks for a re-snapshot, and
  the window is exactly where a first advance replays. So `MailDto` carries
  **`TrailEventId`**, the trail's end at snapshot time, and the client
  subscribes at `Last-Event-ID: <it>`: the SSE id IS the byte offset after a
  line, so the resume starts exactly where the picture's knowledge ends — zero
  loss, zero duplicate, asserted as one fact. Sound rather than merely
  convenient on two counts. The stamp is read **before** the store, so the
  residual window — two in-process reads, not a network round trip — can only
  ever duplicate and never lose; the direction of the error is one statement's
  placement, and since neither reflection nor a drive can see statement order
  without a real sleep, a SOURCE pin holds it there (the `Api/` naming pin's
  precedent, and deliberately weak on its own). And the field is nullable and
  **never defaulted**: 0 in this id space means "from the first byte", so a
  client reading absent-as-0 would replay the entire trail as live — absent
  means "no trail served, fall back to subscribe-then-snapshot", and the
  reducer carries the null through rather than coalescing it. Rotation and
  truncation needed nothing new: `TrailCursor` already resets and emits
  `Reset` (id 0), which the reducer already re-seeds on. `DaemonHost` now
  resolves the SSE options ONCE and hands the same path to the stream and the
  read model — an offset into a different file is not an alignment. The replay
  rules are unchanged; they stop being load-bearing on first paint and go back
  to covering reconnects, gaps and a replaced trail. 4 tests (`MailApiTests` —
  resume-exactness end to end through a real host and SSE client, the null and
  zero contracts, the read-order pin), 3 web units; `MailState.trailEventId`
  carries it so slice 5's resnapshot re-anchors the stream as well as the
  picture. C# 948 → 952 green twice; web units 211 → 214.)
  `mail-live-choreography` (2026-08-15; phase 4, slice 5 — the bus stops being
  a photograph taken every four seconds and becomes the thing itself, moving.
  `web/src/mailStream.ts` is the whole seam: fetch the snapshot, seed, open the
  stream AT the stamp the snapshot carries, and re-seed whenever the reducer
  raises `resnapshot`. **Two as-built amendments to d14**, the first forced by
  ADR-0009 d2. The tempting design — let the bus ride the trace's already-open
  stream and drop frames whose id is at or behind the stamp — is NOT AVAILABLE,
  because the resume id is an opaque monotonic cursor a client echoes and never
  interprets (d4 will redefine it as a cross-segment global offset), and that
  comparison is an interpretation. So Mail runs its OWN subscription opened at
  the stamp, which asks the server the same question and needs no arithmetic;
  it is independently right, since the trace's buffer is `TRACE_CAP`-capped and
  dropping the oldest line is correct for a log and silent corruption for a
  reduced picture. `TrailEventId` therefore became a STRING — opacity
  structural rather than commented, and the one comparison that must never
  collapse (`"0"` = replay everything vs absent = no trail served) can no
  longer be flattened by a falsy test. The cost is one more attached observer
  (ADR-0007 d7 defers idle-exit), so the stream starts LAZILY on the Mail
  view's first visit rather than at session start. Second: **resync is a
  first-class state**, not an error path — the reducer already refuses to guess
  and says so, and the driver watches edge-triggered for exactly that, tears
  the stream down, re-seeds and re-anchors at the NEW stamp (resuming at the
  old one would replay everything the fresh snapshot already contains, which is
  the overlap this design exists to remove). The badge is the Mail stream's
  own, not the trace's — two subscriptions can disagree and the one that
  matters is the one feeding THIS picture — with `resyncing` and `snapshotOnly`
  as states a log has no equivalent of. **Delivered is finally real**: it comes
  from a `mail.deliver` line and nowhere else, which no snapshot can carry, so
  slice 4 could only ever say *before cursor · no record*; the fold supplies the
  record, and an envelope still lacking one reads the same honest sentence.
  FOUR MOTIONS, all CSS keyed on state the reducer already computes
  (`MailGlyph.arrival` from the line's `source`, `MailTrack.motion` from the
  cursor's `lastEventKind`) so the canvas computes no motion of its own and
  every one vanishes under `prefers-reduced-motion` without removing a fact:
  arrival drops onto the lane from the spine (keyed on INSERTION, so it plays
  once per envelope and a re-seed correctly replays nothing), the cursor slides,
  a re-anchor **jumps** — load-bearing, not decorative, since a cursor only ever
  reads forward and animating a re-anchor as a leftward slide would depict one
  reading backwards — and a spend transitions rather than repaints. e2e asserts
  DOM STATE, never timing, against a real daemon driven by real `mail send` +
  real fired hooks: an envelope arrives live in its lane and is distinguishable
  from a snapshot line; a digest at a seam delivers it and the RECORD is what
  says so; no `/mail` request follows the first (the absence of the poll is part
  of the contract — with it back, arrivals would silently stop animating); and a
  TRUNCATED trail under a live daemon drives reset ⇒ resync ⇒ re-anchor, with
  mail sent afterwards still arriving, proving the new anchor is live. Two
  assertions the drive corrected: the digest legitimately delivers what the seed
  left pending too, so the pin moved to the envelope we WATCHED arrive rather
  than a count that was really about the seed; and the `deliver` record lands
  after the advance, so the cursor's last motion is `deliver`, not `advance` —
  the claim is that it read FORWARD, not which forward event was last. The gap
  half of the resync path stays at the unit level: reaching it e2e would mean
  exposing the SSE buffer's capacity as daemon CONFIGURATION, and a production
  surface added so a test can reach it is the worse trade. C# 952 green twice;
  web units 214 → 231, e2e 32 → 36 green twice; all three tiers × both themes
  snapped and read.)
  `mail-canvas` (2026-08-15; phase 3, slice 4 — the sixth GUI view, and the
  first picture of the bus: `web/src/MailPanel.tsx` over the pure geometry in
  `web/src/mailCanvas.ts`, drawing the MECHANISM rather than a mailbox. The
  ledger is a fixed spine in append order (mail never moves), each `to` role a
  lane hanging off it by a drop line, each session reading that role a TRACK
  with its cursor as a marker — and the mail it passed over marked underneath
  with the cursor's own arithmetic (`n of ttl`, spent when the opportunities
  reach it). A role nobody reads still gets a lane, saying `no reader`; a
  malformed line and an unterminated tail live on the spine alone, because
  neither names a recipient. **Two as-built amendments to d14**, both forced by
  drawing it. (1) The pan/zoom is ONE AXIS, not a `viewBox`: the sketch's
  uniformly-scaled box scales the CHROME with the content, so the pinned role
  gutter is either a fixed patch of scene — which shrinks under its own
  constant-size labels until one lane's text prints inside the next, observed —
  or a fixed patch of screen, which then grows over the ledger it is supposed to
  sit beside; there is no width that is both. The bus is one-dimensional, so
  `MailView = {x, z}` scrolls and scales the LEDGER and nothing else: every
  vertical measure, font and stroke is a CSS pixel, the svg's viewBox is 1:1
  with its box, and a bus with more roles makes a taller canvas the page
  scrolls rather than a vertical pan to get lost in. (2) The fit opens on
  whatever tier the bus's SIZE implies — a nine-envelope store on cards, a
  hundred-and-twenty-envelope store on role pulses — and never magnifies past
  natural scale; the tier is a fact about the bus, not a preference. Semantic
  zoom is measured in px-per-slot (far < 40 ≤ mid < 132 ≤ near = a slot owning
  at least its natural width, which is exactly when a four-line card fits at
  constant text size), and the x axis is SLOT-uniform rather than
  byte-uniform — one 128KiB envelope would otherwise swallow a ledger of a
  hundred small ones, and every question this view answers is ordinal — while
  still mapping offsets EXACTLY at line boundaries, where cursors, frontiers
  and deliveries actually sit. A `?since=` snapshot and any hole draw as an
  explicit `never seen` break rather than being closed up. **The canvas computes
  no status of its own**: every glyph's standing is `lineStatus` per cursor
  (the lane shows the most-pending of them when two sessions disagree) and
  every mark is `projectCursor`'s, which 37 tests in `mailCanvas.test.ts` pin
  scenario by scenario against all 16 engine-generated goldens — glyph in its
  recipient's lane at its own offset, cursor where the reducer holds it, mark
  under ITS envelope with the reducer's arithmetic, nothing laid out past the
  scene it declares. **Delivered is absent from this slice, honestly**: it comes
  from a `mail.deliver` line and the snapshot cannot carry one, so an envelope
  behind a cursor reads *before cursor · no record* until slice 5 folds the
  trail; the legend says so and the cursor's own `lastDeliveredId` is shown
  instead of inferring. Detail card (click any envelope) is the only place a
  BODY appears — bodies reach the browser through the snapshot alone. Colour is
  never the only channel: a status glyph, an sr-only/`<title>` sentence, a
  legend, and an `aria-label` summarising the whole scene; presence FADES the
  track and is never claimed. The seed became a scripted SWARM — three roles,
  two sessions on one of them, held + spent + fresh + a role with no reader —
  put on the bus through the REAL `mail send` and moved by REAL `mail digest`
  registrations at real fired hooks, so the seeded picture is one the engine
  could actually produce; `fireHook` grew a payload argument (a session id is
  what gives a digest a per-session cursor) and the sandbox now redirects
  `CAPTAINHOOK_MAIL_DIR` — **it did not before, so any mail work in a spec or
  the preview would have written the operator's live bus** (CLAUDE.md's
  pollution warning, one env var from being violated). Three defects the
  screenshots caught and fixed in-slice: a card's fourth line printing below its
  own body, the pinned gutter overprinting the first envelope of every lane, and
  the far tier stacking three constant-size label lines into a lane that had
  shrunk below them. Two the e2e caught: pointer capture taken on pointerdown
  retargeted the following click, so a canvas that panned could never SELECT
  (capture is now taken only past a 3px travel), and `[data-tier]` on both the
  readout and the canvas. `snap.mjs` learned per-view zoom tiers — a view with
  semantic zoom is three drawings and one shot would leave two thirds
  unreviewed — driven through the real buttons; all three tiers × both themes
  snapped and read. Also repaired, pre-existing and unrelated: two e2e specs
  still asserted `Stop` declares no loop effects, which stopped being true when
  item 20's reconcile seam gave it `decide` (`e134ec0`); and the human stderr
  line rendered `mail.append`'s nested `from` as
  `System.Collections.Generic.Dictionary\`2[…]` while the JSONL beside it
  carried the real object — `LogEvent.ToPretty` now renders any structured value
  as the same compact JSON, pinned. C# 948 (947 + the pretty pin) green twice;
  web units 211 (from 175, of which 36 are `mailCanvas.test.ts`), e2e 32 (from
  28) green — twice, after the one flake it opened with was run down and fixed:
  the spec zoomed before the first snapshot landed, and an EMPTY bus fits at
  natural scale, so the tier readout said "near" while there was nothing near to
  look at.)

- [x] **19. GUI overhaul — sidebar views, template-gallery authoring, the
  screenshot loop** — the GUI works but has a visible defect (handlers table
  overflows its card), a broken one-page layout (trace buried, dead space),
  a cramped authoring surface, and a raw-JSON policy editor. Per **ADR-0015**
  (accepted 2026-08-11): sidebar + one-view-at-a-time over the untouched
  island architecture (a store `view` slice, no router; Trace lands first);
  a real token system, polished-terminal direction, both themes; authoring =
  a client-side **template gallery** single-sourced from `examples/payloads/`
  via Vite `?raw` — the script-write API verb explicitly REFUSED (ADR-0011's
  trigger stays unfired); policy rule builder + raw toggle with the
  round-trip guard; and the **screenshot-driven agentic loop as a committed
  deliverable** (extracted daemon sandbox → `preview.mjs`/`snap.mjs` + the
  `ui-loop` skill) — landed FIRST, since it is the eyes for the rest.
  Build order: ADR-0015 § Implementation plan (8 slices; the risk slice is
  `tokens-and-sidebar`, whose nav change churns all 14 e2e specs in one
  atomic commit). API surface frozen throughout. Tick slices here as they
  land.
  Slices landed: `status-harness-polish` (2026-08-11; slice 7 — the two read
  views were flat in the same way: everything at one weight, so nothing was the
  point. **Status gains a hierarchy rather than more tiles.** Identity and pid
  become a text STRIP — a content hash is read and compared against a deploy,
  not a metric that moves, and tile chrome made seven equal boxes whose seventh
  wrapped alone onto its own row; the tiles now carry only what CHANGES, with
  `served` leading at a new `--fs-2xl` and in flight / background / open streams
  beside it. Every tile owns a NOTE saying what the number means now (`idle`,
  `none pending`) and, for supervision, what it COSTS — an escalated worker
  reads "asks fail fast — see Handlers" (ADR-0004 d5), because a count does not
  tell an operator what it bought them. A toned value ships a glyph as well as
  the color (color is never the only channel carrying a state), and the grid
  stretches so a row's notes share a baseline. **Harnesses becomes a real
  MATRIX**: the chips read `Stop (0)` — a count, with the verbs themselves
  hidden in a hover title — which answered a question nobody has; events are now
  rows, declared verbs are columns, and a cell says yes or no. Columns are
  DERIVED from the spec (`verbColumns`), never hardcoded, per ADR-0003's
  declare-in-data rule: a fixed list would render a future verb as a silent
  blank, the harness permitting what the matrix denies. Permitted cells take the
  ACCENT, deliberately not the reserved ok/warn/bad palette — permission is a
  capability, not a health state, and borrowing health colors here would cheapen
  them one view over; every cell's yes/no also exists as `.sr-only` text, and an
  event declaring nothing gets one spanning "no loop effects" cell rather than an
  all-blank row that reads as missing data. `verbsLabel`/`effectLandsOn` are
  SHARED with the gallery per the ADR rather than re-spelled, which surfaced a
  real inconsistency in the old inline code: it collapsed three cases into two,
  claiming "no loop effects" for an event the registry never declared —
  the same false accusation `effectLandsOn` already refuses to make. Now
  declared / declared-empty / not-declared are three sentences.
  Three design misses the snap read caught and fixed IN-slice: ragged tile
  heights (`align-items: start` let each size to content), a lead tile spanning
  two columns so the number floated in dead space, and an over-long note that
  wrapped and made its tile the tallest. 98 units (from 94) + 28 e2e (from 27);
  all four views snapped in both themes and read.)
  `policy-rule-builder` **COMPLETE — build + skeptic pass**
  (built 2026-08-12 on opus, tests first per the plan; the ADR's one
  adversarial-verify slice, its independent `fable` skeptic pass run
  2026-08-11 — builder ⇄ JSON attacked both directions, the raw-lock guard,
  and the 412 path. **Two real finds, both fixed in-pass.** (1) A DAEMON
  contract breach the round-trip guard could never see: `JsonDocument` defers
  string unescaping, so a lone-surrogate escape (`"\ud800"`) in a criterion
  parses as a valid document and throws `InvalidOperationException` at
  `GetString` — not `JsonException` at `Parse` — escaping
  `PolicyResolution.Resolve`'s documented never-throws contract on the
  dispatch hot path, and reaching `ApiPolicyWriter.Write` as an opaque 500
  instead of a 422; fixed with `TryReadString` at every string-read site in
  `DispatchPolicy` and pinned at all three layers (TryParse violation, Resolve
  ⇒ Malformed, Write ⇒ Invalid). The same deferred-unescape pattern in
  `Harness.cs`/`ExecHandlersFile.cs`/`ExecWire.cs` is recorded in scratch as a
  follow-up sweep. (2) The raw → Rules switch SILENTLY DISCARDED raw edits:
  it reverted to the pre-toggle rows and Save then wrote those — draft-level
  silent destruction wearing an intentional-looking write; the switch now
  ADOPTS the raw draft into rows, or refuses with a
  `data-draft-unrepresentable` notice leaving the text exactly as typed.
  Tightened in-pass: the client criterion check now mirrors the daemon
  exactly (whitespace-only and lone-surrogate criteria lock to raw —
  `IsNullOrWhiteSpace` + a `\p{Surrogate}` well-formedness probe), and the
  gate assumption "the builder only ever sees daemon-accepted text" was
  EMPIRICALLY verified: version `1.0`/`1e0` (which `JSON.parse` collapses to
  1) are malformed daemon-side, now pinned. Survived attack unbroken: the
  round-trip meaning guard, duplicate-key reasoning, `__proto__`/`constructor`
  keys (locked), first-save-create with null etag, and 412 `current: null`
  (file deleted ⇒ retry is a deliberate unprotected create, now unit-pinned).
  Coverage the pass added: the 412 path driven END TO END for the first time —
  real daemon, real content-hash ETag: conflicting disk edit ⇒ mismatch with
  nothing written, retry with the adopted tag overwrites deliberately — plus
  e2e pins for raw-edits-carry-into-builder and the refused switch. Units 94
  (from 91), e2e 27 (from 24), dotnet 648 green twice; policy view snapped
  both themes and read.) The hazard is silent
  destruction — a builder that drops a field it did not understand leaves the
  page looking right while the daemon enforces something the user never wrote,
  invisible to every screenshot and every green e2e — so representability is
  decided by ROUND TRIP rather than by a checklist: `parsePolicyRows` builds
  rows, re-serializes, and compares the MEANING of the result against the input
  (`default` absent ⇒ allow and `rules` absent ⇒ none are the only
  normalizations, and they are the dialect's own). Anything that fails —
  including a shape nobody enumerated — returns null and the island LOCKS to
  raw with a notice. A malformed file never reaches the builder at all: the
  daemon could not read it, which is exactly when a lossy rewrite would be most
  destructive; that gate is also why duplicate JSON keys need no client
  detector (`JSON.parse` collapses them, but such a file is malformed
  server-side). The user's own spelling survives — `user-prompt-submit` stays
  kebab in the row and in the file, since the daemon canonicalizes at parse and
  the GUI has no business rewriting text behind a user's back. UI: default
  decision + numbered, reorderable rows (first-match-wins makes order
  load-bearing, so it is visible and editable), a Raw JSON toggle that shows
  EXACTLY what Save would write (the rows are the document — no second
  serializer), and the daemon left as the only validator of legality (its 422
  renders as before). 29 property/round-trip tests written BEFORE the
  implementation (91 units total, from 62), including a 300-case deterministic
  generator for rows → JSON → rows identity and a 15-case raw-lock table; e2e
  24 (from 20) with four new builder pins — compose-and-save, reorder, a
  hand-written policy loading into rows and saving back byte-equivalent, and
  the raw-lock refusing to touch a malformed file. One snap-read fix: a blanket
  `[data-island="policy"] button` rule from when the island had exactly one
  button was painting the mode toggle and every row action as a primary call to
  action. dotnet 642 green twice.)
  `template-gallery` (2026-08-11; d3 — authoring without a
  script-writing verb. **ADR-0011's provenance trigger stays UNFIRED**: the
  daemon gains no ability to write an executable; the gallery shows the whole
  script, says where to save it, and pre-fills the install form, leaving the
  verbatim confirm as the gate. Four new GENERIC starters in
  `examples/payloads/` — one per verb, written to be copied: `starter-inject`
  (note file + the session's git branch, read from the ENVELOPE's cwd — the
  first cut ran git in the daemon's own directory, which the smoke test
  caught), `starter-decide` (a gate that can deny, with the fail-mode choice
  spelled out), `starter-side-effect` (documents that `background` is NOT an
  exec verb — that is an in-process handler's word to the engine; the exec
  equivalent is work + `noop`), and `starter-llm` (the DESIGN.md thesis in 60
  lines: a second model spliced into the loop, carrying ADR-0010 N7's
  `--setting-sources ""` reentrancy guard and a degrade-to-noop path). All four
  **smoke-run through a real daemon** per the plan — Decide/Noop/Noop/Inject on
  the wire, the side-effect's stderr in the trail, and a stub `claude` that
  EXITS NONZERO if the reentrancy guard is missing, so the guard is proven
  passed rather than merely present; the degrade path re-run with an empty
  PATH. Script text is single-sourced via Vite `?raw` from
  `templateScripts.ts` — split from `templates.ts` because `node --test` (zero
  deps, by design) cannot resolve a query suffix — and a unit test reads each
  template's file off DISK asserting it exists, is `#!/bin/sh`, and is
  executable, which is the only thing that would catch a template pointing at a
  moved file. The save path is derived from what the daemon reports, in order:
  the handlers.json directory (its real runtime home, sandbox included), then
  the shim's deploy home, then NOTHING — a wrong absolute path is worse than an
  empty field, since the daemon would accept it and the handler would silently
  never run (the e2e found this: no shim is staged in the fixture, so the
  shim-only derivation produced no path at all). Gallery + form now surface the
  per-event effect verbs from `HarnessesDto.events` — data the client always
  fetched and never showed — so "why did my decide do nothing on Stop?" is
  answered on screen (Stop: "no loop effects"). 62 units (48 → 62) + 20 e2e
  green; dotnet 642 green twice.)
  `handlers-view` (2026-08-11; d6's split. `SupervisionPanel` →
  `HandlersPanel` (`git mv`, island `data-island="supervision"` →
  `"handlers"`, so `gotoView` loses its last special case): the registered
  table + the handlers.json editor now own a full-width view under two
  headings, and the daemon-wide SUMMARY — registrations / escalated / restarts
  / resident children — moves to Status, where the other health numbers live.
  `restarts` is derived rather than reported: a worker's generation starts at 1,
  so restarts survived is Σ(generation − 1) across registrations (ADR-0002's
  model), reading 0 on a healthy daemon. **N4's modal accessibility debt paid**:
  the verbatim confirm — ADR-0011's trust surface, the screen where a user
  consents to running a process as themselves — gains a hand-rolled focus trap
  (no dep), Esc-to-cancel, and focus RESTORE to the opener; pinned by an e2e
  that tabs 12 times asserting focus never leaves the dialog, shift-tabs off the
  first element, then Escapes and asserts the opener is focused and nothing was
  written. **`readinessTimeoutMs` is settable at last**: `ExecEntry` already
  carried it (so a hand-written value round-tripped through an edit) but the
  form could not set it — now a labelled field, shown in the verbatim confirm
  for resident entries, pinned end-to-end form → confirm → file (typed as a
  NUMBER) → back into the edit form. Two snap-read fixes in-slice: the consent
  button was visually identical to Cancel on a trust surface (now primary), and
  the 11-field form was one tall column taller than the viewport — the ADR's
  "cramped authoring surface" — now a responsive 2–3 column grid with the wide
  inputs spanning. Extraction diff reviewed before commit per the plan: pure
  rename + island rename + section headings, no logic moved. 19 e2e (16 → 19)
  + 48 units green; dotnet 642 green twice.)
  `trace-landing` (2026-08-11; the landing view stops being a
  document and becomes a FRAME: shell `100vh`, the view region a definite-height
  flex column, the `createRoot` mount divs `display: contents` so they leave the
  box tree entirely (four empty divs would otherwise share the height and break
  the flex chain), and the trace card takes the rest with its `<ol>` scrolling
  inside itself — which is why the filter and stream badge are always on screen
  with no `position: sticky` anywhere. Rows became a CSS grid with every cell
  ALWAYS rendered (empty when the field is absent), so a line without `durMs`
  no longer slides every later column left. Perf per the ADR's rejected
  alternative (no virtualization dep): `React.memo` on the row + a stable
  useState-setter callback, plus `content-visibility: auto` with an intrinsic
  size. **Measured, not asserted** — `web/scripts/perf.mjs` (`npm run perf`,
  committed as the standing evidence) drives a REAL seeded daemon to
  TRACE_CAP=2000: zero long tasks while 200 lines stream in at the cap, filter
  keystroke → filtered render p50 33ms, and the heaviest operation — clearing
  the filter, re-rendering every row — 88ms WITH content-visibility vs 135ms
  without. Append→visible is 200ms and the script says plainly that this is the
  SERVER's trail stat-poll beat (TrailTail.cs), not rendering. Two measurement
  traps found and documented in the harness: a rAF scroll loop reports ~16.7ms
  whatever the work (capped) and scrolling a container invalidates no layout, so
  both scroll probes read "fine" even when nothing is; and the first cut of the
  harness reported a NEGATIVE duration — the wall clock again — so every timing
  is `performance.now()`. Also fixed a real defect in slice 1's loop that using
  it exposed: `--no-build` skipped STAGING as well as compiling, so the
  documented `npm run dev` + `snap --no-build` loop silently screenshotted the
  previous build; `buildAndStage` split into `build()` + `stageUi()` and both
  scripts always stage. 16 e2e + 48 units green; dotnet 642 green twice.)
  `tokens-and-sidebar` (2026-08-11; the risk slice, one atomic
  commit. A store `view` slice + `VIEWS`/`VIEW_LABELS` is the WHOLE of
  navigation — no router (ADR-0015 d1): the console rail writes `view`, every
  screen island returns null unless `view` names it, and the gate sits AFTER
  each island's hooks so a hidden panel keeps polling and returns current
  rather than blank. Rendering null does not unmount, so the policy editor's
  unsaved draft and pending If-Match tag survive a view trip — the thing a
  router would have thrown away. Token system in `styles.css` (color / type /
  space / radius / depth, light + dark both first-class via
  `prefers-color-scheme`, no toggle; the console rail is deliberately dark in
  BOTH themes, which is what gives the light theme its hierarchy). e2e churn
  landed as the ADR predicted: ONE `gotoView` helper in `fixtures.ts` that
  every spec navigates through, plus the new `nav.spec.ts` — exactly-one-view
  and the regression that matters, **the SSE stream surviving a view switch**
  (lines appended while Trace is off-screen are all there on return, in order,
  and the same stream keeps feeding). 14 specs → 16, green first try after the
  restructure. Two design misses the snap read caught and fixed IN-slice: the
  Policy island floated uncarded while every other view was carded, and — the
  real one — a bookmark visit (`/ui` with no token) showed a BLANK region beside
  a nav that did nothing, with the only instruction buried in 11px rail text;
  the rail now states the session tersely and a `SessionNotice` island fills the
  region with the actionable `captainHook ui` copy (`shell.spec` asserts it
  where a visitor actually looks). The handlers-table overflow the ADR opens
  with died here as a side effect — full-width views gave it room. Snapped all
  5 views × 2 themes + both no-session states and read before commit; frontend
  units 48 green; dotnet suite 642 green twice.)
  `screenshot-loop` (2026-08-11; the eyes, landed first. The
  e2e fixture's daemon sandbox — spawn, env isolation, api.json readiness,
  drain-by-PID, reclaim — extracted verbatim to `web/e2e/daemon.ts` and now
  shared by THREE consumers: the Playwright fixture (`fixtures.ts` is just the
  binding), `scripts/preview.mjs` (ONE persistent seeded daemon printing its
  `/ui#t=` URL, with `trail`/`hook`/`burst`/`url`/`quit` on stdin), and
  `scripts/snap.mjs` (every view × light/dark → gitignored `web/.screens/`).
  What the tests see and what the camera sees therefore cannot drift. View
  discovery is dynamic — `[data-nav]` items if present, else the one-page
  shell as view `all` — so the script starts finding views the moment slice 2
  lands, unchanged. `scripts/seed.mjs` fills the sandbox (and ONLY the
  sandbox — payload scripts are written into it, never pointed at
  `examples/payloads/`, which read/write the operator's home) with three
  handlers, a policy exercising every criterion shape, a varied trail, and two
  real fired hooks. The first baseline capture paid for itself twice: the
  seeded payloads' answers were caught violating the exec wire grammar
  (`inject` takes `text`, `decide` takes `verdict`), and the trace screenshot
  came back EMPTY — an SSE subscription anchors at the END of the trail
  (ADR-0007 d5), so the seed had to split into `seedFiles` (before start) and
  `seedTrail` (after the page is live, per context). The `ui-loop` skill
  documents the loop and what to look for in a snap. Extraction contract met:
  the 14 existing specs stay green UNTOUCHED (one random per-run flake, proven
  pre-existing by running the un-extracted fixture on the same machine —
  a transient then still blamed on daemon warm — **run down and fixed the same
  day**: it was never the daemon. Sampling `CLOCK_REALTIME` against the
  monotonic clock caught **WSL2 stepping the wall clock +86.4s while monotonic
  advanced 101ms**, then back; strace of a "stalled" daemon showed an 86.30s
  gap with zero syscalls and every thread parked, and one probe measured a
  NEGATIVE start-to-ready. The fixture's readiness deadline was `Date.now()`-
  based, so a forward step expired 40s against a healthy daemon — the harness
  violating house invariant 2 while the engine honors it everywhere
  (`DateTime.UtcNow` survives only in log timestamps and the opt-in
  `ColdStartProbe` boot delta). Deadline now `performance.now()`; e2e green
  three runs straight with zero flakes, from 1 flake in every run before.
  Recorded in doc/platform.md § Wall-clock steps, with the misdiagnosis
  corrected in `playwright.config.ts` and the GUI flow doc).
  Baselines captured in both themes and read: they show exactly the defects
  the ADR opens with — the handlers table clipped at its card edge, a dead
  left column, and the live trace 3400px down the page. dotnet suite 642 green
  twice.)
  `docs-capstone` (2026-08-11; slice 8, the close. ADR-0015's Ground truth
  back-filled decision→code, ADR-0008 given the amendment notes it lacked —
  its header, its d1 screen table, and its d7 loop now say what ADR-0015
  changed, so a reader landing on the older ADR is not silently misled (only
  one inline table row had mentioned it). The verify was MECHANICAL rather
  than a re-read: a throwaway checker parsed every Ground truth table in the
  flow doc + both ADRs and asserted each named file exists and each backticked
  symbol appears in that row's files — 140 file refs and 123 symbols across
  the three, with the sole flag being ADR-0008's deliberately historical
  `SupervisionPanel.tsx`, which its own row already annotates as renamed. The
  weak spots of substring matching (`build`, `stageUi`, `VIEWS`) were then
  hand-checked. **Item 19 complete: all 8 slices landed.**)
  **Item complete 2026-08-11** — five sidebar views over the untouched island
  architecture, a token system in both themes, the trace holding 2000 rows
  with measured evidence instead of a virtualization dep, authoring by
  template gallery with ADR-0011's script-write trigger deliberately UNFIRED,
  a policy rule builder whose representability is decided by round trip and
  which survived an independent skeptic pass, and — landed first, because it
  is the eyes for the rest — the screenshot loop as a committed deliverable.
  Final tally: 98 frontend units, 28 e2e, dotnet 648 green twice; all five
  views snapped in both themes as the visual record. NOT deployed yet: the
  live `~/.captainHook/bin` still runs the pre-overhaul build, so `/deploy`
  is the next GUI-facing action whenever the maintainer wants the new shell
  on their own daemon.

- [x] **18. Own the spawn seam: bosun** — the exec-handler kill discipline stops
  renting `setsid(1)` from the host's package set. Per **ADR-0014** (accepted
  2026-08-11, out of the ADR-0013 language analysis — which measured the Go
  rewrite's premise and collapsed to "the only cross-OS defect a native
  component uniquely fixes is the spawn seam"; the owner withdrew that ADR's
  Windows decision the same day, so targets stay Linux + macOS + containers):
  `bosun` — ~130 lines of Zig in its own MIT repo — becomes the spawn prefix,
  ending the stock-macOS `pgroup=false` baseline (ADR-0012 N2's ⚠) and the
  dependency on the host having util-linux at all.
  Slices landed: `bosun-resolution-seam` (2026-08-11; d1/d2 — `ProcessGroup`
  grows a `SpawnPrefix` record and a `Resolve(baseDir, PATH)` that picks
  co-located bosun → PATH setsid → degrade, replacing the bare setsid probe;
  the ARGV differs per rung (bosun requires a literal `--` before the command,
  setsid takes it bare) so `BuildPsi` assembles from the prefix and takes an
  optional prefix for test injection — the deploy rung is never the rung a
  test tree resolves; the winner rides every `exec.spawn` as
  `spawner=bosun|setsid|none` in BOTH modes, the degrade keeping its existing
  `pgroup=false`. The 22 `SetsidPath is null` guards became rung-explicit.
  11 tests (`SpawnPrefixTests`): rung order — co-located bosun beats an
  available setsid, which is the whole decision — non-executable
  fall-through, PATH scan order, both-absent degrade-not-throw, null inputs,
  live-prefix self-consistency, per-rung argv literals, and the `--` contract
  driven through a REAL in-place exec against a wrapper that enforces it
  (pid == pgid asserted), so a dropped terminator fails in the suite rather
  than on a live deploy. Suite 626 → 637 green twice.)
  `deploy-fetch-and-stage` (2026-08-11; d3/d4 — bosun is the FOURTH deploy
  artifact, staged and swapped with the other three and rolled back by the
  same `bin.prev`. `/deploy` § 1a fetches the PINNED release asset (tag +
  target) and verifies it against the published `SHA256SUMS` before staging —
  never "latest", because an unpinned binary would change deploy bytes behind
  unchanged source and build determinism cannot tolerate that; native ⇒ no
  MVID ⇒ invisible to content identity by the `captainShim` rule, so a
  bosun-only swap doesn't roll the socket identity and takes effect on the
  next spawn. Verification treats a missing co-located bosun as a STAGING
  DEFECT (the first rung must win on a live deploy) and reads `spawner=bosun`
  off the trail. Pin: **bosun v0.1.0**, cut 2026-08-11 from its own CI (zig
  test → smoke suite → four cross-compiled targets → `SHA256SUMS`); the four
  digests are recorded IN the deploy skill, not merely fetched beside the
  binary, so the pin means *these bytes* rather than "whatever that tag serves
  now". Fetch + verify + run driven for real: checksum OK, 28 KB static ELF.
  **Decisive drive** (2026-08-11): the real v0.1.0 binary staged co-located
  with the dev engine, then a collapsed hook through the REAL resolver and
  dispatcher — trail `spawner=bosun`, no `pgroup=false`, and the payload
  reported `pid=125115 pgid=125115`, i.e. exec-in-place with its own group.)
  `docs` (2026-08-11; platform.md § Process groups — the fork→exec window no
  managed runtime exposes, exec-in-place pid identity, bosun's macOS
  `setsid()`, the ⚠ retired and the per-OS summary gaining a process-groups
  row; `doc/flow/exec-payloads.md` § The spawn prefix (rung ladder + argv +
  why a wrapper at all); ADR-0010 d6 amendment note; README's
  `brew install util-linux` nice-to-have replaced by the staged-deploy story
  ADR-0014 rejected it for.)
  **Deployed 2026-08-11 and dogfooding live**: `/deploy` staged all four
  artifacts (the fourth for the first time) and swapped them together — pin
  verified OK, zero `shim.wireSkew`, warm hook 15–19ms, `/ui` serving, doctor
  leaving one healthy daemon on the new identity. The rung is in force on the
  maintainer's OWN hooks: every `exec.spawn` from real tool calls carries
  `spawner=bosun` with no `pgroup=false`, in BOTH modes (oneshot
  `deploy-guard`, resident `doc-pointer`), and the live resident child sits at
  pid 164980 == pgid 164980 — exec-in-place, its own group, so a `kill(-pgid)`
  would take any grandchild it spawns. d5 (`--pdeathsig`) stays DEFERRED —
  it fires on the parent's forking THREAD exiting, and the engine spawns from
  pooled threads, so it would kill healthy payloads; revisit when a dedicated
  long-lived spawn thread exists or orphan pressure exceeds `doctor`.

- [x] **17. Dogfood findings: queued-ask fast-fail + payload reentrancy** —
  the first LLM-backed payload (orient-brief, field report
  `doc/dogfood/2026-07-21-llm-payload-and-a-find.md`) surfaced two engine
  truths when its `claude -p` child transitively fired the handler's own
  event. Landed 2026-07-27:
  (a) **a worker restart strands queued asks** — an ask enqueued in a mailbox
  the supervisor then supersedes is never dequeued (F#'s `MailboxProcessor`
  can't be drained), so the classified ask burned the FULL budget before
  guessing `backlogged` (observed: a 20s SessionStart stall for a dispatch
  whose script never spawned). Fix as built: each `ActorRef` instance carries
  an **abort** `TaskCompletionSource` that `Swap`/`MarkDead` completes on
  supersession; `current`+`epoch` fold into ONE atomically-swapped `Instance`
  record so the ask reads a consistent (mailbox, epoch, abort) triple; the
  classified ask races reply / abort / window and returns a new sixth status
  **`Abandoned`** (fast-fail, uncounted, distinct trail classification
  `abandoned`). Genuine backlog — instance still alive — is unchanged, keyed
  apart by `abortedTask.IsCompleted`. Pinned by `WorkerAbandonedAskTests`
  (incident-shaped fast-fail + a backlog-stays-backlogged split guard); the
  dispatcher-level `ClassificationTests` backlog case was revealed to have
  ALWAYS been an abandonment (d1's cancel-restart strands d2) and now asserts
  `abandoned`. Recorded as the **ADR-0004 d5 amendment** (2026-07-27), flow
  doc `doc/flow/actor-supervision.md` (six-status table + the swap section).
  (b) **payload reentrancy self-blocks** — engine-side detection isn't
  feasible (the re-entering `claude` mints its own dispatchId; stripped env
  means no depth marker survives the socket), so it's a documented
  payload-authoring constraint: *a payload must not transitively fire its own
  event* — `--setting-sources ""` is the pattern. Recorded as **ADR-0010 N7**
  and in the exec-payloads flow doc; `orient-brief.sh` carries the guard.
  Suite 626 green twice.

- [x] **16. Distribution & macOS** — ship a runtime-free single binary and add
  the second target. Per **ADR-0012** (accepted 2026-07-20): target Linux +
  macOS (Windows-native out of scope, WSL2 is the path); the engine goes
  self-contained single-file so no host .NET runtime is needed (the shim is
  already Native AOT); port the `/proc` identity sites so the safety guards
  hold on macOS — the kill path (`setsid`+`kill(-pgid)`) is already POSIX;
  all containerization deferred.
  Landed 2026-07-21 as one commit (the ADR's three steps):
  **single-file publish** — `/deploy` publishes
  `--self-contained -p:PublishSingleFile=true`; the csproj's
  `KeepAppAssembliesLooseForIdentity` target keeps the FOUR app assemblies
  loose beside the ~74MB bundle because ContentIdentity hashes the deploy
  dir's *.dll MVIDs and the shim's skew guard reads captainHookWire.dll (a
  fully-bundled publish would throw identity and read permanent skew —
  probed both, plus the excluded-ENTRY-assembly layout running a cold hook +
  daemon spawn + warm answer end-to-end); determinism re-verified (clean
  publish ×2 + empty-commit leg ⇒ loose DLLs byte-identical).
  **`/proc` port** — no third-party lib and no hand-rolled sysctl: the BCL's
  `Process` IS the cross-platform seam (sysctl/`proc_pidinfo` underneath on
  macOS). `ChildRecords.ProcStartTime` keeps RAW `/proc` field-22 ticks on
  Linux (BCL `StartTime` jitters there — equality would misread a live
  child as pid-reused; platform.md records the asymmetry) and uses BCL
  ticks on the macOS branch where they are stable; liveness checks moved
  from `/proc` existence to POSIX `kill(pid,0)` (both OSes, one path);
  `Doctor.DefaultIsOurs` gets a `MainModule.FileName` macOS branch (weaker
  than Linux's argv check — no `--daemon` proof without a KERN_PROCARGS2
  P/Invoke; recorded as the ADR-0012 N3 residue to tighten on a real Mac).
  platform.md: macOS → committed target, Windows → out of scope, § Single-file
  distribution added. ADR-0012 N2 stands: macOS is code-complete but
  UNPROVEN until a real Mac run exercises the sysctl branch. Suite 624
  green twice.

- [x] **1. Converge the C# dispatcher and F# actor layer** — handlers become
  supervised actors: dispatch = `Ask` with the latency budget as the ask
  timeout; fail-open/fail-closed maps onto supervision (fail-open ≈ restart +
  degrade, fail-closed ≈ escalate + deny). The moment the two halves become
  one architecture. Touches `Dispatcher.cs` + `Supervision.fs`; demands a
  flow-doc update and new tests (shipshape will insist).
  Landed as `Worker<'Req,'Reply>` (ADR-0002); escalated-worker fast-fail deferred to the daemon-topology item.
- [x] **2. First live deployment** — wire the echo handler into the real
  `~/.claude/settings.json` (UserPromptSubmit) and watch an actual Claude
  Code session flow through the JSONL trail. Dogfood before features.
  Observed live 2026-07-04: a real session's dispatch in the trail (47.7ms
  end-to-end) with the injection visible in-context.
- [x] **3. Declarative harness registry** — no hardcoded harness-string
  branches: a `HarnessSpec` registry (embedded defaults + validated user
  overrides in `~/.captainHook/harnesses/`) declares each harness's request
  fields, response adapter (a CLOSED coded set — data selects, code
  implements; config never becomes a template language), per-event effect
  capabilities, and install target (the data the management API + GUI will
  consume). Pattern lineage: pharos `config.gleam` (defaults/load/cached
  layering, tool gating) and moby `models/registry.ts` (capability registry
  + validated custom entries). `claude-code` stays the default; a
  `generic-json` adapter proves N>1.
- [x] **12. Thin AOT `captainShim`** — ADR-0004 decision 7's gate tripped
  (2026-07-06): a PreToolUse-class before-tools hook puts the shim's measured
  ~85ms procBoot+JIT residual on **every tool call** — per-action, on the
  agent's critical path — so the reserved thin-AOT-shim step is scheduled.
  Design resolved in the decision-7 amendment: two new projects
  (`captainHookWire` leaf lib + `captainShim` AOT exe; arrows `captainShim →
  captainHookWire ← captainHook → captainHookActors`); identity math
  unchanged (the native shim is invisible to it by construction — sound, not
  a hole); publish-time wire-stamp skew guard (skew fails safe to collapse);
  one JSONL schema across two emitters, pinned by a golden byte-equality
  test; source-generated wire JSON; delegation fallback to the co-located
  engine. Success bar: warm hook **p50 ≤ 40ms** end-to-end (from 99ms) and
  the per-tool-call tax subjectively gone. Build order: ADR-0004
  § Implementation plan, amendment plan (6 slices → 3 phases; critical path
  wire-lib-extraction → captainshim-aot-artifact → deploy-two-artifacts).
  Tick slices here as they land.
- [x] **14. Dispatch policy — captAInHook's own front door** — the product's
  native policy story, layer 1 of 3 (2026-07-06 reframe: policy governs what
  WE bring, not a second copy of harness permissions). A user-editable
  policy file decides whether an arriving hook gets *worked*: per-event /
  per-handler enable-disable, per-project or per-session scoping, and a
  global pause — the hook is always *answered* (the harness blocks on
  stdout), but policy short-circuits dispatch to an immediate Noop.
  Daemon-side by rule — the shim stays policy-free (aot-boundary rule 1).
  Design recorded in **ADR-0006** (2026-07-06): one strict JSON file
  (`~/.captainHook/dispatch.json`, house policy dialect), decisions
  `allow|deny` only, rules AND event/handler/project(prefix)/session,
  first match wins; absent ⇒ allow all, malformed ⇒ Noop-everything
  loudly; one evaluator covering both the daemon serve loop AND the
  collapsed pipeline; hot reload, no last-good; **no pause mechanism** —
  `default: deny` already says it (API convenience later if friction is
  real). This is the same data item 5's API manages and item 6's GUI
  edits: file → API → GUI.
  Build order: ADR-0006 § Implementation plan (10 slices → 6 phases;
  critical path dispatch-policy-file → rule-matcher →
  event-level-deny-shortcircuit → absent-allow-malformed-noop →
  evaluator-both-paths → policy-hot-reload; adversarial verify on exactly
  three slices; no ultracode). Tick slices here as they land.
  Slices landed: `dispatch-policy-file` (2026-07-06; the `DispatchPolicy`
  model + strict parser under `Core/`, on `HarnessSpec.TryParse`'s precedent
  — collect-every-violation, all-or-nothing, never throw on bad DATA —
  tightened so unknown fields, an unknown/missing `version`, `ask`, and
  criteria-less rules are all MALFORMED per ADR-0006 decision 1;
  `ResolvePath` injectable-path idiom + the `CAPTAINHOOK_DISPATCH_FILE`
  override; 24 parse tests; no matcher and no tri-state yet — those wrap it
  in phases 2/4); `handler-level-exclusion` (2026-07-06; an optional
  order-preserving excluded-names filter on `DispatchAsync`, pre-fan-out so
  an excluded fail-closed handler contributes no deny, snapshot registry +
  supervised Worker left untouched — filtered never restarted; default-null
  path byte-identical, dead until wired; smoke-tested). Both land as one
  session (disjoint code); Phase 1 complete.
  `rule-matcher` (2026-07-06; `DispatchPolicy.Evaluate` → `PolicyOutcome`
  {Work, ExcludedHandlers} — two questions from one rule list: handler-less
  rules decide event-level work/short-circuit (first-match-wins, else
  default), handler-named rules decide per-handler exclusion
  (first-match-per-handler, allow shields a later deny); the sharp edge —
  project path-prefix — is separator-boundary-aware so `/repo` never matches
  `/repo2`, trims trailing separators, matches cwd==project and strict
  subdirs, literal prefix / no realpath; still no callers, wired in phase 5)
  and `exclusion-ordering-failmode-pins` (2026-07-06; the N3 adversarial
  pass, test-only: an excluded fail-closed gate contributes no deny among
  survivors, a middle exclusion leaves registration-order merge intact, and
  the sharp one — an excluded handler's supervised Worker is never
  restarted, its stateful counter continuing across the skip; plus
  exclude-all⇒Noop and exclude-unregistered⇒harmless edges). Suite 218 green
  twice. Phase 2 complete.
  `event-level-deny-shortcircuit` (2026-07-06; first wire-touching slice —
  a `DispatchPolicy?` on `HookRun.CollapsedAsync`, evaluated after
  `ParseEvent` and BEFORE the dispatcher is built: `Work==false` answers a
  valid Noop through the shared `DeniedStdout` gate+serialize tail, no worker
  asked / no budget spent / no background drain. Byte-identity to an
  uneventful hook holds by construction — the Noop rides the identical tail a
  worked dispatch's Noop takes — and is both unit-pinned and driven: a real
  collapsed run under a deny policy emits exactly `{}`, == the uneventful
  baseline, ≠ the echo, with the skip visible only on stderr. Default null =
  today's behavior, live CLI byte-unchanged; the daemon site + resolver wiring
  are phase 5, reusing `DeniedStdout` so the two paths can't drift). Suite 223
  green twice. Phase 3 complete.
  `absent-allow-malformed-noop` (2026-07-06; the file tri-state
  `PolicyResolution.Resolve` — Absent (no file ⇒ allow all, the zero-config
  default) / Malformed (present but unreadable or unparseable ⇒ deny all
  loudly, carrying the error; a directory or dangling symlink is Malformed
  not Absent — ambiguous I/O fails toward quiet, never toward silent grant;
  no keep-last-good) / Loaded (valid ⇒ evaluate); `Evaluate` maps each case
  so the two wire sites consume it uniformly. **Adversarial verify (3-skeptic
  fan-out) earned its keep**: confirmed Resolve never throws + the case
  mapping is sound, but caught a real SILENT GRANT — a rule event spelled
  kebab (`user-prompt-submit`, the project's first-class spelling) parsed
  valid yet never matched the canonical dispatch, turning a deny into a grant;
  fixed by canonicalizing the event criterion at parse (Harness.Canon) +
  case-insensitive event match, plus duplicate-JSON-field rejection (strict
  never-guess). Resolver still unwired — phase 5). Suite 240 green twice.
  Phase 4 complete.
  `evaluator-both-paths` (2026-07-06; **the go-live slice**. One shared
  `HookRun.PolicyGateFor` — resolve+evaluate → a `PolicyGate` that either
  short-circuits to the byte-identical `DeniedStdout` Noop or proceeds with
  the handler exclusions — called at the IDENTICAL seam (after ParseEvent,
  before the dispatcher) in BOTH `HookRun.CollapsedAsync` and
  `DaemonHost.DispatchOneAsync`; `policyPath` threaded RunAsync→serve→dispatch,
  Program.cs feeds all three prod entry points (daemon, shim-fallback,
  collapsed) from the same `DispatchPolicy.ResolvePath()`. Resolved
  per-dispatch (content edit effective next hook; phase 6 adds the stat-gate).
  Adversarial verify — no-drift: a cross-path test drives the same policy
  file+event through the real daemon (over ShimClient) and the collapsed
  pipeline and asserts byte-identical answers; a 2-skeptic fan-out confirmed
  no decision drift and no ungoverned dispatch route (all three shim routes +
  both prod pipelines gated); driven live through the real CLI — deny⇒`{}`,
  undenied event echoes, malformed⇒`{}`+loud stderr. Load-bearing order
  honored: landed only after phase 4's absent⇒allow default). Suite 245 green
  twice. Phase 5 complete.
  `policy-hot-reload` + `skip-trail-visibility` + `default-deny-pause-pin`
  (2026-07-06, the tail. `ReloadingPolicy` — the daemon's per-dispatch
  (mtime,size) stat-gate over Resolve: poison-AND-advance (a broken edit
  denies all AND advances the stamp — no keep-last-good, no re-parse storm),
  unchanged file returns the same instance, collapsed path resolves once.
  Trail: `policy.skip`/`.exclude`/`.malformed`/`.reload` emitted in the one
  shared gate (no trail drift), happy path silent. default:deny Noops every
  hook — decision 7's pause, pinned. **Adversarial verify (skeptic on the
  reload edge)**: confirmed poison-advance/no-storm/recover all hold, and
  caught a NEW fail-open — an absent⇒`mkdir` at the path stamped identically
  to absent so the gate stayed allow-all when Resolve says Malformed; fixed
  by giving Stamp Resolve's directory-first precedence, regression-pinned.
  Two other flagged risks (mtime/size stamp collision; unlocked two-field
  swap) are pre-existing properties `ReloadingHarnessRegistry` already
  accepts — not introduced here. Flow doc: doc/flow/dispatch-policy.md).
  Suite green twice. **Item 14 complete — dispatch policy is live on both
  paths.** Deployed 2026-07-06 and verified on the live daemon: deny-
  SessionStart hot-reloaded in (denied ⇒ `{}` while UserPromptSubmit still
  echoed), file removed ⇒ SessionStart echoes again — hot reload both
  directions with `policy.reload`/`policy.skip` in the trail; clean allow-all
  state (no `dispatch.json`) restored. Dogfooding live.
- [ ] **13. PreToolUse policy gate** — *demoted to a secondary payload*
  (2026-07-06): tool-call gating overlaps harness-native permissions; its
  differentiated value (dynamic decisions, portability, central
  distribution) matures with items 5/10. Design stays recorded in
  **ADR-0005** (status: deferred) for when the payload is wanted — likely
  alongside item 9's other handlers, after item 6.
  Slices landed: `wire-lib-extraction` (2026-07-06; pure move — five files
  `git mv`'d into the new leaf lib, wire log seam bound to `Actors.Log` by
  engine + tests, suite green twice, zero behavior change);
  `wire-json-source-gen` (2026-07-06; `WireJson` context for the two frame
  records, `IsAotCompatible` analyzers on — wire lib builds warning-free);
  `wire-jsonl-logger` (2026-07-06; `WireJsonl.Render` = the shim's emitter of
  the trail's one schema, pinned byte-identical to F# `ToJson()` by 17 golden
  cross-emitter tests — unicode/control escaping, durMs rounding, omit-null,
  nested data — plus mirrored path resolution and the O_APPEND appender).
  Phase A complete.
  `captainshim-aot-artifact` (2026-07-06; 3.8MB native ELF, wire-lib-only
  reference graph; ShimMain tested in IL form through injected streams —
  warm relay, delegation verbatim, at-most-once held, mode refusal; staged
  co-located deploy measured live: **16ms/hook warm native vs 139ms JIT**
  20-run avg against the same daemon — 8.7×, success bar ≤40ms beaten;
  ~11ms of the 16 is the forward span, native procBoot ≈5ms; the sun_path
  overflow path exercised by accident and delegated exactly as designed;
  AOT toolchain + no-MVID facts recorded in platform.md);
  `wire-skew-guard` (2026-07-06; zero build machinery — Native AOT preserves
  `Module.ModuleVersionId`, probed AOT≡IL, so the shim compares what it IS
  against what the directory advertises; mismatch or missing DLL ⇒ never
  touch the socket, delegate, `shim.wireSkew` in the trail; pinned by IL
  tests in both skew directions + a live-socket never-accepted assert, and
  verified in the native artifact — including catching a REAL skew created
  mid-verification by copying one artifact without the other, which
  delegated and answered the hook exactly as designed). Phase B complete.
  `deploy-two-artifacts` (2026-07-06; /deploy reworked to stage-both +
  swap-together with `bin.prev` kept for one-swap rollback; live cutover
  verified — cold delegated+spawned, warm `shim.answered`, zero
  `shim.wireSkew`, superseded daemon doctor-drained; PreToolUse wired into
  settings.json per the gate's own trigger, dispatching `{}`/exit-0 with
  zero handlers until item 9's policy gate; **live warm hook 16ms vs 143ms
  pre-cutover** — 9×, the amendment's ≤40ms bar beaten on the real path).
  **Item complete: the amendment plan is fully landed; dogfooding live on
  the native shim.**

## Next

- [x] **4. Daemon topology** — long-lived `captaind` + thin per-event shim
  (DESIGN.md's split). ⚠ Fires ADR-0001's revisit trigger: re-evaluate
  Akka.NET vs the hand-rolled layer *before* building on either → an ADR.
  Design recorded in ADR-0004 (verdict: stay hand-rolled; carry-ins
  answered) — the gate is discharged, this item is now implementation.
  Build order: ADR-0004 § Implementation plan (14 slices → 6 phases;
  critical path content-identity → lock-bind → serve-loop → drain →
  idle-exit). Tick progress here as slices land.
  Slices landed: `three-mode-dispatch`, `frame-protocol`,
  `content-identity-versioned-socket`, `timeout-fault-classification`
  (2026-07-05) — Phase 1 complete; `lock-bind-rendezvous`,
  `shim-forward-or-fallback`, `detached-daemon-spawn` (2026-07-05) —
  Phase 2 complete; `daemon-serve-loop` (2026-07-05; dispatchId adoption
  verified end-to-end); `at-most-once-fallback-guard` (2026-07-05; chaos
  audit: 30 hooks under random daemon SIGKILL — zero double dispatches) —
  Phase 3 complete; `sigterm-drain` (2026-07-06; real SIGTERM landed
  mid-dispatch — in-flight hook still answered, drained in 62ms);
  `harness-hot-reload` (2026-07-06; in-place edit — same inode, dir mtime
  unmoved — served on the next hook through a live daemon); `doctor-reaper`
  (2026-07-06; swept 3 stale identities live while sparing the healthy
  deployed daemon from a dev-tree run) — Phase 4 complete;
  `mandatory-idle-exit` (2026-07-06; live daemon survived refreshing hooks,
  self-reaped 92ms past its window, respawned on the next hook) — Phase 5
  complete; `concurrency-audit-and-soak` (2026-07-06; 200 concurrent
  dispatches in-suite — seq values a perfect 1..200 permutation, background
  queue exact, escalation mid-load survived; 180 live hooks against the
  deployed daemon — 100% warm, zero double dispatches, p50 99ms round-trip
  / 13.4ms daemon-side, RSS asymptoting not leaking) — Phase 6 complete.
  **ADR-0004's implementation plan is fully landed**; dogfooding live.
  Carry-out for the AOT-shim gate (ADR-0004 decision 7): the measured warm
  residual is ~85ms of shim procBoot+JIT per hook — **gate tripped
  2026-07-06 → item 12**.
  ⚠ Until `mandatory-idle-exit` lands, a spawned daemon lives until killed —
  SIGTERM now drains gracefully; kill -9 stays safe.
  Dogfooding: `/deploy` ships the apphost build to the live hooks and
  verifies the warm path (mechanics: doc/flow/live-deployment.md).
  **Carry-ins from ADR-0002 — DISCHARGED** by the
  `timeout-fault-classification` slice (ADR-0004 decision 5): (a) a wedged,
  token-ignoring handler is abandoned-and-respawned and counts toward
  escalation; (b) asks against an escalated worker fail fast (~0ms); (c)
  honored cancellations restart without counting — changed deliberately.
  Pinned by ClassificationTests.cs; mechanics in
  doc/flow/actor-supervision.md.
- [x] **5. Management API** — HTTP + SSE on the daemon: inventory of
  installed hooks/skills, install/uninstall/enable/disable operations, and a
  live event stream sourced from the structured log pipeline (dispatchId
  correlation = per-dispatch traces for free). **After item 14** — the API's
  write surface IS item 14's policy/registry data (file → API → GUI), and
  the event stream wants real dispatch traces. The ADR it fires is fired:
  design recorded in **ADR-0007** (2026-07-07) — BCL `HttpListener`
  loopback-only in daemon mode (no new project; the zero-new-deps answer —
  Kestrel and even the ASP.NET FrameworkReference rejected); fixed default
  port 4665 + `api.json` discovery file, drain-start port handoff across
  identity cutover (the port is a singleton the versioned socket never
  was); SSE over a stat-poll tail of the JSONL trail file (both emitters'
  halves — an in-process tee would miss the shim and collapsed dispatches),
  byte-offset event ids for lossless reconnect, bounded-channel drop-oldest
  + gap marker per subscriber; writes v1 = `PUT /policy` only, validated by
  the same strict parser and atomic-renamed so hot reload makes API writes
  ≡ hand edits (install ops deferred to ride with items 6+10, ADR-0006 N1);
  per-daemon bearer token (0600 `api.json`) + Origin checks on every
  request; idle-exit answered — requests reset the clock, an open SSE
  stream defers exit (current lock-holder only), the API never spawns a
  daemon. ADR-0004's "management API lands" trigger examined and declined:
  the hook path keeps one UDS connection per dispatch.
  Build order: ADR-0007 § Implementation plan (2026-07-07; 13 slices → 7
  phases; critical path api-listener-host → port-config-and-cutover →
  api-json-discovery → auth-token-origin → put-policy-write →
  docs-flow-platform; adversarial verify on 6 slices — the port handoff,
  auth, both SSE slices, idle-defer, and the atomic policy write; no
  ultracode). Tick slices here as they land.
  Phase 7 requirement (`docs-flow-platform`): the flow doc MUST carry a
  ground-truth table of `TrailCursor`'s edge behaviors — oversized-line skip
  (lines ≥ `maxBytes` are dropped, not delivered, and surfaced as a gap;
  the 128KiB read window is a hard limit), truncation-reset, and
  alignment-self-heal — so the tailer's sharp edges have a discoverable home,
  not just the Phase-5 close-out prose below + test names.
  Slices landed: `api-listener-host` (2026-07-07; Phase 1 — the loopback
  `HttpListener` management-API host in a new `Api/` area (`ApiHost` +
  reflection-STJ `ApiJson`), accept-and-hand-off loop that serves requests
  concurrently, `/api/v1` router skeleton that 404s every unwired route as
  JSON, started after `BindWhenWarm` beside the UDS serve loop via a new
  `DaemonHost.RunAsync(apiPort:)` seam and stopped at drain start — off in
  production until port-config wires Program.cs; the shim never sees it,
  aot-boundary rule 1 intact; zero new deps (HttpListener is BCL). 5 tests,
  suite 267 green twice).
  `port-config-and-cutover` (2026-07-07; Phase 2 — the API goes LIVE in
  production: Program.cs resolves `ApiHost.ResolvePort` (default 4665, env
  `CAPTAINHOOK_API_PORT`, 0 disables, malformed falls to default) into the
  daemon; N1's singleton-port handoff lands as `ApiHost.StartRetrying` —
  one sync bind attempt, fast 100ms→1s backoff spanning the incumbent's
  drain deadline, one `api.bindBlocked` warn past it, then a 5s cadence
  that never gives up until Stop, so a deploy-superseded incumbent that
  lingers to idle-exit still hands the port over; the incumbent's release
  stays at drain start (the Phase-1 `api?.Stop()` seam, now also halting
  in-flight retries via a gate double-check so a draining daemon never
  re-grabs the port); bind failure is never fatal and hooks serve
  throughout. Platform facts probed and recorded (doc/platform.md § Loopback
  TCP): no co-bind cross- or in-process, TIME_WAIT does not block a
  .NET→.NET rebind (the .NET Unix PAL sets SO_REUSEADDR on every TCP bind;
  Linux honors it pairwise, so a non-.NET prior occupant can cost ~60s —
  absorbed by the slow retry), loopback binds unprivileged. 18 new tests
  incl. a two-real-daemons cutover proof (successor binds while the
  incumbent still drains a straggler) and a TIME_WAIT rebind pin; suite 285
  green twice; adversarially verified per the plan — the verify pass then
  hardened Stop/Dispose (a concurrent-Dispose ODE), made the trail's
  stopped→listening cutover order deterministic, silenced a misleading
  post-stop warn, and corrected the platform-fact attribution above).
  `api-json-discovery` (2026-07-07; Phase 3a — the credential file: a
  version-partitioned 0600 `captaind-<id>.api.json` (port, token, pid,
  identity) beside socket/lock/pid (`RendezvousPaths.ApiJsonPath` +
  `ApiDiscovery` read/write). `ApiHost` mints a 256-bit hex bearer token —
  the SOLE credential source — and publishes/removes the file UNDER the
  same gate that flips `_listening`, so "file exists ⟺ we hold the port"
  holds against a racing Stop and a retrying host never advertises a port
  it doesn't own. Version-partitioned so a draining incumbent never deletes
  its successor's file; `doctor` reaps a stale one once the lock proves the
  owner dead. No gate yet — the token is published, nothing is checked. 8
  tests; suite 292 green twice).
  `auth-token-origin` (2026-07-07; Phase 3b — the credential gate on the
  WHOLE TCP surface, before the router, so even the unwired 404 is
  unreachable without the token. `ApiAuthGate` (a pure, directly unit-tested
  internal seam) checks, in order: Host = the exact loopback authority
  (rebind), Origin present ⇒ must be ours / absent ⇒ allowed so curl works
  (CSRF), bearer token constant-time compared via `FixedTimeEquals` (authn);
  401 carries `WWW-Authenticate: Bearer`. The token is the api.json one, the
  sole credential. Platform-composed: managed `HttpListener` prefix-matches
  on Host, so a foreign Host 404s at the listener BEFORE the gate (rebind
  defense's first layer, recorded in platform.md); the API answers 127.0.0.1
  only (localhost would need a second prefix — deferred). The engine csproj
  gains one `InternalsVisibleTo` so the security logic is tested directly,
  not only through HttpListener's quirks. 22 auth tests (15 pure-gate + 7
  HTTP) plus every prior HTTP test updated to present the token; suite green
  twice; adversarially verified per the plan. Endpoints (Phase 4) inherit
  the gate for free.
  Phase 4 read endpoints — `get-status` + `get-policy-read` + `get-harnesses`
  + `get-handlers` (2026-07-07; the parallel antichain, landed as one batch —
  read-only, no adversarial verify). `GET /api/v1/{status,policy,harnesses,
  handlers}` render from an `ApiReadModel` over the SAME live resolvers,
  registry, and dispatcher the dispatch path runs, so the API view cannot
  drift (ADR-0007 d3). `/status`: identity, pid, monotonic uptime, and live
  serve counters (a new `ServeStats` replaces DaemonHost's bare `active`
  local, adding a lifetime `served` count). `/policy`: the resolved tri-state
  (absent/malformed+error/loaded+parsed doc) plus the raw file and a
  content-hash **ETag** (header + body — the token `put-policy-write`'s
  If-Match will consume). `/harnesses`: the registry projection (specs,
  adapters, request mapping, per-event capabilities). `/handlers`: every
  registration with fail mode + live supervision state — carried by the one
  new bit of plumbing, plain-data `Worker.Generation`/`IsDead` F# accessors
  (int/bool cross the boundary; the DUs stay inside) behind a
  `Dispatcher.Snapshot()`. All four inherit the Phase-3 auth gate (401
  without the token) and 404 an unknown route; a pure listener with no read
  model 404s them all. 8 endpoint tests over real Core objects + the
  daemon-integration `/status` at 200; suite green twice.
  `sse-trail-tail` (2026-07-08; Phase 5, first slice — the live stream:
  `GET /api/v1/events` is SSE over a stat-poll tail of the JSONL trail file
  (decision 5) — the file, not an in-process tee, so both emitters' halves
  and collapsed dispatches all flow. `TrailCursor` owns the sharp edges: only
  complete lines are ever emitted (bytes past the last `\n` re-read next
  poll, so a concurrent O_APPEND can never surface half-written), event id =
  byte offset after the line (`Last-Event-ID` resumes with zero dup/loss —
  byte-split before UTF-8 decode, so multi-byte content can't skew ids; a
  mid-line resume offset self-heals forward to the next boundary rather than
  emitting garbage), truncation/replacement resets the id space with an
  explicit `reset` event, an absent file is quiet-not-error. The tailer is
  SCHEMA-BLIND — ships opaque newline-delimited lines, parses nothing — so
  N4's third-consumer coupling shrinks to "newline-delimited". Per-subscriber
  `TrailSubscription` (poll task → channel → writer with comment heartbeats —
  the heartbeat doubles as the dead-client probe); streams run on the ApiHost
  stop token, so drain-start `Stop()` now terminates open streams (the
  Phase-2 stub, cashed) and `OpenStreams` tracks them (finally-decremented;
  the idle-defer slice reads it next). Channel is unbounded THIS slice —
  `sse-backpressure` bounds it. Auth-gated like every route; browser
  EventSource can't send the bearer header, so item 6's GUI uses
  fetch-streaming (noted in code). 19 tests incl. byte-offset ids over real
  HTTP, exact resume, live-end default, Stop teardown, heartbeat dead-client
  release, and the Phase-1 debt cashed: an open stream while other requests
  answer. Suite green twice; adversarial verify per plan — the resume/id math
  survived attack (probed via a standalone compile of the real TrailCursor);
  the pass then fixed, in-phase: a line longer than the read window no longer
  wedges every cursor forever (it is SKIPPED across polls and surfaced as an
  honest gap — the verify's one correctness-threatening find), a live cursor
  now detects truncate-then-REGROW via a boundary-byte re-check (offsets rest
  just past '\n'; a replaced file fails the check 255/256), a truncation
  racing inside one poll yields quietly instead of killing the subscription,
  the ApiHost stop-CTS is never disposed (a Stop∥Dispose race could swallow
  the only Cancel SSE writers ride), align-consumption polls report More
  correctly, and a drain-racing /events OCE is a routine end, not
  handlerError noise. It also surfaced a PRE-EXISTING emitter defect: .NET's
  File.AppendAllText does NOT open O_APPEND (strace-proved) — shim+daemon
  can clobber concurrent trail appends; recorded in platform.md + scratch as
  a wire-lib follow-up, reader unaffected.)
  `sse-backpressure` (2026-07-08; Phase 5 — a slow consumer gets drop-oldest
  plus an explicit gap marker with the EXACT dropped count, never a growing
  daemon, never a silent hole, never a disconnect (decision 5 / ADR-0004 d6).
  The per-subscriber channel is bounded (`SseOptions.Capacity`, default 256);
  eviction is by hand — `BoundedChannelFullMode.DropOldest` discards silently
  and could never carry the count — and the count plus the truncation-reset
  both travel OUT OF BAND (Interlocked fields the writer checks before each
  dequeue), which is what makes the gap and the reset structurally
  un-droppable: they are never in the buffer that drops. "Slow" means no
  room within one poll-beat of grace — a burst append bigger than capacity
  with a healthy consumer must not drop on a scheduler race (found by the
  first cut's own test); once pressured, evictions run at full speed until a
  first-try write succeeds. A reset clears the buffer and supersedes any
  pending gap (counting lines of a replaced file would lie). A gap carries
  no id, so a reconnect resumes from the last line id and RECOVERS the
  dropped region from the file. Deterministic stalled-sink tests: exact
  drop counts, reset-supersedes, fast-consumer-full-fidelity.)
  `idle-exit-defer` (2026-07-08; Phase 5, ADR-0004's open question cashed as
  decision 7: any API request resets the idle clock (an `onRequest` stamp
  callback into DaemonHost's `lastActive`, fired before the gate — even a
  401 proves interaction) and an open SSE subscription defers idle-exit —
  `ApiHost.OpenStreams` (finally-decremented) joins `active` and
  `BackgroundPending` in the idle watchdog's activity check, riding the same
  bookkeeping the background queue uses. CURRENT-LOCK-HOLDER-ONLY by
  construction: drain-start `Stop()` terminates every stream, so a
  superseded daemon is never pinned by a forgotten tab. `/status` now
  reports `openStreams`. FakeClock daemon tests: stream-defers/close-
  releases (a full fresh window after release), request-refreshes-the-
  window, drain-never-pinned-by-a-stream.)
  Phase-5 adversarial verifies, closed out (2026-07-08): backpressure's
  exact-count and un-droppable-marker contracts SURVIVED attack (200k-item
  probe: delivered+evicted=enqueued, exactly once; reset ordering airtight);
  touch-ups landed (skip-gaps may surface up to `capacity` lines before
  their chronological hole — positional only, count exact, recovery
  unharmed, now documented+pinned; every eviction counted regardless of
  type; FastConsumer made structurally flake-proof). The idle-defer verify
  CONFIRMED two real gaps, both fixed: (1) the immortal-daemon loop —
  decision 7's "current-lock-holder-only" was an effect of drain, not a
  mechanism, so a forgotten tab could pin a superseded daemon on the
  singleton port forever; the daemon now re-fingerprints its own deploy dir
  on quiet ticks and drains itself on mismatch (`daemon.superseded` —
  ADR-0007 d7 amendment, giving decision 2's "superseded" clause its
  missing code). (2) Probe-proven: managed `HttpListener.Stop()/Close()`
  BLOCK on Linux behind a write wedged on a zero-window subscriber — a
  synchronous teardown made one stalled client an unkillable daemon that
  never released the version lock (every same-identity hook collapsing
  forever); teardown now runs bounded-background (the port frees the
  instant Stop begins, so the handoff is unharmed), recorded in
  platform.md. Both pinned: supersession-reaps-despite-a-forgotten-tab,
  Stop-bounded-under-a-wedged-writer.
  `put-policy-write` (2026-07-08; Phase 6, the last critical-path slice — the
  ONE write verb: `PUT /api/v1/policy`, the API as EDITOR OF THE FILE, not
  owner of state. `ApiPolicyWriter` validates the body with the daemon's OWN
  strict `DispatchPolicy.TryParse` (refuse to write what the daemon would
  refuse to load), honors `If-Match` when supplied (the content-hash ETag
  Phase 4 built), and installs ATOMICALLY — temp+rename in the TARGET'S OWN
  directory, the `GetTempFileName()`+`Move` cross-device trap sidestepped — so
  `ReloadingPolicy`'s stat-gate makes it effective on the next dispatch exactly
  as a hand-edit does. A closed `PolicyWriteOutcome` DU maps 1:1 to HTTP:
  200+ETag / 422 violations / 412 If-Match mismatch / 413 over-cap / 500 I/O;
  the write inherits the Phase-3 auth gate, null policy path ⇒ 404. Wired
  through DaemonHost beside the read model (same `policyPath`). 20 tests incl.
  a concurrent-reader ATOMICITY probe (a hook stat-gating the file mid-write
  never sees a torn/absent state — the exact hazard a non-atomic write would
  flash) and an END-TO-END in-daemon proof (a live PUT of `default:deny`
  short-circuits the next real UDS hook to a Noop — the ADR's mandatory
  hot-path verify, now a committed guard). Suite 358 → 378 green twice.
  Adversarially verified: the two named sharp edges (cross-device atomicity,
  ETag round-trip) SURVIVED; fixes landed for a BOM asymmetry (the daemon's
  loader strips a leading BOM, so the writer must too — else a spurious 422 on
  content the daemon would load, and a broken round-trip), a drain-race
  `api.handlerError` (OCE now → 503, mirroring the SSE swallow), and a
  non-exhaustive outcome switch. One MODERATE routed to follow-up, not
  scope-crept: the `(mtime,size)` stat-gate can miss a same-length change on a
  COARSE-mtime FS — probe-confirmed unreachable on ext4/APFS/NTFS, a
  pre-existing ADR-0006 property; the write API just makes it programmatically
  reachable (scratch.md + platform.md).)
  `docs-flow-platform` (2026-07-08; Phase 7, the capstone — terminal by
  construction: `doc/flow/management-api.md`, the management-API flow doc
  (request-lifecycle + cutover ASCII diagrams, why-prose for discovery/auth/
  lifecycle/SSE/write, GUI notes, and a ground-truth table naming every
  endpoint, symbol, log event, and test class), including the mandated
  `TrailCursor` edge-behavior table (oversized-line skip / truncation-reset /
  alignment-self-heal / truncate-regrow). platform.md's `HttpListener`-on-Unix
  quirks (N5) recorded as met — the § Loopback TCP section already consolidates
  co-bind, SO_REUSEADDR/TIME_WAIT-pairwise, teardown-blocks-behind-a-wedged-
  write, and Host-prefix-match; now cross-linked to the flow doc. No adversarial
  verify — shipshape only. **ADR-0007 complete: all 7 phases, all 13 slices
  landed; item 5 checked.**)
  Install operations carry item 10's
  trust model with them. The fleet/enterprise shape (one org, many
  employees) is local-data-plane + central-control-plane: per-machine
  daemons exactly as today, with policy distribution / config / telemetry
  aggregation as the centralized layer this API eventually fronts —
  never a shared remote daemon on the hot path (ADR-0004's transport
  revisit trigger stands).
- [x] **6. GUI v1: browser UI** — localhost web app served by the daemon.
  Catalog + one-click install, live dispatch traces, supervision view
  (restarts/escalations as they happen). Web-first per the GUI direction
  below; on WSL2 this is the *best* UX, not a fallback. Lands WITH a
  Playwright harness (Microsoft.Playwright, same xunit suite): the DOM +
  accessibility tree is the agent-legible surface — semantic locators and
  auto-waiting beat TUI screen-scraping for the agentic dev loop.
  Design recorded in **ADR-0008** (accepted 2026-07-09): observability-first
  v1 — five screens (live trace / supervision / policy editor / harnesses /
  status) over the EXISTING API, no new data endpoints, no control verbs
  (catalog + one-click install defer to item 10's trust model + the install
  ops); a separate `web/` React+Vite project served same-origin from a disk
  `ui/` dir (the third /deploy artifact; Node dev-only, built assets
  committed); token handed off via URL *fragment* by a new `captainHook ui`
  verb; islands + one Zustand store, TS types generated from the C# DTOs,
  full-reload dev loop (HMR reserved). Playwright E2E lives in `web/`
  driving the daemon's own `/ui` — supersedes the
  Microsoft.Playwright-in-xunit leaning above. Trail growth split out to
  **ADR-0009**: the opaque-cursor resume contract is honored by this GUI's
  SSE client now; rotation/backfill deferred until growth bites.
  Build order: ADR-0008 § Implementation plan (2026-07-09; 13 slices → 7
  phases; critical path web-scaffold → dto-schema-codegen → zustand-store →
  sse-fetch-client → read-panels-islands → playwright-e2e →
  flow-doc-management-gui; adversarial verify on exactly 3 slices — the /ui
  route's traversal guard + gate split, the SSE fetch client's
  resume/reset/gap + dead-credential logic, and the policy editor's ETag
  lifecycle; no ultracode). Tick slices here as they land.
  Slices landed: `ui-static-route` + `inert-shell-tests` (2026-07-09; one
  commit — the `GET /ui[/*]` static route in `Api/`, the daemon's ONE
  bearer-exempt surface, pinned the instant it opens. `ApiAuthGate` split into
  `EvaluateShell` (Host+Origin only) so /ui and /api/v1/* can't drift on
  transport policy; exemption bearer-ONLY and scoped to exactly /ui[/...]
  (`IsUiPath` on the router's own AbsolutePath). Pure `ResolveUiFile` traversal
  guard — rooted/NUL/escape/dir/missing ⇒ null, separator-appended prefix so a
  sibling ui2/ can't pass, trailing-slash-root trimmed. 41 tests: guard proven
  against files that REALLY exist outside ui/, traversal probed over a raw
  socket, inert contract (byte-identical authed/unauthed, token never served).
  **Adversarially verified** — no escape under any encoded/mixed form
  (HttpListener collapses dot-segments pre-gate ⇒ /ui/../api/v1/status meets the
  full 401); trailing-slash hardening applied, symlinks-inside-ui trusted per
  d2). `web-scaffold` (2026-07-09; the `web/` React19+Vite6+Zustand5 project,
  Vite base '/ui/', outDir → the committed `ui/`; Node dev-only, built assets
  committed; drove the real built ui/ through the live daemon route
  end-to-end). Phase 1 complete.
  `dto-schema-codegen` + `token-handoff-bootstrap` (2026-07-09, one
  frontend-plumbing session per the plan's batching note. Codegen:
  `ApiSchema.Export` (BCL `JsonSchemaExporter`; camelCase wire casing,
  NRT-honest nullability, strict numbers) → checked-in
  `web/schema/api.schema.json` pinned by `ApiSchemaTests` — the drift detector;
  `CAPTAINHOOK_SCHEMA_UPDATE=1` regenerates — → `gen-types.mjs` derives
  `src/api.gen.ts` in every npm build; DTO change ⇒ regenerate both, same
  commit. Handoff: `web/src/auth.ts` — #t= fragment → sessionStorage →
  immediate replaceState scrub → Bearer on every apiFetch; fresh hash beats
  stale stash; 401/403 ⇒ the no-self-heal 'session ended' state. Driven
  end-to-end in headless chromium against a real isolated daemon: fragment
  parsed, hash scrubbed, stash survives reload, /status renders live, fresh
  tab inert). `ui-cli-verb` (2026-07-09; `Mode.Ui` in the wire enum + shim's
  loud refusal, `UiVerb` reads the 0600 api.json — absent ⇒ clear refusal,
  never a spawn — and opens `/ui#t=<token>` via the OS opener; the fragment
  shape is pinned against the shell bootstrap's regex; the token reaches the
  browser and NOWHERE else (stdout/stderr asserted clean); the argv
  /proc-cmdline residual recorded in scratch). `deploy-ui-staging`
  (2026-07-09; /deploy stages the committed ui/ beside the two executables —
  one swap, one bin.prev rollback, no npm at deploy; verification gains the
  same-daemon /ui shell check). Suite 417 green twice. Phase 2 complete.
  `zustand-store` (2026-07-09; the contract slice, its own deliberate pass —
  one provider-less store (`web/src/store.ts`), slices 1:1 with decision 1's
  screens + session/stream; `SseFrame` mirrors the server SSE grammar (opaque
  id per ADR-0009 d2, id-less gaps), `PolicyVerdict` mirrors the closed
  `PolicyWriteOutcome`; `foldTrace` is the one reducer — reset clears and
  supersedes the client truncation count, gaps visible, unparsable lines
  render raw, TRACE_CAP counts evictions; trail stays schema-blind
  (all-optional `TrailLine`). App/main are the first store consumers; reducer
  tested pure via node:test (zero new deps); re-driven in headless chromium.
  Phase 3 complete.
  `sse-fetch-client` + `policy-editor-island` (2026-07-09, the parallel pair,
  both **adversarially verified against live daemons** per the plan. SSE
  client (`web/src/sse.ts`): pure protocol layer under a reconnect loop — all
  five contracts held under attack (opaque-cursor resume zero-dup/zero-loss
  across 8 real TCP cuts; gap-never-advances proven against a real
  57,853-line eviction with all 60k lines recovered; reset re-anchors incl.
  cut-at-the-reset ⇒ genesis replay exactly-once; 401⇒dead-stops vs
  drop/drain⇒retry-resumes; UTF-8 through 1–5-byte chunk slices). The verify
  CAUGHT a real server defect — the from-now anchor raced the first flush,
  silently losing a line appended at client-live; fixed by anchoring the
  subscription BEFORE the retry-hint flush (anchor ≤ headers ≤ live by
  construction) — and two client hardenings landed (reader.cancel in a
  finally so a throwing subscriber can't leak the stream and pin the daemon's
  idle-defer counter; CR-free trail note). Policy editor (`policy.ts` +
  `PolicyPanel.tsx`): the ETag discipline's three pins all held (If-Match on
  every PUT once known — stale tag 412s, file untouched; 200's tag adopted,
  no re-GET; 412's current adopted, draft preserved); verify hardenings: a
  tagless 200 maps to null + GET re-seed, never `\"\"` (the server ignores an
  empty If-Match — probed live, it would write blind); submitPolicy never
  throws; saving flag released in a finally. 24 web unit tests; chromium
  drive incl. save-twice ETag adoption; suite 417 green twice ×2 runs.
  Phase 4 complete.
  `read-panels-islands` (2026-07-09; the four read screens as sibling islands
  over the store. Three fetch-and-render tables (Status — identity + serve
  counters, polled 3s; Supervision — handlers with fail mode / generation /
  dead, polled 4s; Harnesses — the ADR-0003 registry projection, fetched
  once) share one `useApiJson` hook so a 401 flips the whole session to dead
  in one place. Live trace reads the SSE-filled trace slice: dispatchId
  correlation via a stable color-per-id + click-to-filter, a substring
  filter, follow-the-tail scroll, and gap/reset as honest dividers; pure
  display logic (`src/format.ts`) unit-tested. App slimmed to the shell; one
  theme-aware stylesheet. 31 web unit tests; chromium drive of all panels +
  trace ingest/filter, eyeballed light+dark; engine untouched, C# 417 green.
  Phase 5 complete.
  `playwright-e2e` (2026-07-09; a @playwright/test suite in web/ — 10 tests, 5
  files — driving the daemon's own same-origin /ui end to end: the shell's
  bearer-exempt-but-inert boundary, the fragment token handoff (live/scrub/
  reload/bad-token-dead), the read panels over real daemon data, the live
  trace ingesting appended lines with dispatch-chip + text filters, and the
  policy editor's write path incl. the ETag-adoption pin. The reasoning is the
  daemon fixture: a fresh daemon per test, fully isolated from the live
  ~/.captainHook tree (temp XDG_RUNTIME_DIR/log/harness/dispatch), readiness by
  the 0600 api.json (polled, not slept), teardown SIGTERM-by-PID → await exit →
  SIGKILL; globalSetup builds the engine + stages the fresh ui/. The phase's
  named flakiness bit — the daemon's F#-actor warm starved under the browser's
  CPU load (a 58s stall diagnosed) — and was root-caused + fixed three ways
  (await-true-exit so drainers don't pile up, a thread-pool floor, one retry
  for the all-cores-pegged residual; proven green under deliberate 4-core
  saturation). Also driven headed via WSLg. Node/@playwright/test dev-only.
  Phase 6 complete.
  `flow-doc-management-gui` (2026-07-09, the capstone — terminal by
  construction: `doc/flow/management-gui.md` (request-lifecycle ASCII diagram +
  why-prose for islands/store/handoff/`/ui`-route/SSE-client/ETag/codegen/
  deploy/E2E + a ground-truth table naming every file, symbol, and test), and
  ADR-0008's placeholder Ground-truth section back-filled as a decision→code
  index. Docs-only; symbols cross-checked against the code. **ADR-0008
  complete: all 7 phases, all 13 slices landed; item 6 checked.**

## Later

- [ ] **22. The watcher — human nudge always on, robot nudge sometimes, and
  ask/reply** — [ADR-0017](adr/0017-watcher-nudge-and-ask.md) (Proposed
  2026-08-17). `captainHook mail status` for the statusline (📬 count, ruleless,
  never enters the loop); an in-daemon watcher, event-driven off the trail's
  own `mail.append` / `mail.cursorAdvance`, pure rules over pending +
  presence + role-kind with monotonic deadline re-checks (no cron); the robot
  nudge as an internal `MailNudge` hook event through the ordinary dispatcher
  (handlers.json = the hands, dispatch.json = the consent, budgets = the
  bound); per-harness turn payloads (`turn-claude.sh` first; the bus is the
  memory, fresh session per turn); `inReplyTo` read + `mail ask --wait` +
  hop budgets; nudge/thread marks on the canvas. E2E is load-bearing and
  model-free: a stub harness payload that fires real hooks and answers on
  the bus. Slices via `/adr-plan`. Depends on: item 21 slices 6a/7 landing
  first is *not* required; item 15 (capability policy) bounds the runners
  when it lands.
  Slices landed: `mail-status` (2026-08-17; phase 1, slice 1 — the human
  channel, and the one slice of this ADR that pays off alone.
  `captainHook mail status` prints `📬 2 · 1 urgent` per role for a harness's
  passive display (Claude Code's `statusLine`). It needs no consent surface
  because it interrupts nothing — every other channel here can spend tokens or
  take a turn, and a count is read when a human chooses to look. **The role is
  never declared twice**: which roles a window may read is already answered by
  the `mail digest` registrations that survive `dispatch.json` for this
  cwd/session — the same evaluation the dispatcher runs before it fans out — so
  a window whose digest is denied gets a silent bar rather than a count for
  mail it will never be handed, and a second file naming a window's role could
  only drift out of sync with the first. Recognition is the REAL parser
  (`MailDigest.TryParseArgs` decides whether a registration is a digest, on
  `MailCursors.List`'s rule that what counts is what the live path would
  accept), so a registration the verb would refuse contributes no role. Two
  as-built notes on d2: the line NAMES its role only when the window reads more
  than one (a bare count cannot say which of two roles it means, and every
  human window is the one-role case), and a role registered at two seams is one
  cursor and therefore one line — the naming must not switch on how the seams
  were registered. Silence is a state, on stdout and in the exit code: no
  readable role, nothing pending, an absent or malformed `handlers.json` all
  print nothing at exit 0, because a display command that failed loudly would
  put an error where a human expects a number; the only refusal is an
  unexpected argument, which is a wiring typo a human can fix and which
  reporting as "no mail" would hide forever. Expired mail is uncounted (spent —
  pointing a human at mail no digest will ever hand over is the one way this
  line can lie), held mail is counted (undelivered is exactly the state it
  surfaces). It reads and only reads — `Pending` returns a re-anchor rather
  than stamping one, the store creates its directory only on Append, and
  nothing logs (a trail line per status-bar render would drown the trail it
  helps you read) — driven by a test that runs it repeatedly and asserts the
  store's bytes and the cursor set unchanged, plus an absent bus that stays
  absent. The cost is stated rather than discovered: every render re-reads the
  whole store, which is unbounded until rotation (ADR-0016 N4). 30 tests
  (`MailStatusTests`), suite 971 → 1001 green twice; smoked live read-only
  against the maintainer's own bus, and in a sandbox for the two-role and
  policy-denied renderings.)

- [ ] **23. Instance addressing + the reaper** —
  [ADR-0018](adr/0018-instance-addressing.md) (Proposed 2026-08-17).
  `to: role` stays broadcast; `to: role@instance` is unicast to one durable
  mailbox named by its registration (`mail digest --as`); cursor key becomes
  role × instance (session id as the unnamed fallback — backward compatible);
  unicast has no TTL (refused, not ignored); dead mailboxes are detected by
  the watcher and disposed of by a `reaper` role — forward with
  `forwardedFrom` / drop / hold, then a logged `mail reap` — never deleted
  automatically. `address-grammar` + `instance-registration` precede ADR-0017
  phase 2 (answers go `to: asker's role@instance`; 0017 d8's preference hack
  disappears). Reaper authority left open until its slice. Slices via
  `/adr-plan`.
  Slices landed: `instance-registration` (2026-08-17; phase 2 — `mail digest
  --as <instance>` names the mailbox a registration reads, and the cursor key
  becomes role × instance (`--as` ?? session id) through the existing
  `CursorPath`. An unnamed reader is EXACTLY the reader ADR-0016 built —
  proven, not asserted: the reducer's checked-in golden corpus did not have to
  be regenerated for this slice. A named one is durable, and two windows under
  one name share one cursor (first pickup consumes), which needed no new
  concurrency because `Advance`'s per-cursor flock already decides who wins.
  Both halves of an address are grammar-checked AT REGISTRATION against
  `MailAddress.IsRole` — the envelope parser's own predicate, never a second
  spelling — since a `--role`/`--as` no sender could address is a mailbox that
  reads nothing forever, silently. **The sharp edge, and what it cost:** the
  cursor keys on the instance while the trail keeps the window, so
  `MailPendingView` grows `HookSession` beside `Session` (which IS the cursor
  key); `sessionId` still answers *who moved it* and a new `instance` column
  answers *which mailbox*, written ONLY when the two differ. Collapsing them
  would cost one or the other — key on the session and a named mailbox stops
  being durable; log the instance and two windows sharing a name become
  indistinguishable. `MailCursors.Pending` became two OVERLOADS rather than one
  optional parameter, and the golden corpus is why: a defaulted
  `hookSession = null` reads as harmless and silently unlinked every trail line
  from its window, which the golden caught on the first run — so the SHORT call
  is the safe one and the split has to be spelled out to be taken.
  `mail status` had to follow the same key in the same commit (the phase-4
  slice `mail-status-per-instance`, folded in as the plan allows) or a named
  window's bar would count a mailbox nobody reads; a qualified line now names
  its full address, the spelling a sender would use. **One gap left honest:**
  the read-only snapshot cannot tell an instance-keyed cursor from a
  session-keyed one — the file name is just the key, and learning otherwise
  means reading `handlers.json` from a port that deliberately reads only the
  mail dir — so a named mailbox shows in presence as a session no window is
  called. The live trail CAN tell (that `instance` column), so the picture is
  recoverable from the stream; making the SNAPSHOT say it belongs to
  `canvas-instances` with its sub-lanes, rather than to a heuristic here that
  would guess a mailbox's kind from its name. 17 tests
  (`MailInstanceRegistrationTests` 13, four `MailStatusTests` cases); suite
  1057 → 1074 green twice.)
  Slices landed: `unicast-refuses-ttl` (2026-08-17; phase 2 — taken
  immediately after slice 1 rather than in parallel with its phase-mates,
  because slice 1 opened a window worth closing: `MailStore.Serialize` writes
  `ttlDeliveries` unconditionally, so between the two slices EVERY unicast
  envelope would have landed on the append-only chain carrying a ttl — even
  one whose sender never asked for a ttl — that this slice's parser then
  refuses to read back. Malformed forever, warned-and-skipped by every future
  reader. The window was empty in practice (nothing can send unicast in anger
  until `instance-registration`), and it is now closed rather than migrated.
  `ttlDeliveries` is REFUSED on a `role@instance` address, not ignored — an
  accepted-and-ignored field is a lie in a record nobody can amend — and the
  refusal supersedes the `>= 1` bound, since a `ttlDeliveries: 0` unicast
  envelope has one thing wrong with it and the address is the reason.
  `MailEnvelope.TtlDeliveries` becomes `int?` where null has exactly one
  meaning (unicast) and is never a second spelling of the default; the stored
  line and `mail.append` OMIT the field, which is this format's existing
  spelling of absent (`session`, `inReplyTo`) and keeps the column's type
  rather than making it a string `"none"` that every consumer special-cases;
  `MailCursors.Pending` guards the expiry comparison, so a held unicast is
  never spent — bounded by the reaper's judgement (d6), not by an arithmetic
  that quietly drops unread mail. The read half followed the pipeline end to
  end: DTOs → `api.schema.json` → `api.gen.ts` → reducer → canvas, with three
  renderings that say what they know (`n held` on the mark, `none — unicast,
  delivered once to <addr>` on the card, "unicast mail does not expire" on the
  standing line) instead of a countdown with no denominator. **The slice added
  no write-side validation, and the finding is why:** `MailStore.Append`
  already re-parses the exact bytes it is about to make durable and refuses a
  line the strict parser would reject, so the plan's named failure — Serialize
  writing a ttl the parser refuses — cannot corrupt the chain; an envelope
  whose ttl contradicts its address is constructible in process, impossible
  from the wire, and refused AT THE APPEND. Pinned by
  `Append_RefusesAUnicastEnvelopeCarryingATtl` and the round trip the verify
  note asked for, `Render_UnicastLine_ReParsesCleanWithNoTtl`. The reducer
  learns the same rule from the other side — a role address must carry a ttl,
  a unicast address must NOT, anything else is a line the engine could not
  have written and is refused rather than repaired (N8: the reducer
  interpolates, it does not second-guess the store). 9 C# tests + 4 reducer
  tests; suite 1048 → 1057 green twice, web 249 → 253, mail e2e 18 green.)
  Slices landed: `address-grammar` (2026-08-17; phase 1, slice 1, alone as
  planned — the forever pin. `to` stops accepting anything and parses as
  `role` or `role@instance`, `[a-z0-9][a-z0-9-]*` per half, one `@`, both
  halves non-empty; `MailAddress` is the whole decision and
  `MailEnvelope.TryParse` is where it binds — the single choke point every
  write path crosses (`mail send` parses before it appends, the store
  serializes a parsed record), so an ungrammatical address cannot reach the
  append-only chain. Introducing the separator IS introducing the grammar:
  `@` can only mean "instance follows" if it can mean nothing else. **Nothing
  routes on the instance yet** — a `role@instance` envelope parses, is carried
  verbatim, and is addressed to nobody, which is the right shape for a slice
  whose risk is PERMANENCE rather than slip-through: what parses today is what
  the ledger holds forever, and a grammar loosened after mail is on the chain
  cannot be tightened again. Three as-built calls, each on the ADR's Ground
  truth: scoped to `to` ALONE (`from.agent` is a provenance label nobody
  routes on, and constraining it would refuse ledger lines for a property
  nothing reads); lowercase **pinned rather than folded**, diverging from
  `kind`/`priority` because those are closed sets a parser can correct a
  casing slip against while an address names an open universe — and folding
  here while `CursorPath`'s percent-encoder keeps `Ops` and `ops` as two
  cursor files is ADR-0016 N8 wearing an address for a hat; and the
  alphanumeric test spelled out in ASCII rather than `char.IsLetterOrDigit`,
  which is Unicode-aware and would admit `mаintainer` with a Cyrillic а — a
  mailbox that renders identically to a real one and receives none of its
  mail, the exact silent misrouting d2 exists to refuse. A second `@` is
  refused, not split-on-first or split-on-last: `a@b@c` has two plausible
  readings and picking either is guessing. The compatibility corpus is NAMED
  rather than asserted in the abstract — every role on the maintainer's live
  ledger and in the suite's fixtures (`maintainer`, `reviewer`, `scribe`,
  `main`, `auditor`, `other`, `intent-watcher`, `s1`) is lowercase-legal and
  pinned as a theory, since orphaning mail already on the chain is the one way
  this slice could have failed quietly. 47 tests (`MailAddressTests` 17, the
  address block in `MailEnvelopeTests` 30), suite 1001 → 1048 green twice.
  Docs: flow doc § *The address grammar*, plus a count audit on the way past.
  Every mail test count in the flow doc AND in ADR-0016's Ground truth was
  re-measured from the runners; five were wrong — envelope table 26 → 115
  (the `26` counted methods, not the cases xunit expands them to),
  `MailStoreTests` 43 → 46, `MailDigestTests` 54 → 70, `mailCanvas.test.ts`
  40 → 39, `mailStream.test.ts` 11 → 10 — the last two stale in the sweep that
  had just claimed to correct three others. The flow doc's Ground truth now
  opens with the two commands that reproduce every number in it, because a
  count nobody can re-derive in one command is prose wearing a table's
  clothes.)

- [x] **20. The mailbox bus — cross-harness agent communication** — the hub
  reframe: N external agent loops (Claude Code + any hook-bearing harness),
  one daemon as the store-and-forward bus. Mail is written by appending an
  envelope to a durable disk store; it is *delivered* only at seams the
  recipient's harness declares (turn-start inject / mid-turn urgent /
  Stop-block reconcile), with per-(role, session) byte-offset cursors,
  delivery-opportunity TTLs, and cursor-advance-on-inject as the Stop-loop
  guard. Members span deterministic gates (key-redactor), write-only
  observers (edit log), on-demand LLM watchers, and full peers — LLM-ness is
  a payload detail the bus never sees. Zero core: two engine CLI verbs
  (`mail send` / `mail digest`), payloads, and data; swarm activation is a
  dispatch-policy flip, not a boot verb; ADR-0011's consent gate stays
  per-executable. Ask/reply correlation explicitly deferred (envelope
  reserves `inReplyTo`). Provenance/governance designed in, not bolted on:
  delivery is a `mail.deliver` ledger event closing the cross-agent causality
  chain, the mail store is hash-chained (trail chaining deferred with the
  two-emitter lock cost named), policy reloads gain content hashes, and the
  three stores get three lifetimes (cursors ephemeral / trail operational /
  mail archival, all 0600). Design: **ADR-0016** (accepted 2026-08-12).
  Build order: ADR-0016 § Implementation plan (2026-08-12; 11 slices → 6
  phases; critical path mail-envelope-parser → mail-store-chained-append →
  mail-cursor → mail-digest-handler → cursor-edge-adversarial-tests →
  swarm-profile-and-flow-doc; adversarial verify on exactly five slices —
  store, cursor, digest, the adversarial-test campaign, stop-seam; no
  ultracode). Sequencing hazards named in the plan: the on-disk chain format
  is durable (nothing writes real data before phase 2's verify settles it),
  and dogfood lands strictly LAST — no live payloads on the maintainer's
  session until the exactly-once tests and the Stop-loop pin are green.
  First dogfood target: both agents' PostToolUse streams into one edit log
  with stale-view warnings — the payload only the hub position makes
  possible. Tick slices here as they land.
  Slices landed: `mail-envelope-parser` (2026-08-12; phase 1 — d2's envelope as
  a strict parser on the `DispatchPolicy.TryParse` precedent: every violation in
  one pass, all-or-nothing accept, unknown AND duplicate fields malformed, never
  a throw on bad DATA. The failure DIRECTION is the mirror of the policy
  parser's and drove every judgment call — a malformed policy poisons the door
  loudly, a malformed envelope is warned-and-skipped, so too-loose delivers what
  nobody can render and too-tight silently drops real mail. Four calls the
  ADR left to the slice: `session` inside `from` is OPTIONAL (a write-only
  hookless member is a real membership class per d5 — requiring it would make
  the bus's cheapest tier unrepresentable); `priority`/`ttlDeliveries` are
  optional with defaults that fail SAFE (lowest-traffic seam class, bounded TTL
  — a forgotten field can never buy the mid-turn budget nor mean "forever"),
  while an unknown priority is still malformed rather than silently downgraded;
  `ts` is REQUIRED but format-unvalidated (the store is the influence record
  per d13, and nothing may ever parse or compare it — TTL is delivery-counted);
  and `prev` (d11's chain link) is a KNOWN optional field, because the store
  appends it and a strict parser that had never heard of it would read every
  chained line as malformed the moment phase 2 lands — the NAME is reserved,
  the encoding stays phase 2's durable-format decision. `TryParseLine` gives
  the JSONL reader one thing to check: torn final lines, garbage, and two
  values on a line land in `errors` beside schema faults, so one bad line can
  never throw its way out of a digest run. The lone-surrogate guard from the
  ADR-0015 skeptic pass is carried over — JsonDocument defers unescaping, so
  `"\ud800"` parses fine and throws at GetString, which without the guard takes
  down whichever reader is walking the store. 68 units.)
  `policy-content-hash` (2026-08-12; phase 1's batch-along, d12 — the trail saw
  policy EFFECTS (`policy.reload`, `policy.skip`) but never policy CONTENT, so
  "which rules were in force at time T" was unanswerable from the ledger alone.
  `PolicyContent.Of` stamps SHA-256 + byte size onto the `policy.reload` emit
  and onto a NEW `policy.write` from `ApiPolicyWriter`. The whole slice is one
  AGREEMENT question, and three sub-decisions settle it: the stamp is over the
  LOADER's view — the BOM-stripped text, which is what `File.ReadAllText`
  returns and what the writer installs — so a document written through the API
  hashes identically when the daemon reloads it (hashing raw file bytes would
  give one document two identities and make one human edit look like two on the
  ledger); hash+bytes are OMITTED, never zeroed, when there are no bytes to
  name, since hashing `""` would put a real-looking empty document on the
  ledger for a file that was never there; and a schema-MALFORMED file still
  stamps, because it IS in force — as deny-everything (ADR-0006 d4) — and an
  audit reconstructing time T needs to identify it. Stamped from the same read
  that classifies, never a second one, so the hash can't name a document other
  than the one now live. `policy.write` fires on the WRITTEN path only and logs
  under src `policy`, not `api`: a 422/412 changed no rules, the ledger records
  what was in force rather than what was attempted, and one src filter now
  shows a document's whole life. The full 64-hex hash prefix-joins the
  `GET /policy` ETag (same input, different surface). +12 units incl. the
  end-to-end write⇄reload same-hash pin and, from the `/shipshape` pass, the
  three no-bytes/no-emit edges the first cut proved only at the `Resolve`
  layer. **Phase 1 complete.**)
  `mail-store-chained-append` (2026-08-12; phase 2 — d11's durable store:
  flock-serialized chained append + `Read` + `VerifyChain`, built opus
  tests-first (37 pins), **fable skeptic pass run 2026-08-12** — the
  chain-under-concurrency attack AND the format sign-off the plan demanded.
  **Format SIGNED OFF as durable**: genesis = 64 zeros (an absent `prev`
  cannot distinguish "first line" from "head deleted"); `prev` =
  lowercase-hex SHA-256 of the previous line's EXACT bytes excluding its LF
  (the terminator is framing, not content — a torn line hashes by the same
  rule as every other); torn tails TERMINATED never repaired, the terminator
  riding the next append's single write so a crash mid-append reduces to the
  case already handled; and — the `gen` question the sign-off had to answer,
  settled in-pass before any real byte exists — **ROTATION STARTS A NEW
  CHAIN**: every generation is an independent self-verifiable file, a
  cross-file `prev` refused (it would make a file unverifiable in isolation,
  and d13 archives/prunes generations independently), with the honest cost
  stated that deleting a whole archived generation is chain-invisible —
  cross-generation continuity belongs to the cursor's `gen`, never to `prev`.
  **Three real finds, all fixed in-pass.** (1) `Append` could write a line
  its OWN strict parser rejects — an in-process envelope value (ttl 0, an
  out-of-range enum cast, a blank `to`) rendered without complaint and landed
  on disk where every future reader warns-and-skips it: mail silently lost
  behind a successful-looking append, the exact too-loose failure the
  envelope slice's direction-of-failure note warned about. Append now
  validates the EXACT rendered bytes through `TryParseLine` and refuses with
  the violations, nothing written — every line the store writes re-parses
  clean, enforced rather than assumed. (2) `VerifyChain` read an IN-FLIGHT
  append as corruption — readers deliberately never take the lock, so a
  verify racing a concurrent send can catch the tail mid-write(2); the
  unterminated-tail fault now says what it can be (interrupted write or
  append in flight, terminated and chained over by the next append) instead
  of crying tamper, pinned in both directions (suffix present unterminated,
  gone once terminated). (3) The `mail.torn` warn reported the NEW line's
  offset under the name `offset` in a warn about the TORN line; `TailLink`
  now reports the torn line's own start. Survived attack unbroken: the flock
  protocol (`FileShare.None` = LOCK_EX, kernel-released on any death incl.
  SIGKILL; lock file never unlinked per the DaemonRendezvous fresh-inode
  rule; bounded monotonic wait per invariant 2), both crash windows (die
  holding the lock / die mid-write — the single-payload write means every
  partial completion reduces to a torn tail), one-corruption-one-fault (the
  expected link is computed from actual bytes, no cascade),
  sender-supplied-`prev` forge-resistance, whole-line hashing under window
  growth (a 300KB line hashes whole), and the empty-line /
  consecutive-newline edges traced coherent between `TailLink` and `Read`.
  Named carry-in pinned to the `mail-cursor` slice: the store accepts lines
  of ANY length but d4's TrailCursor semantics DROP oversized lines — the
  cursor must size its window above anything `mail send` accepts or the send
  verb must cap the body, else big mail is written durably and never
  delivered. 42 store tests; suite 766 → 771 green twice. **Phase 2
  complete — the on-disk chain format is settled; real data may now be
  written.**)
  `mail-cursor` (2026-08-12; phase 3's hard half — d3/d4/d6/d13's
  per-(role, session) delivery cursor, built fable per the plan's
  hard-reasoning row, **independent skeptic pass run same day** (a fresh
  fable agent with no stake in the design). THE SHAPE, settled beyond the
  ADR's sketch and annotated as-built at d4: a bare offset cannot express
  out-of-file-order delivery — a mid-turn seam delivering urgent past held
  ambient forces a single offset to either lose the held line or double the
  delivered one, the exact "too early loses mail, too late double-injects"
  trap — so the cursor is a read FRONTIER plus `held`, a bounded exception
  list before it (offset + id + seenAt), delivered mail being structurally
  ABSENT rather than flagged; plus `head`, the chain's first-line hash, as
  the chain-native rotation check beside `gen` (phase 2 settled that every
  generation restarts at genesis, so rotation IS a head change; gen stays 1
  until d13's rotation machinery exists). THE TTL CLOCK: `deliveries`
  increments once per Advance — an envelope stamped seenAt at its first
  pass-over expires when deliveries − seenAt + 1 ≥ ttl ("passed over at N
  opportunities", exact at ttl=1 and 2, pinned), reads-without-advances age
  nothing, and no wall clock appears anywhere (asserted on the bytes).
  RE-ANCHOR: absent anchors at 0 (store-and-forward — offline mail reaching
  the next holder of the role IS the feature); malformed / foreign gen /
  changed head / offset past frontier / offset off every line boundary /
  held entry the file contradicts each re-anchor loudly preserving the
  monotonic deliveries counter, the frontier never enters an unterminated
  tail (TrailCursor's half-written-line rule one layer up), and cursor
  filenames percent-encode (role, session) so a hostile role cannot escape
  the mail dir. The phase-2 carry-in CLOSED at the write: `MaxLineBytes`
  (128KiB, TrailCursor's window precedent) enforced in Append. **The
  skeptic's attack earned three real fixes**: (1) the staleness guard — the
  If-Match-shaped deliveries check that refuses a stale view — was an
  unlocked check-then-rename, so two concurrent digests for one (role,
  session) could both pass it and double-inject: Advance now runs the guard
  under a per-cursor flock (the store's own "flock is a CORRECTNESS
  requirement" reasoning, one layer up; `MailStore.TryLock` shared), making
  it the authoritative at-most-once backstop, pinned by a held-lock
  bounded-fail test; (2) a PARSEABLE cursor with duplicate held offsets
  passed every check, double-rendered the envelope in one digest, and made
  Advance THROW through its never-throw contract — duplicate held offsets
  are now malformed (duplicate held IDS stay legal: the store does not dedup
  ids), a held entry addressed to another role re-anchors, and Advance's
  whole body is failure-as-value; (3) `head` was taken from lines[0] even
  when the first line was an unterminated tail, so a store born from an
  in-flight first append would later fire a tamper-flavored "different
  chain" false alarm — head now comes from complete lines only, pinned by a
  torn-only-store test. Stated-in-pass rather than fixed (each a sentence in
  the header + a pin): a re-anchor RESURRECTS expired mail with a fresh TTL
  (the seenAt stamps die with the held list; d13's redelivery cost includes
  it — expiry is a one-way door only while the cursor lives), the TTL unit
  is ADVANCES not seams-where-deliverable (a chatty urgent turn burns held
  ambient TTL — managing that is pinned as phase 4's planner obligation),
  `mail.expire` now lands AFTER the rename so the ledger states a fact, and
  `session_id: ""` normalizes to the sessionless cursor instead of sharing
  its file. 31 cursor tests + 44 store; suite 771 → 804 green twice (one
  unidentified single-test failure in one full run did not recur across two
  subsequent full runs + 15 mail-subset runs — watch for recurrence).
  `mail-send-verb` (2026-08-12; phase 3's cheap half, d7's universal write
  path — `captainHook mail send`: one JSON envelope on stdin, phase 1's
  strict parser, phase 2's chained append, one exit code; anything that can
  run a process can put mail on the bus with no jq and no hand-rolled JSON
  (the ADR's rejected shell-script alternative, inverted into the verb's
  whole reason). `Mode.MailSend` joins the wire lib's argv contract (`mail
  <subverb>`, the subverb riding EventName; the ENGINE judges it — unknown
  subverbs are a loud usage error, `mail digest` reserved for phase 4) and
  the shim's refusal list grows the verb (aot-boundary rule 11's discipline;
  the boundary doc's generic "engine-only modes are refused loudly" needed
  no edit). `ts` IS STAMPED at the verb when absent — d2's "every writer
  goes through mail send, which stamps it"; wall-clock UTC, invariant 2's
  display-timestamp carve-out — and the stamp REBUILDS the object copying
  every property verbatim (unknown fields, duplicates, all of it), so
  stamping can never launder a malformed envelope past the strict parser
  (pinned: a duplicate `to` survives the rebuild and is refused); a sender's
  own `ts` is kept verbatim, and a stamp that cannot be applied degrades to
  the parser's own report rather than ever throwing. MailSend.Run takes
  injected streams (the doctor/ui precedent) so the verb is driven entirely
  in-suite; 9 tests including the end-to-end promise the cursor slice was
  owed — send → store → chain verifies → cursor delivers — plus the argv
  parse, the shim refusal, and the MaxLineBytes refusal surfacing on stderr.
  Driven live through the real CLI against a scratch dir: stamped ts, frozen
  defaults, genesis prev, 0700/0600 modes, garbage rejected loudly with
  nothing written. Suite 804 → 813 green twice. **Phase 3 complete — mail
  has a real write path and every recipient a position in it; next is phase
  4, `mail-digest-handler`, the semantic core.**)
  `mail-digest-handler` (2026-08-12; phase 4, the semantic core — d5/d7/d10's
  read path, built fable per the plan's hard-reasoning row, **independent
  fable skeptic pass run same day**. THE SEAM CLASS IS REGISTRATION DATA:
  "is PostToolUse a mid-turn seam?" is a loop-position fact no HarnessSpec
  field carries and no event NAME answers without hardcoding one harness's
  vocabulary — and d7 already says registration is configuration — so the
  registration declares it (`--seam ambient|urgent|reconcile`, one
  handlers.json entry per seam class) and the planner stays a pure function
  (priority × seam class × declared verbs → deliver|hold|degrade) with no
  per-harness code. The matrix as built: ambient/reconcile-class seams
  deliver ALL priorities (the cursor slice's pinned obligation — once a seam
  advances, everything held ages, so holding at an advancing seam only burns
  TTL); urgent-class delivers urgent only, and a QUIET urgent seam noops
  WITHOUT advancing (three consecutive quiet mid-turn seams age a ttl-1
  envelope by zero, pinned). Vehicle downward only with inject preferred at
  EVERY class; an event the spec does not declare — or declares effectless
  (claude Stop today) — delivers nothing and never advances, deliberately
  STRICTER than the permissive capability gate, because the gate noops
  AFTER the advance and that direction is silent mail loss. Rendering is
  deterministic and golden-pinned: priority rank then arrival order,
  provenance per item with the envelope id as the store join key, per-seam
  char caps (4096 / urgent 1024, `--max-chars`) at whole-item granularity
  (a capped tail stays PENDING — only rendered offsets ever advance),
  expired mail named once in the digest that drops it. ADVANCE BEFORE EMIT
  is the order-of-operations contract at the verb: everything that could
  stop the effect is checked first, a failed advance answers noop with the
  mail still pending, and the same seam asked twice delivers exactly once —
  the Stop-loop guard's shape, pinned at the verb AND through a real-daemon
  smoke (the engine registered as ITS OWN payload: handlers.json command =
  the co-located apphost, seeded mail riding `additionalContext` on the
  first prompt, the cursor on disk in the sandbox, the second prompt
  clean). `--resident` speaks ADR-0010 d3's lock-step protocol, because an
  urgent-class registration fires per tool call and a cold JIT start per
  dispatch is the tax ADR-0004 d7 killed.
  **Skeptic pass: four real finds, all fixed in-pass, plus three
  hardenings.** (1) The truncation path could deliver a CONTENT-FREE digest
  — a sender-controlled topic longer than the cap (or a pathological
  `--max-chars`) cut the item block from the FRONT, erasing id and sender
  while the cursor advanced past the mail; truncation now cuts the BODY
  only, the provenance head always renders whole with every
  sender-controlled field display-clamped, and the id moved ahead of the
  topic. (2) The "hard cap" was not hard: the expired parenthetical
  appended unbounded sender data OUTSIDE maxChars — probed at 50,285 bytes
  into a mid-turn answer via a 50KB envelope id; now a count + at most
  three clamped ids, the whole urgent-seam answer pinned < 4KB. (3)
  `--seam reconcile` mis-registered on a decide+inject mid-turn event
  (PreToolUse) turned an ambient status message into a DENIED tool call;
  the vehicle preference is inverted — inject at every class, decide only
  when inject is absent — so the block shape is reserved for events whose
  only loop verb is decide (Stop's phase-5 shape, unchanged). (4) The
  resident malformed-line recovery was self-defeating: its un-echoed noop
  is itself the protocol error the engine kills a conversation over; the
  error reply now lifts a best-effort dispatchId from the rejected line
  (addressing a failure report is not guessing at mail), and true garbage
  stays an honest protocol kill, stated. Hardened from the pass's PLAUSIBLE
  finds: the exec-child env allowlist gains the engine's own config paths
  (`CAPTAINHOOK_MAIL_DIR`/`HARNESS_DIR`/`LOG` — ADR-0010 d5 amendment
  note; a child resolving DEFAULT paths under a redirected daemon read the
  wrong mailbox and logged into the live tree), and a resident re-resolves
  its harness spec per envelope through the daemon's own stamp-gated
  reload (a spawn-frozen view is advance-then-gate-swallow; pinned by a
  between-envelopes spec edit that leaves the newly-arrived mail pending),
  plus strict `--seam` spelling (Enum.TryParse comma lists refused).
  Survived attack unbroken: advance-before-emit and at-most-once (no
  in-contract double-inject or lost-without-trace envelope constructible),
  quiet seams age nothing, all nine matrix cells, every emitted shape
  through ExecWire.ParseAnswer, lone surrogates unreachable past the
  envelope parser, oneshot answer-wins-over-exit. Named residue, stated
  not engineered around: the answer still crosses the dispatcher merge and
  the capability gate after the advance, so a co-registered deny/replace
  handler on the digest's event can eat a delivered digest — registration
  guidance (examples/payloads) says give the digest its seam events to
  itself. 55 digest tests (46 + 9 skeptic pins); suite 813 → 868 green
  twice. **Phase 4 complete — next is phase 5's hardening train:
  `mail-deliver-ledger-event` → `cursor-edge-adversarial-tests` →
  `stop-reconcile-seam`, with dogfood strictly last.**)
  `mail-deliver-ledger-event` (2026-08-13; phase 5's first, d10's OTHER
  direction — the digest already told the recipient who was speaking; now the
  LEDGER records what the recipient was shown. `MailDigest.LogDelivery` emits
  `mail.deliver` (src `mail`, info) carrying envelope ids + `renderHash` +
  `bytesInjected`, closing the cross-agent causality chain on join keys that
  already exist: envelope → this event (ids ↔ dispatchId ↔ session) → the
  recipient's own later hook events. Four judgment calls, each with a
  direction of failure. (1) ONLY where mail was really consumed: the single
  `MailCursorWrite.Written` branch. The sharp case is the digest that planned
  AND rendered a delivery and then could not advance the cursor — it degrades
  to noop, the mail is still pending, and a ledger line there would be the one
  false claim that matters (mail recorded as seen that the recipient will be
  shown again). (2) AFTER the answer is written, on the `mail.expire` ordering
  rule: a crash between advance and write leaves the ledger SILENT rather than
  asserting a delivery that never reached stdout — the ledger may under-claim,
  never claim falsely. (3) The stamp describes the bytes the effect ACTUALLY
  carried (`PolicyContent.Of`'s shape — full 64-hex SHA-256 + UTF-8 count over
  `render.Text`), so a cap-truncated delivery hashes truncated: the store
  proves what was written, this proves what was shown, and an auditor
  comparing the two is exactly how "A only saw part of this" surfaces. (4)
  Bounded by data, never by a sender: ids are display-clamped by the SAME
  clamp the digest head uses, which also makes the ledger id and the digest
  line's id the identical string — the two surfaces join to each other
  verbatim. `vehicle` joins the ADR's field list as the one fact nothing else
  on the ledger can reconstruct: whether the digest informed the loop or
  BLOCKED it. **ADR d10 annotated as-built**: the sketch's nested
  `recipient: {role, session}` ships as the first-class `sessionId` column
  instead — nesting would have made mail delivery the one event invisible to
  every existing session filter (JSONL, API stream, GUI trace) — with `role`
  in data; an empty `dispatchId` (what a collapsed run puts on the wire) is
  omitted rather than blank, since as a join key it joins nothing. Proven
  where it has to work: the daemon smoke now reads the SANDBOX TRAIL FILE
  after a real spawned child answers a real hook — a separate process, so the
  event only lands if the child's own Log sink resolves to the engine's JSONL
  — asserting exactly one delivery (the second prompt delivered nothing and
  claims nothing) and the adopted dispatchId on it. The `/shipshape` pass
  closed the one gap it found — the emit-AFTER-write ORDER, which the whole
  under-claim-never-false-claim argument rests on, had no test naming it;
  pinned now by a stdout writer that records how many deliver events exist at
  the instant the answer is written (zero). 14 ledger tests + the smoke
  assertions; suite 868 → 882 green twice.)
  `cursor-edge-adversarial-tests` (2026-08-13; phase 5's campaign — the
  exactly-once tests the dogfood gate waits on, built fable per the plan's
  "the slice IS adversarial thinking" row, **independent fable skeptic run
  on the tests themselves** per the plan's verify column. Everything drives
  real files and real locks: two barrier-started advances from ONE view
  through separate MailCursors instances (exactly one Written, every round),
  two full digest verbs racing one (role, session) (one inject, one noop,
  one `mail.deliver`), and the drain soak — three senders appending under
  flock contention against two racing digests, every envelope rendered into
  exactly one digest, chain verifying clean. **Designing the fixtures found
  a real double-inject** and the fix shipped in-slice: `Advance` never
  re-checked the STORE against the view, and the staleness guard's
  deliberate "a disk cursor on a different chain vouches for nothing" is
  exactly backwards when the VIEW is the stale side — a view from a
  replaced chain sailed past it and clobbered the cursor a fresher digest
  had written for the new chain, whose already-delivered mail then re-pended
  on the next read's re-anchor: constructible today by hand, a legitimate
  race the day d13's rotation replaces chains for real. `Advance` now
  re-reads the store's identity under its lock (`MailStore.HeadHash`, the
  same first-complete-line rule `Pending` records — agreement pinned as its
  own tripwire, including the bare-newline and past-the-cap-first-line
  states) and refuses a view of a chain that is gone; both guards proven
  load-bearing by MUTATION probes (disable either, watch its tests fail),
  not just by passing. The rest of the campaign pins each accepted
  deviation in its stated direction: advance-then-crash loses the digest
  VISIBLY (store durable, `mail.cursorAdvance` on the trail, no
  `mail.deliver` — the ledger under-claims, never falsely); same-head
  truncation mid-read passes the guard honestly (chain-invisible, phase 2's
  own statement) and lands on the truncation-reset's loud redelivery; a
  foreign oversized line — forced past the write gate — is DELIVERED
  truncated with the marker, never size-skipped (the windowless read means
  skip would be silent loss); a chain break behind the frontier is one
  audit fault while delivery continues (refusing to deliver over tampered
  history would turn tamper-evidence into denial of service); an
  expired-only mailbox noops untouched until the next DELIVERING digest
  names and flushes it; and one full turn's seam interleaving — urgent
  delivered mid-turn structurally gone at reconcile, held ambient arriving
  there aged, second reconcile clean. **Skeptic pass on the tests: nine
  findings, all addressed in-pass.** The real one: a cursor DELETED
  mid-race at deliveries 0 lets both racers deliver — quietly, because
  no-cursor-at-zero is indistinguishable from first contact — contradicting
  the campaign's own "every accepted deviation is loud" header; pinned now
  as d13's stated deletion cost (so nobody later "fixes" it with a guard
  that would refuse every first contact), with the distinguishable variant
  made loud (`mail.cursorVanished` warn on advancing over a vanished
  lineage) and guard refusals made first-class on the trail
  (`mail.cursorRefuse`, info — usually a legitimate concurrent delivery
  winning the race). Also from the pass: the drain soak's noop-break had a
  hole patched only by fixture-specific settings — final sweep now loops
  until a genuine noop; the clobber regression asserts WHICH guard refused
  (green-on-incidental-failure was the exact rot the charter names); the
  id-count helper counted body lines (a body could fake a delivery — now
  head-lines only); and the chunk-spanning HeadHash test used a line Append
  would happily have written (now genuinely past the cap). The skeptic also
  traced the guard's residual window TIGHTER than built: a competing
  advance needs the same per-cursor flock the guard holds, so nothing can
  deliver inside it — an in-window replacement degrades to a loud re-anchor,
  and the silent shape needs a head flap (d13's `gen` owns it). The
  mail-cursor slice's watch item recurred: the unidentified single-test
  transient struck twice across this slice's ~12 full runs (one test each,
  neither identified — both captures truncated to summaries), then refused
  to reproduce across 7 subsequent fully-captured runs; the watch stands,
  and full-run output is now captured to a file as a habit. 15 campaign
  tests (`MailCursorEdgeTests.cs`); suite 882 → 897 green twice.)
  `stop-reconcile-seam` (2026-08-13; phase 5's third — the turn-end seam
  turned on. ADR-0016 d5 left one conditional open: enabling Stop is a data
  edit UNLESS the adapter's `Decide` rendering is not event-appropriate there,
  in which case it is coded-adapter work. **The conditional fired**, and
  finding out which way it fell was most of the slice. The published hooks
  docs were fetched twice and gave two DIFFERENT nested shapes
  (`hookSpecificOutput.decision`, then `.permissionDecision`), neither
  matching what the installed harness actually parses — so the contract was
  read off the shipped binary's own schemas instead: `hookSpecificOutput` is
  a union keyed on `hookEventName` with **17 members and no `Stop` among
  them**, while the top level carries `decision: approve|block` + `reason`,
  and `block` becomes a message appended to the conversation (which is what
  "prevents stopping" mechanically IS). So the nested shape at turn end
  matches no member, fails the parse, and drops the block with no error —
  the "a wrong Stop block shape ships silently" hazard, made concrete rather
  than assumed. Shipped: `"Stop": { "effects": ["decide"] }` (decide and only
  decide — which is what makes the block non-escalating, since the vehicle
  rule prefers inject wherever inject exists) plus
  `ClaudeHookJsonAdapter.DecidesAtTopLevel`, covering `SubagentStop` too as
  the same contract reachable through the capability gate's permissive
  undeclared path, and degrading `ask` — a word the top-level vocabulary
  lacks and the host THROWS on — to noop on the existing
  never-send-what-it-cannot-represent rule.
  **One real defect found, and the seam could not have worked around it**:
  `Harness.Canon` short-circuited on names with no hyphen, so a single-word
  event stayed lowercase — and the install template writes
  `hook {event-kebab}`, making `stop` precisely what arrives on the live
  wire. It matched no spec declaration, so every capability lookup missed
  into the permissive undeclared path, the digest saw no verbs and would
  have noop'd forever, and an echoed `hookEventName` of `stop` is a name the
  host rejects outright. Caught by the end-to-end pin failing, not by
  reasoning; a single word is now a one-segment kebab under the same rule,
  pinned with the idempotence and empty-string edges. Two tests fell out of
  the data edit and are corrections rather than churn: the ledger theory's
  "effectless reconcile seam" case moved Stop → SessionEnd (still
  `"effects": []`), and a hot-reload E2E that read a pid out of an INJECT on
  Stop moved to PostToolUse — the gate now correctly flattens that inject,
  which is the data edit having teeth. **N3's termination pin lands where it
  has to**: a daemon smoke test drives a spawned digest child through the
  shipped spec, the capability gate, and the real adapter — first Stop
  answers the top-level block carrying the digest as its reason and with no
  `hookSpecificOutput` key at all, second Stop answers the bare `{}` that
  lets the turn end, cursor-advance-on-inject doing it across a process
  boundary on real files. Golden bytes pin the shape, and a companion test
  pins that every OTHER event keeps the nested `permissionDecision` — the
  guard against a later "simplification" that hoists the top-level shape and
  silently breaks the tool gate. Docs: doc/platform.md § The Stop block shape
  (contract + harness version + the re-probe command, since the docs and the
  binary disagree and only one of them parses our stdout), the hook-dispatch
  flow doc's egress prose, ADR-0016 d5 annotated as-built.
  **First skeptic pass run 2026-08-13 (opus)** (against the plan's verify
  column: "a wrong Stop block shape ships silently"). It re-derived the
  contract from the binary WITHOUT taking the implementation's word for it
  and confirmed all four claims — the 17-member union with no Stop, the
  top-level `decision`/`reason` consumption, the rejection of a third
  verdict, the event-name equality check — adding the detail that a nested
  Stop shape is
  not perfectly silent: it yields a verbose-only `hook_non_blocking_error`,
  and the decision is discarded either way. It also proved the tests bite by
  MUTATION: forcing the nested shape fails the golden bytes, the warn pin, and
  the daemon end-to-end; restoring the old `Canon` fails both the unit pin and
  the end-to-end — the regression is caught at the real wire, not only in a
  unit. **No REAL defect; four latent hazards, all now recorded rather than
  discovered later.** One earned a code fix (the override-spec case-miss, in
  d5's note above); two are stated in the ADR (fail-closed-on-Stop is an
  unbounded livelock now that Stop declares `decide` — the harness has NO loop
  cap of its own, so our advance is the only guard; and `Merge`'s
  first-deny-wins can falsify the delivery ledger from outside the digest);
  one is a deploy-boundary cosmetic worth knowing before reading the trail:
  the live JSONL holds **4,494 `"hookEvent":"stop"` lines** from the old
  pass-through, so the same physical event starts logging as `Stop` at the
  cutover and any consumer grouping by event name sees a discontinuity there.
  Nothing keys state on the spelling (cursors key role+session), so it is
  presentation only. Suite 897 → 903 green twice.
  **Independent fable skeptic pass run 2026-08-14** — the plan's verify
  column proper, run once the first pass was noticed to have been opus
  (build-opus / skeptic-fable is the house split, and this slice's hazard is
  exactly the contract-re-derivation class the split exists for). It
  re-derived the contract from the 2.1.119 binary with byte-offset evidence,
  confirmed every load-bearing claim, and re-ran both mutations itself
  (Canon revert → 4 tests fail incl. the daemon e2e; nested-shape force → 3
  fail, the stays-nested companion correctly unaffected). **Verdict: ship —
  no real defect — but it earned its keep three ways.** (1) A new latent
  hazard FIXED in-pass: `SubagentStop` was undeclared, so an Inject there
  passed the permissive gate into the memberless nested shape — the exact
  disease this slice cures, one event over; now declared `["decide"]` like
  Stop, pinned by a gate-flatten test. (2) A pre-existing hazard recorded:
  `replaceOutput` appears NOWHERE in the 245MB binary — the PostToolUse
  union member takes `additionalContext`/`updatedMCPToolOutput` only, and
  the schema's unknown-key strip discards an `Effect.Replace` with no error
  of any kind, quieter than the Stop failure; now its own ⚠ row in
  platform.md § The Stop block shape (the spec still advertises `replace`;
  re-probe on upgrade). (3) The record corrected on two accuracy points,
  fixed across code comments + platform.md + the flow doc + the d5 note:
  the host does not observably THROW on a third verdict — the zod enum
  rejects it at parse into the same `hook_non_blocking_error` path, the
  `Unknown hook decision type` throw being dead code behind the enum and
  even the runner's throws caught — and the dropped Stop decision is not
  zero-signal: a visible "Stop hook error occurred" notification fires, so
  the DECISION is what is silent, not the failure. Traced and stated, not
  engineered around: the host converts agent-scoped Stop registrations to
  SubagentStop while the command string still says `hook stop`, and the CLI
  arg wins over the payload field, so such a firing is gated and logged as
  Stop — routing/telemetry aliasing only, the output shape stays correct
  either way; and an ALL-CAPS override key (`"STOP"`) still misses Canon
  into the permissive path, one step beyond the two documented spellings.
  Suite 903 → 904 green twice.)
  `first-members-dogfood` (2026-08-14; phase 5's last, deliberately so — the
  exactly-once campaign and the Stop pin were green, so the gate opened. Two
  committed MEMBERS, both answering `noop` because a member's value is what
  reaches ANOTHER agent: `starter-mail-observer.sh` (write-only, d5's cheapest
  class, resident on PostToolUse — streams reads and edits to the PEER's role
  and escalates to `urgent` when an edit lands on a path the peer is holding,
  which is the payload only the hub position makes possible: neither agent can
  compute it alone) and `starter-mail-watcher.sh` (on-demand LLM member on Stop
  — a DETERMINISTIC gate, my edits ∩ the peer's reads, decides whether the turn
  is worth waking a model over, so it is cheap when there is nothing to say).
  **The reentrancy guard is PROVEN, not asserted** (the plan's new test
  pattern): a stub `claude` that EXITS NONZERO when `--setting-sources ""` is
  missing from its argv — the shipped watcher passes it and the model's words
  reach the bus, while a guard-stripped copy is refused and degrades to the
  ungarnished handoff, which is what gives the first test its meaning.
  **The slice's real find, caught while designing the swarm test: two agents
  cannot get two roles from REGISTRATION at all.** `handlers.json` is global and
  `--role` is a static string, so both members run in both windows and every
  observer reports the same role — the bus reduced to one agent talking to
  itself. The fix is the ADR's own slogan arriving as a requirement — *swarm
  activation is a dispatch-policy flip* — concretely handler-named rules AND'd
  with a `project` path-prefix, an excluded handler being filtered BEFORE
  fan-out (never asked, never restarted), so the wrong-role member costs nothing
  in the window it does not belong to. The shared-role alternative was rejected
  in-slice: nothing in the digest filters by sender, so a member would receive
  its own traffic back. Now DRIVEN rather than described, and mutation-checked —
  making the observer address its own role fails the sender-hears-nothing
  assertion. Two smaller findings recorded in the report: a `settings.json` hook
  edit takes effect MID-SESSION with no restart (assumed otherwise and planned
  around it; the trail then showed 9 real PostToolUse dispatches from the live
  session), and WSL2 stepped the wall clock **−89,120ms** during the run while
  the engine's monotonic `durMs` stayed correct — invariant 2 re-confirmed with
  the measurement HARNESS as the violator, exactly as in item 19's flake hunt.
  Live on the deployed stack and measured: observer **3.9–5.0ms** per tool call
  inside a 10.4ms dispatch / 14–18ms shim round trip, 100.3ms once for the
  resident cold spawn (the latency doctrine's whole argument), oneshot digest
  159.8ms on a turn edge; write path → chained envelope with genesis `prev`,
  read path → a HOOKLESS peer's `mail send` delivered at a real turn-start seam
  with provenance, exactly-once on the second seam, `mail.deliver` on the ledger
  with renderHash + bytesInjected, cursor on disk. Deliberately NOT dogfooded
  and stated: a second real agent loop (the suite covers two sessions/projects/
  roles with real spawned payloads; the live half used one real agent + a
  synthetic hookless peer) and a Stop-seam digest left ARMED — a blocking
  turn-end seam unattended is not worth a demo when our advance is the only
  livelock guard. **Live tree reverted to exactly as found** (handlers,
  settings, mail store, views all restored/removed; the append-only trail keeps
  the record). Field report:
  doc/dogfood/2026-08-14-first-bus-members.md. 5 tests
  (`MailDogfoodTests.cs`); suite 904 → 909 green twice.)
  `swarm-profile-and-flow-doc` (2026-08-15; phase 6, the close — docs only.
  **`doc/flow/mailbox-bus.md`**: the request-lifecycle diagram from "any process
  can send" through store → cursor → planner → advance-before-emit → the
  recipient's loop, plus why-prose for the two asymmetric halves (writing is
  universal, reading is seam-bound), the seam-class matrix as REGISTRATION data,
  why the cursor is a frontier plus a held exception list rather than a bare
  offset, the delivery-counted TTL, the four accepted deviations each in its
  stated direction, what the chain does and does NOT prove (rotation starts a
  new chain; deleting an archived generation is chain-invisible), the seven
  loud re-anchor reasons, provenance vs the `mail.deliver` ledger's opposite
  direction, and d13's three lifetimes. **The profile-as-policy prose the phase
  is named for says the sharper thing the dogfood found**: per-project handler
  rules are not merely how a swarm is *activated* — they are the only thing
  that gives two agents on one machine two ROLES, since `handlers.json` is
  global and `--role` is static, and a shared role would hand every member its
  own traffic back (nothing in the digest filters by sender). ADR-0016's
  prospective Ground truth back-filled decision→code with the as-built
  departures named at d4 (frontier+held), d8 (scoping IS role identity), d10
  (`sessionId` as a first-class column, not nested), and d11 (rotation restarts
  the chain). Both tables verified MECHANICALLY on the item-19 precedent — a
  throwaway checker parsing every Ground truth row and asserting each named
  file exists and each backticked symbol appears in that row's files: 21 file
  refs + 58 symbols/events in the flow doc, 12 + 44 in the ADR, all clear after
  it caught three real gaps (the d5 row naming no `MailDigest.cs`, two
  repo-relative paths that resolved nowhere) plus the substring-matching weak
  spots hand-checked. Every trail event in the table was checked against an
  actual emit site in `Mail/`. Writing the lifetimes table against the real
  tree also caught **a d13 claim the code does not implement**: the ADR says
  all three stores are 0600, and the cursors and mail store are — but neither
  trail emitter sets `UnixCreateMode`, so the trail lands at the process umask
  (`0644` live). Recorded in both the flow doc and a d13 as-built note rather
  than quietly repeated, and deliberately NOT fixed in a docs slice: closing it
  touches both byte-identical-pinned emitters plus existing files, so it is a
  code change and is left as one. **Item 20 complete: all 11 slices, all 6
  phases.**)
  **Item complete 2026-08-15** — the daemon is a store-and-forward bus for N
  agent loops, built with ZERO core: two CLI verbs (`mail send` / `mail
  digest`), payloads, and data. Mail is durable and hash-chained, positions are
  per-(role, session) frontiers whose advance-before-emit is simultaneously the
  at-most-once guarantee and the Stop-loop guard, delivery happens only at seams
  the recipient's harness declares, and every delivery lands on the ledger with
  the hash of the bytes actually shown. Swarm activation is a dispatch-policy
  flip, exactly as designed — and that same mechanism turned out to be what
  gives two agents two identities at all. First members shipped and dogfooded
  live (`starter-mail-observer.sh`, `starter-mail-watcher.sh`), the reentrancy
  guard proven by a stub that refuses. Final tally: 184 mail test methods across
  7 files, suite 909 green twice; ADR-0011's script-write trigger still UNFIRED,
  ask/reply correlation still deferred with `inReplyTo` reserved.
  NOT deployed: the live `~/.captainHook/bin` runs the phase-5 build, so
  `/deploy` is the next bus-facing action whenever the maintainer wants the
  members on their own daemon.
  **Follow-on: `trail-owner-only` (2026-08-15)** — the one gap the capstone's
  mechanical pass turned up, closed the same day. d13 says all three stores are
  0600; the cursors and mail store always were, but neither trail emitter set a
  create mode, so the trail inherited the process umask — `0644` on the live
  install, in `logs/` at `0755`. It earns the mode on its CONTENTS rather than
  its name: `exec.stderr` captures payload stderr verbatim, so a trail holds
  whatever an arbitrary user process wrote, and `api.json` (bearer token), the
  mail store, the cursors, and the rendezvous files were all already locked —
  the trail was simply the one that missed the rule. Fixed at the ONLY place it
  can be fixed: neither `PosixTrail` can pass a mode to `open(2)` (the
  two-argument form is deliberate — Apple arm64 passes a variadic tail
  differently), so each funnels absent-file creation through one BCL call, and
  that call now carries `UnixCreateMode` 0600 with its caller making `logs/`
  0700. **The DIRECTORY mode is the load-bearing half** — it is the only thing
  covering files the engine never creates (a payload's own `session-pulse.jsonl`
  is shell `printf >>` at that payload's umask; no engine change reaches it),
  and that boundary is pinned rather than assumed. Mirrored in both leaves
  per the aot-boundary rule-5 duplication (the F# lib may not reference the wire
  lib), which is exactly why the pin is per-emitter and asserted EQUAL: whichever
  reaches a fresh trail first decides the mode, so a fix to one is only half a
  fix — **mutation-proven**, reverting the F# side alone lands 0644 and fails the
  daemon-side case while the shim case still passes. Create-mode applies on
  CREATE only and `CreateDirectory` no-ops on an existing dir, so nothing
  retightens: a pre-fix tree is DISCARDED at deploy (`/deploy` § 1c, guarded to
  fire only on the loose modes) rather than chmod'ed under a user who may have
  widened it deliberately — cheap, since the trail's lifetime class is
  days-to-weeks and the archival store is `mail/`, untouched. The cost is stated
  in the deploy step: the JSONL history goes, payload-written logs beside it
  included. Wire lib's warning-free bar kept (the two CA1416s suppressed
  LOCALLY, with the reason — the type is libc P/Invoke throughout, so Windows,
  already out of scope per ADR-0012, has never had a working trail). 3 tests
  (`TrailAppendTests`); no ADR — this implements d13 rather than deciding
  anything, and d13's as-built note now records it closed. Suite 909 → 912 green
  twice.
- ~~**7. Desktop shell**~~ — **dropped 2026-07-19** (owner decision): staying
  browser-only. The localhost web GUI is first-class on WSL2 and answers the
  need; no Photino/Tauri wrapper. (This *is* the "staying browser-only" arm
  the planned ADR would have recorded — decided here instead.)
- ~~**8. TUI**~~ — **dropped 2026-07-20** (owner decision): staying
  browser-only for the product's face. The web GUI is first-class on WSL2 and
  answers the admin/observability need; a terminal UI earns nothing the
  browser doesn't already give, and the feedback pyramid never needed it (API
  assertions + Playwright over the web UI carry the agent-dev loop). No TUI.
- [x] **9. Exec handlers — user processes as payloads** *(2026-07-12 reframe;
  was "Real handlers" — retriever/memory as framework code)*. The payload
  surface is the user's own process in any language: one coded `ExecHandler`
  adapts a configured command into the existing dispatch — normalized event
  envelope in on stdin, one strict-parsed Effect out on stdout, supervised
  like any worker. Two modes: `oneshot` (spawn per dispatch; session-edge
  events) and `resident` (daemon-held warm child over lock-step JSONL —
  the pharos-mcp kept-warm pattern; eager spawn at registration, ready-line
  handshake; effectively mandatory for before-tools events, where a cold
  interpreter re-imposes the per-tool-call tax item 12 killed).
  Registration via `~/.captainHook/handlers.json` (strict per-entry parse,
  warn-and-skip entries, stat-gate hot reload, canonicalized events);
  budgets are per-handler and **unbounded by design** — sensible per-event
  defaults, but the user may wait as long as they want (per-handler ask
  windows; the shim's 5s post-delivery clamp is removed — wait until answer
  or EOF; the harness's own timeout recorded as spec data + loud
  registration warn, never auto-edited — ADR-0010 d9);
  child env is a stripped allowlist + explicit `env`/`passEnv` from day one
  (sandboxing itself deferred to item 10's trigger). The original payloads
  survive as proof: retriever + memory land as demo scripts riding the seam;
  the tool-gate stays ADR-0005 (in-process, fail-closed, deferred). New
  engine seam: handler teardown on worker restart/drain (a resident child
  must die with its generation). Design: **ADR-0010** (supersedes the
  item-15 capability-API framing and dissolves item 11's payload half).
  Build order: ADR-0010 § Implementation plan (2026-07-12; 14 slices → 8
  phases; critical path child-wire-contract → exec-handler-adapter →
  handlers-json-registry → child-env-allowlist → resident-child-runtime →
  kill-discipline-teardown → handlers-hot-reload; adversarial verify on 9
  slices; ultracode on exactly one — resident-child-runtime). Tick slices
  here as they land.
  Slices landed: `child-wire-contract` (2026-07-12; `Core/ExecWire.cs` —
  the envelope encoder (deterministic field order, omit-null, payload
  re-written compact, single-line guarantee pinned for resident line
  framing) + the strict answer parser (closed four-shape grammar,
  collect-every-violation, duplicate/unknown/trailing/second-object all
  MALFORMED per the never-guess house rules, `Empty` as its own exit-code-
  blind case, one leading BOM stripped); 38 golden/strictness tests; no
  runtime path reaches the codec until the adapter lands — the golden suite
  IS the slice's verification per the plan).
  `shim-timeout-removal` (2026-07-12; decision 9's shim layer — the 5s
  post-delivery `ResponseTimeout` deleted at the type level (no parameter
  left to configure): past the at-most-once boundary the shim waits for the
  answer or EOF with no timer of its own; the wedged-daemon test's premise
  replaced by a TCS-gated late-answer pin (delivered + observed-still-
  waiting → released → relayed), the EOF fail-safes unchanged and still
  pinned; flow docs rewritten to the harness-backstop doctrine
  (hook-dispatch, live-deployment — including a stale "until doctor lands");
  adversarially verified by a wall-clock drive: a real 6.0s-late answer
  relayed by the IL ShimClient where the old timer abandoned at 5s. Deployed
  shim behavior changes only at the next /deploy — both artifacts swap
  together, so no skew window. Suite 457 green twice. **Phase 1 complete.**
  Deployed 2026-07-12 — live hooks ride the no-post-delivery-timer shim.)
  `per-handler-budget-windows` (2026-07-12; decision 9's dispatcher layer —
  `HandlerSpec.Budget` + `Registry.On` budget overloads; each `RunGuarded`
  gets its OWN CTS/deadline/ask-window sized to its handler's effective
  budget (override may EXCEED the default — a min-clamp under the old shared
  token could only shorten), grace scales with the effective budget
  (explicit ctor grace still wins, the classification-test seam);
  `HandlerContext` gains optional `DispatchId` (the adapter's envelope
  correlation); trail truth: `dispatch.start` keeps the default budgetMs,
  overridden handlers carry their own on `handler.*` lines. **Adversarial
  verify (skeptic) earned its keep**: caught the int-ms ask-window OVERFLOW —
  a ≥24.8-day budget (d9 blesses "no upper bound") went negative and faulted
  every dispatch with a misleading trail; fixed by registration-time
  `CheckBudget` (positive, ≤ ceiling, loud at construction) — plus three
  stale dispatch-wide-budget claims in hook-dispatch.md (fixed same commit).
  Skeptic-confirmed clean: CTS-dispose lifetime not worsened, classification
  travel headroom ≥98ms on every path, no overload mis-binding, no test
  depending on the old wall-time bound. Named carry-in → kill-discipline
  slice (N6): a budget > the daemon's 10s drain deadline can now cut a
  live dispatch at cutover — the drain-vs-long-dispatch decision the ADR
  already defers; consider a loud construction-time warn there. 6 tests.)
  `exec-handler-adapter` (2026-07-12; decisions 1/2/5/6's oneshot core —
  `Handlers/ExecHandler.cs`, the ONE coded handler that makes the closed set
  extensible in user space: spawn per dispatch, envelope out on stdin (then
  EOF), answer = first non-empty stdout line strict-parsed by ExecWire,
  reply-then-linger (effect counts when parsed; an async reaper owns the
  afterlife — drains pipes so a chatty lingerer can't wedge, records
  exec.exit), exit-0-empty ⇒ Noop, nonzero-before-answer / protocol garbage
  ⇒ fail mode (child killed on protocol error), budget cancel ⇒ best-effort
  tree kill + honored-cancel OCE (setpgid rigor deferred to
  kill-discipline). Env allowlist BAKED into this first spawn site
  (`Clear()` + PATH/HOME/USER/SHELL/LANG/LC_*/TZ/TMPDIR — sequencing risk 2
  closed; config env/passEnv arrive with handlers.json); cwd = event cwd
  else runtime home. Trail: exec.spawn/answered/exit/stderr/kill/
  protocolError. **Adversarial verify (skeptic, reproduced live) earned its
  keep**: grandchildren decouple pipe-EOF from child-exit — `sleep 30 &
  exit 0` (the everyday daemon idiom) rode the GRANDCHILD's lifetime,
  burning the budget and counting the decided-Noop case as a WEDGE toward
  escalation (stderr variant) or skipping the kill entirely (stdout
  variant); fixed by racing exit vs answer with BOUNDED post-exit pipe
  joins (PipeGrace 250ms — buffered answers still parsed, pinned by three
  grandchild tests), plus a torn stderr-tail read on the protocol-error
  path (lock + join-before-read). d2 amendment recorded: strictness binds
  through the answer line, post-answer stdout is linger chatter; linger is
  a daemon-mode pattern (collapsed abandons the reaper, late writers get
  SIGPIPE). 18 tests incl. dispatcher-integration fail-open/fail-closed/
  deny-wins-merge. Suite 481 green twice. **Phase 2 complete.** Not yet
  user-reachable — registration is code-only until phase 3's
  handlers.json.)
  `handlers-json-registry` (2026-07-12; decision 4 — ExecHandler goes
  USER-REACHABLE. `Core/ExecHandlersFile.cs`: strict per-entry parser +
  `PolicyResolution`-shaped tri-state (Absent ⇒ zero exec handlers, silent;
  whole-file malformed ⇒ zero + loud `handlers.malformed`, directory/
  unreadable/empty all Malformed-never-Absent; Loaded ⇒ valid entries
  register, invalid entries warn-and-skip with every violation collected —
  the deliberate FILE-strict/ENTRY-lenient split of d4, N2's tension);
  entry fields name/command/args/events/mode/failMode/budgetMs/
  readinessTimeoutMs/env/passEnv/cwd, events canonicalized at parse,
  duplicate names and post-canon duplicate events rejected, budget bounds
  mirror `Registry.CheckBudget` as DATA validation (a bad value skips the
  entry, never throws — probed exactly at the boundary). Registration via
  one shared `HookRun.RegisterExecHandlers` (factory overload — restart ⇒
  fresh handler, the resident-slice shape); `handlersPath` threaded
  policyPath-style through CollapsedAsync + DaemonHost.RunAsync, all three
  Program.cs entry points feed `ExecHandlersFile.ResolvePath()`
  (`CAPTAINHOOK_HANDLERS_FILE` override; null = zero exec = test-safe);
  resident entries parse valid but skip loudly until phase 5;
  `handlers.slowShape` warns oneshot-on-PreToolUse; collapsed re-reads per
  dispatch, daemon reads once at warm-up (hot reload = phase 7).
  **Adversarial verify (skeptic, probed live) earned its keep — the exact
  predicted bug class**: camelCase / upper-kebab / all-lower event
  spellings parsed valid and registered workers that NEVER fired (silent
  dead handlers visible as live in /handlers) — `Canon` only rewrites
  kebab and the runner map was case-sensitive; fixed structurally by a
  case-INSENSITIVE runner map (casing can never split the event space —
  DispatchPolicy's case-insensitive match, registration-side), pinned by a
  parser-routed three-spelling firing test. Also fixed: loudness asymmetry
  — parse-valid env/passEnv/cwd/readinessTimeoutMs were silently inert on
  registered oneshot entries while resident skipped loudly ⇒ new
  `handlers.fieldIgnored` warn until phases 4/5 wire them; CLAUDE.md env
  list gains CAPTAINHOOK_HANDLERS_FILE. Known holes recorded, not fixed:
  slowShape keys on the literal PreToolUse (a user harness's other
  decide-capable events draw no warn — needs spec data, revisit with the
  harness-timeout-hint slice); unknown-event typos register silently dead
  (vocabulary warn deferred; phase 8's expected-vs-registered view is the
  backstop). 46 tests incl. the kebab-file-fires-on-canonical-dispatch
  full loop and a collapsed E2E (child inject in the real hook answer).
  Suite 527 green twice. **Phase 3 complete — user payloads are live on
  the collapsed and daemon paths.**)
  `child-env-allowlist` + `harness-timeout-hint` (2026-07-13, the phase-4
  config batch. Env: ExecHandler consumes the entry's `env`/`passEnv`/`cwd`
  — precedence fixed-allowlist ∪ passEnv-names-from-parent, literal env{}
  applied LAST (explicit beats inherited), a passEnv name the parent lacks
  silently skips; cwd rungs config → event → runtime home, each only if it
  EXISTS (bad cwd never fails a spawn; the resolved choice + any fallback
  ride exec.spawn's data); `handlers.fieldIgnored` shrinks to
  readinessTimeoutMs. Hint: `HarnessSpec.HookTimeoutHint`
  (`hookTimeoutHintMs`, optional, strict-validated; embedded claude-code
  declares 60000 — Claude Code's hook-command default), threaded from the
  DEFAULT spec at both registration sites; a budget past it draws
  `handlers.budgetBeyondHarness` — loud, informational, never auto-synced
  (d9). Verified by the plan's decisive drive, one real collapsed run:
  secret=ABSENT (canary stripped), passed=crossed (named passthrough),
  literal=from-config (explicit add), the warn firing with the entry named,
  and phase 2's per-handler budgetMs visible on handler.ok. 13 new tests
  (canary-absence for passEnv, literal-beats-inherited, cwd precedence +
  bad-cwd fallback, hint parse rules, embedded-spec pin, warn/no-warn).
  Suite 535 green twice. **Phase 4 complete.**)
  `kill-discipline-teardown` (2026-07-12, phase 5's first chunk — the seam
  + mechanics + drain; resident-child-runtime is the second chunk. Teardown
  seam: disposal threads through the Dispatcher's C# factory closure
  (worker id → current instance; swap-on-restart disposes the REPLACED
  instance unless same-reference — the instance-registration reuse contract
  — or still shared by another slot), and escalation — where MarkDead never
  re-runs the factory — disposes the LAST instance via a new additive
  `Supervisor.SubscribeEscalated` (the settable OnEscalated stays the
  host's slot; the N3 orphan hole closed). Both paths fire-and-forget so
  the one-fault-at-a-time supervisor mailbox never waits a kill grace.
  Kill mechanics: .NET's CreateNewProcessGroup is Windows-only
  (PlatformNotSupportedException probed), so spawn goes through setsid(1)
  — exec-in-place, pid preserved, pgid == sid == pid, grandchildren die
  with the group — and TERM→2s grace→KILL lands via libc kill(-pid)
  (`ProcessGroup`); TERM-ignorers draw a second exec.kill how=kill line;
  setsid-absent degrades to tree walk, flagged pgroup=false per spawn.
  ExecHandler tracks every live child, implements IAsyncDisposable
  (teardown kills all, refuses new spawns), and budget/protocol kills send
  TERM synchronously then escalate OFF the dispatch path (OCE rethrows
  immediately). N6 DECIDED: cut, loudly — the drain gains an unconditional
  child phase after in-flight + background (Dispatcher.DisposeHandlersAsync,
  own 6s bound; ADR-0010 N6 amendment), daemon.drainChildren +
  daemon.drainCut trail it, handlers.budgetBeyondDrain pre-warns at
  registration, Doctor grace 12s→18s covers the tail. Three-skeptic
  adversarial round confirmed and fixed: leader-keyed liveness (a dead
  leader's surviving group read "gone" — IsAlive now probes the GROUP),
  drain-outruns-detached-SIGKILL (children stay tracked until their kill
  concludes; evicted instances' disposals registered pending and awaited
  by the drain), duplicate-worker-id corruption (ctor re-probes ids;
  Supervisor.Spawn refuses duplicates), a throwing restart factory
  silently killing the supervisor loop (escalates instead), drain-timeout
  background effects silently starting doomed work (intake closed →
  side.dropped), reaper exit-code lost to teardown Dispose (cached),
  setsid probed for the execute bit. Residue documented, not defended:
  collapsed-mode kills are TERM-only (exit can outrun the detached KILL);
  a gracefully-concluded child's daemonized survivors are its own
  (kill paths take the group, graceful exit releases it — ExecHandler
  header). 12 tests (pgid==pid end-to-end, grandchild dies with group,
  SIGKILL escalation, dead-leader group teardown, drain-awaits-kill,
  restart/escalation/singleton/shared disposal, lingerer teardown, the N6
  daemon E2E asserting the cut still ANSWERS). Suite 547 green twice.)
  `resident-child-runtime` (2026-07-13, phase 5's second chunk, the ADR's
  one ULTRACODE slice — the daemon holds children warm. Wire: `ExecWire`
  gains `{"ready":1}` strict readiness (`TryParseReady`) + the optional
  `dispatchId` echo on every answer (mandatory for resident, extracted into
  `ExecAnswer.Ok`). `ResidentExecHandler`: eager spawn AFTER teardown-seam
  admission (new `IEagerStart` — `TrackSwap` returns admitted?+predecessor
  so a post-drain restart never spawns and a successor sequences behind its
  predecessor's kill), the three-way readiness race (ready line vs
  readinessTimeoutMs vs early exit) transitioning `ChildState`
  Spawning→Ready/Failed exactly once (transition-wins-then-kills), lock-step
  JSONL with the mandatory echo as stale-answer backstop (kill-on-timeout is
  the primary defense — every timeout path replaces the child),
  fail-mode-while-warming (uncounted, loud — the dispatch token never races
  the readiness timer), counted-throw-on-Failed → supervised respawn.
  `ChildRecords` writes `~/.captainHook/children/child-<pid>.json` (pid +
  /proc starttime identity, atomic write, once-per-process sweep) — NOT the
  XDG tmpfs, so a SIGKILLed daemon's orphan stays traceable for phase-8
  doctor. Registration: `residentAllowed` gates resident to the daemon;
  collapsed runs skip fail-open loudly and register a DENY stub for
  fail-closed (a declared gate never silently vanishes — the N2 inverse);
  `handlers.readinessBeyondBudget`/`residentFanout` warns; slowShape stays
  silent for resident-on-before-tools (the recommended shape). Two
  adversarial rounds: a pre-build design panel (10 confirmed / 5 refuted —
  echo made mandatory, fail-mode-while-Spawning, Start-after-admission,
  records-in-home, deny-stub) then a 5-lens impl-verify (9 serious all
  confirmed + fixed: expired-in-queue Backlogged guard returning fail-effect,
  tokened envelope write, cancellable predecessor + pre-spawn disposed check,
  gate-resolve on teardown, live-child EPIPE → protocol error). 24 resident
  tests (readiness strict table, eager-spawn-before-dispatch, warm reuse, all
  three race arms, missing/mismatched/double-answer echo, wedge → uncounted
  respawn, FakeClock escalation with all children dead, records sweep,
  IEagerStart sequencing, the no-respawn-after-teardown cut, daemon E2E: two
  warm hooks one child + drain kills + record gone). Suite 580 green twice.
  **Phase 5 complete — user processes run warm, and no child outlives its
  daemon.**)
  `collapsed-mode-degrade` + `demo-payload-scripts` (2026-07-13, the phase-6
  batch — prove the seam, protect the fallback. DEGRADE: a resident entry
  with no daemon runs the SAME ResidentExecHandler in oneshot-lifecycle
  (spawn→serve-one→die), replacing the interim skip/deny-stub. Composed at
  one seam: BuildDefaultRegistry takes a `collapsedEvent` — a collapsed run
  passes its one dispatched event so only that event's exec handlers
  register (an unrelated event's resident child never spawns for a
  single-shot hook; daemon passes null = warm all), and CollapsedAsync
  awaits DisposeHandlersAsync in a `finally` AFTER the stdout answer so N3's
  no-orphan is STRUCTURAL, not best-effort. A fail-closed gate now genuinely
  runs (real verdict or fail-closed deny on any spawn/ready/protocol
  failure) — strictly stronger than the deleted deny stub, never a silent
  grant. `handlers.residentDegraded` marks the daemon-down path.
  DEMOS: examples/payloads/ — retriever.sh (resident, PreToolUse, greps
  notes and injects) + memory.sh (oneshot, Stop, appends a durable log) +
  handlers.json + README, dependency-free POSIX sh outside the build graph.
  Two adversarial skeptics confirmed the degrade SOUND (every fail-closed
  collapsed outcome → deny, verified live; ordering + no-orphan hold);
  5 LOWs fixed (stale docs ×3, teardown bound 6s→8s, construct-inside-try).
  Tests: registration filter/degrade-log, two collapsed no-orphan E2Es
  (fail-open serves + fail-closed runs-for-real), the demo E2E driving the
  REAL committed scripts through the daemon (retriever injects a seeded
  note, memory persists its log, child dies at drain). Suite 583 green
  twice. **Phase 6 complete — the seam is proven with scripts, the fallback
  degrades without orphaning.** Remaining for item 9: phase 7
  (handlers-hot-reload), phase 8 (doctor-orphans, handlers-observability).)
  `handlers-hot-reload` (2026-07-13, phase 7 — the runtime registry-refresh
  seam the Dispatcher had NO precedent for: it only ever spawned workers in
  its ctor. Stat-gate `ReloadingHandlers` (a literal ReloadingPolicy mirror)
  at the top of DispatchOneAsync drives `Dispatcher.Reconcile(fresh)`, which
  diffs the reloadable (exec) workers by a STABLE id (`AssignIds` —
  (event,name), coded a constant id-seed, exec names file-unique) carrying an
  INJECTIVE config fingerprint (`ExecFingerprint`, length-prefixed):
  unchanged ⇒ KEEP (warm child untouched — no churn), gone ⇒ REMOVE
  (Supervisor.Remove + evict-and-kill), changed ⇒ CHANGE (Remove frees the
  id, re-Spawn rides the existing TrackSwap to evict the old instance and
  thread its kill as the successor's predecessor), new ⇒ ADD. `_runners`
  became a volatile swapped-whole snapshot (lock-free dispatch reads, one
  atomic publish under _reconcileLock). Malformed reload ⇒ zero exec ⇒ every
  resident REMOVE'd (no keep-last-good, handlers.malformed loud); the summary
  rides handlers.reload. `Supervisor.Remove` is the one new F# member
  (MarkDead + drop the child-spec so a late exit never restarts a retired
  worker; actor.remove). Reuses phase 5's kill/drain machinery wholesale — no
  bespoke kill path, so N3's leak surface does not grow with the seam.
  THREE-SKEPTIC adversarial round (orphans / diff-correctness / concurrency)
  — 1 HIGH + 3 MED confirmed and FIXED:
  • HIGH (generation aliasing) — a CHANGE's Remove+Spawn minted a fresh
    handle whose generation reset to 1, aliasing the retired worker's gen 1,
    so the reload's kill of the old child (a mid-dispatch EOF crash) was
    charged to the fresh REPLACEMENT: spurious restart → churn, and under
    repetition → escalation to permanent-DEAD (deny-every-hook for a
    fail-closed gate). FIX: fault signals (ChildExit/ChildWedged) now carry a
    supervisor-global monotonic EPOCH (`ActorRef.Epoch`), never the per-handle
    restart count — a reused id can't alias; Generation stays the read-model
    restart count. Regression test: CHANGE mid-conversation, the replacement's
    Generation stays 1.
  • MED (drain-await gap, confirmed ×2) — all three eviction sites removed
    from _liveInstances and registered into _pendingDisposals in SEPARATE lock
    sections; the drain's single-lock snapshot could fall between and await
    neither, dropping an in-flight TERM→grace→KILL (orphan on exit — the very
    thing _pendingDisposals exists to prevent). FIX: `FireDisposeLocked`
    registers the disposal UNDER the same lock as the removal (atomic); only
    the kill's fast synchronous prefix runs there (the handler's own lock,
    never _teardownLock — no reentrancy).
  • MED (restart-vs-remove) — a fault-loop restart already committed to could
    resurrect a REMOVE'd worker as a straggler (cleaned at drain). FIX:
    start() re-checks handle.IsDead before spawning, collapsing the window.
  • MED (fingerprint collision) — a naive \x1f/\0 separator scheme aliased
    args ["a","b"] with ["a\x1fb"] (and env value-with-'='); FIXED pre-report
    by length-prefix framing (injectivity unit-tested).
  LOW residue, documented not defended: an exec name EQUAL to a coded name
  ("echo"/"latency-probe") gets a #2 id (works, but dispatch.json exclusion
  keys on the shared name) — a name CONTAINING '#' is now rejected at parse
  (the sharp reorder crack); the coded id-seed is re-read from
  CAPTAINHOOK_PROBE per reload but is constant within a daemon (a probe flip
  needs a new daemon → new ctor, never a cross-reload diff); a budget or
  readiness edit re-warms the resident child (safe over-churn — those fields
  must stay in the fingerprint to take effect at all). Tests: 15 (fingerprint
  injectivity, no-churn KEEP, add/remove/change, events-list
  churn-only-affected, malformed-kills-all, post-drain refusal, coded-survive,
  stat-gate ×2, Supervisor.Remove, the CHANGE-mid-dispatch misattribution
  guard, the add-then-remove daemon E2E). Suite 598 green twice. **Phase 7
  complete — edit handlers.json, the next hook obeys, and unchanged warm
  children never flinch.** Remaining for item 9: phase 8 (doctor-orphans,
  handlers-observability).)
  `doctor-orphans` + `handlers-observability` (2026-07-13, phase 8 — the
  read-side closers, both report/projection-only, no adversarial verify per
  the plan. DOCTOR-ORPHANS: `Doctor.SweepOrphans` applies the daemon reap's
  SAME double-guard to a second entity class, `~/.captainHook/children/` —
  child pid ALIVE + `/proc` starttime MATCHES + owning daemon gone (dead pid,
  or a live pid that is not a captainHook `--daemon`) ⇒ ORPHAN, reported via
  `doctor.orphan` and the `doctor` verb's stdout. REPORT-ONLY: doctor never
  signals the child (a misclassification is a wrong trail line, not a killed
  process — why the slice needs no verify); stale records (dead pid /
  starttime drift / corrupt) are swept in passing. The pid-reuse guard makes
  the orphan verdict safe — a recycled child pid has a different starttime, a
  recycled daemon pid fails the cmdline check. OBSERVABILITY: child state
  reaches the API through a new one-way Core seam `IResidentObservable` (child
  state lowercased + pid, plain data) that `ResidentExecHandler` implements
  and `Dispatcher.Snapshot` correlates from `_liveInstances` — the F# Worker
  never learns what a `ChildState` is, the arrow holds; `HandlerDto` gains
  `childState`/`childPid` (null for oneshot/coded). Expected-vs-registered:
  `HandlersDto` gains the handlers.json tri-state (`source`
  absent|malformed|loaded + `error`) and an `expected[]` — every declared
  entry JOINED by name to the live Snapshot, a valid entry `registered:true`,
  a warn-and-skip entry `registered:false` + violations and NEVER a live row
  (the N2 caution, structural). The GUI `SupervisionPanel` grows a child
  column + a handlers.json section (tri-state mirroring `PolicyPanel`);
  DTO→schema→TS regen + committed `ui/` rebuild rode the `dto-schema-codegen`
  chain, e2e-asserted. Tests: `DoctorOrphansTests` (7 — alive-orphan reported
  + record kept as evidence, healthy-owned ignored, non-daemon-owner via the
  default guard, dead/pid-reuse/corrupt swept), `ApiReadEndpointsTests`
  (child-state null on coded, expected-vs-registered join, skipped-not-live),
  a resident child-state `Snapshot` test, the `SupervisionPanel` Playwright
  e2e. Suite 607 green twice. **Phase 8 complete — item 9 DONE: the exec-
  handler seam is built, proven, orphan-safe, live-reconfigurable, and
  observable end to end.**)
- [ ] **15. Handler capability policy (egress)** — layer 3 of the native
  policy story: what may a running handler *reach*. *(Narrowed by ADR-0010,
  2026-07-12: payloads are user processes, so there is no in-process
  capability API to design — the process boundary is the isolation seam.)*
  What remains: the env-allowlist policy ships WITH item 9 (ADR-0010 d5);
  sandboxing (namespaces, seccomp, cgroup caps, network deny) is the
  enforcement half, deferred to item 10's trust-model trigger with ADR-0004
  N2's process isolation as the backstop. The old `HandlerContext`-hands-out-
  capabilities principle survives only for first-party *in-process* handlers,
  if an egress-bearing one ever exists.
- [x] **10. Hook trust model** — installing a hook = installing arbitrary code
  that runs on every prompt. The install UX must show exactly what will
  execute, from where, before touching settings. **Rides WITH items 5–6**
  (the install operations and install UX are its only real surface), not a
  phase of its own. *(Made concrete by ADR-0010: installing = a command line
  + `handlers.json` entry the GUI shows verbatim; this item's trigger also
  gates the sandboxing half of item 15.)*
  Design recorded in **ADR-0011** (accepted 2026-07-19): the same-user
  trust boundary ratified (installer = the machine's own user installing
  things they can read; resilience not prevention for the daemon); consent
  as the v1 threat model (verbatim-and-resolved confirm before any write);
  one new write verb `PUT /api/v1/handlers` on the `PUT /policy` pattern,
  refusing warn-and-skip entries at the write (hand edits stay
  entry-lenient); enable/disable composed from shipped dispatch.json rules;
  **no settings.json auto-edit** (wiring hint shown-not-written, auto-wiring
  waits for observed friction); write-authz ratified as the existing bearer
  token; sandboxing + provenance explicitly un-triggered until code the
  user cannot read arrives (that surface fires a new ADR first).
  Build order: ADR-0011 § Implementation plan (2026-07-19; 10 slices → 5
  phases; critical path handlers-put-endpoint → handlers-etag-ifmatch →
  gui-handlers-editor → gui-verbatim-confirm → docs-update; adversarial
  verify on exactly three slices — the write endpoint, its test pins, and
  the editor's 412 re-merge; no ultracode). Tick slices here as they land.
  Slices landed: `handlers-put-endpoint` + `handlers-install-strictness` +
  `handlers-etag-ifmatch` + `handlers-write-adversarial-tests` +
  `install-template-fix` (2026-07-19, the server seam as one batch —
  `ApiHandlersWriter` on ApiPolicyWriter's exact pattern (strict-parse with
  the daemon's own ExecHandlersFile, RFC 7232 If-Match after validation,
  sibling-temp same-dir rename, BOM parity) with its own closed
  `HandlersWriteOutcome` DU; d3's tightening pinned by CONTRAST — the same
  bytes the write path 422s (any warn-and-skip entry, violations labeled
  per entry) load entry-lenient via Resolve, so the split can't silently
  converge; `GET /handlers` gains raw/etag (HandlersDto + header) and
  `StatusDto` gains the resolved `shimPath` for the wiring hint (decided
  pre-pin per the plan's sequencing note); DaemonHost wires the writer from
  the same handlersPath the read model and ReloadingHandlers share; the
  stale `{dotnet} {captainHookDll}` install template → `{captainShim} hook
  {event-kebab}` with a spec-pin test (N4). 17 tests incl. the
  resolver-interleave atomicity probe (400 writes against the REAL
  ExecHandlersFile.Resolve — never a non-Loaded flash) and the
  both-directions daemon E2E (PUT-install → the NEXT dispatch runs the
  child; PUT-uninstall with If-Match → gone), the plan's two named
  vacuous-pass traps. Adversarially verified (skeptic, 21-case
  writer-vs-loader matrix + ETag round-trip + If-Match grammar + stamp
  collision probes): NOTHING above LOW survived; three LOW
  inherited-by-design edges (in-string UTF-8 refusal direction, 1 MiB cap,
  symlink flattening) recorded in the flow doc, not defended. Suite 624
  green twice.)
  `gui-handlers-editor` + `gui-verbatim-confirm` + `gui-enable-disable` +
  `gui-wiring-hint` (2026-07-19, the GUI batch — phase 8's read-only
  handlers.json section grown into the install surface (`HandlersSection`):
  install/edit/uninstall as whole-file read-modify-write over GET+ETag /
  PUT+If-Match, every write behind the d2 verbatim-and-resolved confirm
  (exact command/args/events/mode/fail/budget/cwd/env/passEnv + the entry
  JSON; "resolved" = the GUI mandates absolute command paths, hand edits
  stay free); a valid-but-unreconciled entry renders "pending (live on the
  next hook)" — honest to the stat-gate, distinct from skipped; the d4
  toggle composes a PREPENDED unconditional handler-deny through the
  existing PUT /policy (enable removes only toggle-shaped denies — off→on
  is IDENTITY on hand policy); the d5 wiring hint renders the install
  template per event ({captainShim} → /status shimPath), shown never
  written. **Adversarial verify (the plan's third mandated pass) —
  the 412 INVERSION HELD under attack** (no lost-update sequence
  constructible: conflict verdict structurally tagless, etag/raw travel as
  one consistent pair, render-time closures can't pair stale raw with fresh
  etag); five ancillary findings all fixed + pinned: render-crash on
  daemon-tolerated config shapes (rules:[null], handlers:[null] — defensive
  parse), badge-vs-daemon divergence (first-match now includes scoped rules
  ⇒ "scoped", never a wrong-direction "disabled"), malformed-policy badge
  (— not on/off), destructive toggle round-trip (hand-written allow now
  kept inert behind the prepended deny), and the two-read tear gate
  (composable requires raw to parse — a GUI write never drops entries it
  couldn't render). e2e: fixtures gain an isolated CAPTAINHOOK_HANDLERS_FILE
  (a latent read-only gap made load-bearing by the write verb) + `fireHook`
  (the engine's shim mode inside the sandbox); 4 Playwright tests incl. the
  full loop (install → confirm → real hook runs the payload → registered)
  and 412-stops-nothing-clobbered. 51 web unit tests; 14 e2e; committed
  ui/ rebuilt.)
  `docs-update` (2026-07-19, the capstone — terminal by construction:
  management-api.md § The writes (both writers, the d3 strictness split,
  the three accepted LOW edges), management-gui.md § The handlers editor
  (the 412 inversion, the toggle compose, the wiring hint), ADR-0011's
  Ground truth back-filled as a decision→code index. **Item 10 complete —
  ADR-0011 fully landed: all 10 slices, 5 phases; installing a payload is
  a consented, verbatim-shown, atomically-written, hot-reloaded act on
  both API and GUI, and the trust boundary is a written document.**)
- ~~**11. N-runtime harness**~~ — **dropped 2026-07-19** (owner decision):
  staying .NET-only for the core. ADR-0010 already made payloads N-language
  by construction — user processes in any language — which satisfies the
  polyglot story without porting the shim/daemon/dispatch core. DESIGN.md's
  comparison thesis is retired as a build goal.

## Parking lot

- **Mobile** — a responsive browser UI over LAN already answers the likely
  need; no app until a real use case demands one.
- **Community registry** — discovery/versioning for shared hooks & skills.
  (Plausible under ADR-0010: a shared hook is a script + manifest, not a
  compiled plugin.)
- **shipshape as a Stop-hook** — the repo verifying itself with the very
  mechanism it demonstrates.
- **Packaging** — single-file publish for the JIT engine (the shim's
  Native AOT half promoted to item 12).

## GUI direction (updated 2026-07-19: browser-only decided)

**One API; the browser UI is THE face** (item 6, shipped). The desktop-shell
face (Photino) was dropped with item 7 — localhost-in-browser is first-class
from WSL2 and already does the job with less machinery. The TUI (item 8)
remains a possible second face against the same API, for product reasons
only — the feedback pyramid stays API assertions → Playwright.

The structured log stream (JSONL + correlation ids) is the GUI's live data
feed — the observability layer was built GUI-ready before the GUI existed.
