// mail-live-choreography (ADR-0016 d14, roadmap item 21 slice 5) — the driver
// that joins the snapshot to the stream, tested with no daemon, no network and
// no clock.
//
// The pins that matter are all about the JOIN, because that is where a live
// picture goes silently wrong: the stream must open at the snapshot's own
// stamp and nowhere else; an absent stamp must not become the token "0"; a
// resync must re-anchor at the NEW stamp rather than the stale one; and the
// reducer, not the driver, decides when the picture is untrustworthy.
import { test } from "node:test";
import assert from "node:assert/strict";
import { runMailStream, type MailStreamPorts } from "./mailStream.ts";
import type { MailStreamState, SseFrame } from "./store.ts";
import type { MailDto } from "./api.gen.ts";

const enc = new TextEncoder();

function sse(body: string, status = 200): Response {
  return new Response(
    new ReadableStream<Uint8Array>({ start(c) { c.enqueue(enc.encode(body)); c.close(); } }),
    { status, headers: { "Content-Type": "text/event-stream" } },
  );
}

function snapshot(trailEventId: string | null, extra: Partial<MailDto> = {}): MailDto {
  return {
    dir: "<mail>",
    chain: { ok: true, head: null, gen: 1, lines: 0, bytes: 0, dirMode: "700", fileMode: "600", faults: [] },
    since: 0, sinceAligned: true, frontier: 0, trailEventId,
    lines: [], cursors: [], presence: [],
    ...extra,
  } as MailDto;
}

type Call = { path: string; lastEventId: string | null };

/** Drive the loop over scripted answers. `snapshots` are served to /mail in
 * order (the last repeats); `streams` to /events likewise. `resyncAt` fires the
 * reducer's request after the Nth stream attempt opens. */
async function drive(o: {
  snapshots: (() => Response)[];
  streams: (() => Response)[];
  resyncAfterStreams?: number;
  stopAfterStreams?: number;
}) {
  const calls: Call[] = [];
  const seeded: MailDto[] = [];
  const frames: SseFrame[] = [];
  const states: MailStreamState[] = [];
  const ctrl = new AbortController();
  let askResync: (() => void) | null = null;
  let snaps = 0, streams = 0;

  const ports: MailStreamPorts = {
    fetchFn: (path, init) => {
      const lastEventId = new Headers(init?.headers).get("Last-Event-ID");
      calls.push({ path, lastEventId });
      if (ctrl.signal.aborted) return Promise.reject(new Error("aborted"));
      if (path.startsWith("/api/v1/mail")) {
        const make = o.snapshots[Math.min(snaps++, o.snapshots.length - 1)];
        return Promise.resolve(make());
      }
      streams++;
      if (o.stopAfterStreams !== undefined && streams >= o.stopAfterStreams) {
        ctrl.abort();
        return Promise.resolve(sse(""));   // the run is over; deliver nothing more
      }
      if (o.resyncAfterStreams !== undefined && streams >= o.resyncAfterStreams) {
        // The reducer's verdict lands while the stream is open — exactly how it
        // arrives in life: a frame folds, the reducer distrusts the picture.
        queueMicrotask(() => askResync?.());
      }
      const make = o.streams[Math.min(streams - 1, o.streams.length - 1)];
      return Promise.resolve(make());
    },
    seed: (dto) => seeded.push(dto),
    fold: (f) => frames.push(f),
    setState: (s) => states.push(s),
    onResnapshotRequest: (cb) => { askResync = cb; return () => { askResync = null; }; },
    sleep: () => (ctrl.signal.aborted ? Promise.reject(new Error("aborted")) : Promise.resolve()),
    signal: ctrl.signal,
  };
  const result = await runMailStream(ports);
  return { calls, seeded, frames, states, result };
}

const mailCalls = (c: Call[]) => c.filter((x) => x.path.startsWith("/api/v1/mail"));
const streamCalls = (c: Call[]) => c.filter((x) => x.path.startsWith("/api/v1/events"));

// ---- the join --------------------------------------------------------------

test("the stream opens at the snapshot's own stamp, echoed verbatim", async () => {
  const { calls, seeded } = await drive({
    snapshots: [() => Response.json(snapshot("4096"))],
    streams: [() => sse("id: 5000\ndata: {}\n\n")],
    stopAfterStreams: 2,
  });
  assert.equal(seeded.length, 1);
  // The one fact the whole slice rests on: not "from now", not 0 — the
  // position the snapshot was taken at, as a token nobody interpreted.
  assert.equal(streamCalls(calls)[0].lastEventId, "4096");
});

test("the snapshot is seeded BEFORE the stream is opened", async () => {
  // Order, not timing: a frame folded into the previous picture would be
  // applied to a ledger it was never about.
  const order: string[] = [];
  const ctrl = new AbortController();
  let streams = 0;
  await runMailStream({
    fetchFn: (path) => {
      if (path.startsWith("/api/v1/mail")) { order.push("fetch-snapshot"); return Promise.resolve(Response.json(snapshot("7"))); }
      order.push("open-stream");
      if (++streams >= 2) ctrl.abort();
      return Promise.resolve(sse(""));
    },
    seed: () => order.push("seed"),
    fold: () => {},
    setState: () => {},
    onResnapshotRequest: () => () => {},
    sleep: () => (ctrl.signal.aborted ? Promise.reject(new Error("x")) : Promise.resolve()),
    signal: ctrl.signal,
  });
  assert.deepEqual(order.slice(0, 3), ["fetch-snapshot", "seed", "open-stream"]);
});

test("the token \"0\" is sent as \"0\" — it is a position, not an absence", async () => {
  // A trail that exists but is empty stamps "0", which legitimately means
  // "from the earliest still-reachable point". A driver that treated it as
  // falsy would silently open live-from-now instead and lose the window.
  const { calls } = await drive({
    snapshots: [() => Response.json(snapshot("0"))],
    streams: [() => sse("")],
    stopAfterStreams: 2,
  });
  assert.equal(streamCalls(calls)[0].lastEventId, "0");
});

test("no trail served ⇒ snapshot-only: seeded, honest, and NO stream opened", async () => {
  // The one case where there is nothing to align to. Opening "from now" here
  // would be a picture that claims to be live and is not; resuming at "0"
  // would replay a trail that is not being served. Neither: say so.
  const { calls, seeded, states, result } = await drive({
    snapshots: [() => Response.json(snapshot(null))],
    streams: [() => sse("")],
  });
  assert.equal(result, "snapshotOnly");
  assert.equal(seeded.length, 1);
  assert.equal(streamCalls(calls).length, 0);
  assert.ok(states.includes("snapshotOnly"));
});

// ---- resync ----------------------------------------------------------------

test("a resnapshot request re-seeds and re-anchors at the NEW stamp", async () => {
  const { calls, seeded, states } = await drive({
    snapshots: [() => Response.json(snapshot("100")), () => Response.json(snapshot("900"))],
    streams: [() => sse("id: 500\ndata: {}\n\n")],
    resyncAfterStreams: 1,
    stopAfterStreams: 3,
  });
  assert.equal(seeded.length >= 2, true);
  const opens = streamCalls(calls).map((c) => c.lastEventId);
  assert.equal(opens[0], "100");
  // The re-anchor is the point: resuming at the OLD stamp would replay
  // everything the fresh snapshot already contains, and the reducer would be
  // asked to survive exactly the overlap this design exists to remove.
  assert.equal(opens[1], "900");
  assert.ok(states.includes("resyncing"));
});

test("the driver never decides the picture is stale — only the reducer does", async () => {
  // No resync is requested, so a stream that simply ENDS must not silently
  // start a second one behind the operator's back: the picture is still
  // trustworthy, and re-seeding would be work nobody asked for. (A transport
  // drop is the stream's own business — runEventStream reconnects internally
  // and the driver never sees it.)
  const { calls, seeded } = await drive({
    snapshots: [() => Response.json(snapshot("1"))],
    streams: [() => sse("")],
    stopAfterStreams: 2,
  });
  assert.equal(seeded.length, 1);
  assert.equal(mailCalls(calls).length, 1);
});

// ---- the credential --------------------------------------------------------

test("401 on the snapshot is a dead credential: no stream, no retry", async () => {
  const { calls, states, result } = await drive({
    snapshots: [() => new Response("", { status: 401 })],
    streams: [() => sse("")],
  });
  assert.equal(result, "dead");
  assert.equal(streamCalls(calls).length, 0);
  assert.ok(states.includes("dead"));
});

test("a 500 on the snapshot is a transient — it retries rather than dying", async () => {
  const { seeded, calls, states } = await drive({
    snapshots: [() => new Response("", { status: 500 }), () => Response.json(snapshot("42"))],
    streams: [() => sse("")],
    stopAfterStreams: 2,
  });
  assert.equal(mailCalls(calls).length, 2);
  assert.equal(seeded.length, 1);
  assert.equal(streamCalls(calls)[0].lastEventId, "42");
  assert.ok(!states.includes("dead"));
});

// ---- frames ----------------------------------------------------------------

test("every stream frame reaches the fold, gaps and resets included", async () => {
  const { frames } = await drive({
    snapshots: [() => Response.json(snapshot("0"))],
    streams: [() => sse('id: 1\ndata: {"evt":"mail.append"}\n\nevent: gap\ndata: {"dropped":3}\n\nevent: reset\nid: 0\ndata: {}\n\n')],
    stopAfterStreams: 2,
  });
  assert.deepEqual(frames.map((f) => f.kind), ["line", "gap", "reset"]);
  // A gap and a reset are not swallowed here: they reach the reducer, which is
  // what turns them into a resnapshot request and then into a resync.
  assert.deepEqual(frames[1], { kind: "gap", dropped: 3 });
});
