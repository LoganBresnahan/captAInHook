import { runEventStream } from "./sse.ts";
import { useStore, type MailStreamState, type SseFrame } from "./store.ts";
import { apiFetch, clearToken } from "./auth.ts";
import type { MailDto } from "./api.gen.ts";

// mail-live-choreography (ADR-0016 d14, roadmap item 21 slice 5) — the bus stops
// being a photograph taken every four seconds and becomes the thing itself,
// moving. What changes is not the picture's CONTENT but where it comes from:
// the canvas already draws `mail.ts`'s reduced state, and this module is the
// only thing that puts trail events into it.
//
// THE ORDER IS THE DESIGN. A live view needs both an authoritative snapshot and
// the `mail.*` stream, and whatever falls between them is either lost or
// duplicated. Losing is fatal and silent — a missed `mail.cursorAdvance` leaves
// an envelope drawn pending forever with nothing flagged — so the daemon stamps
// each snapshot with the trail position it was taken at (`trailEventId`) and
// this driver opens the stream exactly there. The id is the byte offset after a
// line, so the resume begins precisely where the snapshot's knowledge ends:
// zero loss, zero duplicate, and the reducer's replay rules go back to covering
// what they were written for.
//
// WHY A SECOND SUBSCRIPTION rather than a filter over the trace's stream, which
// is already open and already carries every one of these lines. Two reasons,
// either sufficient. The resume id is an OPAQUE token (ADR-0009 d2) — a client
// stores and echoes it and never interprets it, precisely so segmentation can
// redefine it later — so "have I already got this frame in my snapshot?" cannot
// be answered by comparing a frame's id against the stamp; it can only be
// answered by opening AT the stamp and letting the server answer it. And the
// trace's buffer is display-capped at TRACE_CAP: dropping the oldest line is
// exactly right for a log and a silent corruption for a reduced picture, which
// has no way to know what it was never shown.
//
// RESYNC is a first-class state, not an error path. The reducer refuses to
// guess — a gap, a reset, a store-side re-anchor, an advance whose sequence
// number skipped — and says so by raising `resnapshot`. This driver watches for
// exactly that, tears the stream down, takes a fresh snapshot, and re-anchors
// at ITS stamp. That is why `seedMail` replaces state rather than merging: a
// resync re-anchors the picture and the stream in one step.

/** How the driver reaches the world. Every effect is injected so the whole loop
 * — including resync and the dead-credential end — can be driven in a unit test
 * with no daemon, no network and no clock. */
export type MailStreamPorts = {
  fetchFn: (path: string, init?: RequestInit) => Promise<Response>;
  seed: (dto: MailDto) => void;
  fold: (frame: SseFrame) => void;
  setState: (s: MailStreamState) => void;
  /** Fires when the reduced bus asks for a fresh snapshot. Returns an
   * unsubscribe. The driver never inspects reduced state itself — the reducer
   * is the only thing entitled to decide the picture is untrustworthy. */
  onResnapshotRequest: (cb: () => void) => () => void;
  sleep: (ms: number, signal: AbortSignal) => Promise<void>;
  signal: AbortSignal;
  path?: string;
  streamPath?: string;
  /** Floor between consecutive re-seeds. A bus that keeps giving the reducer
   * reasons to distrust it (a store being rewritten under us) must not become a
   * fetch loop: the resync is real work and it is bounded. */
  resyncFloorMs?: number;
};

export type MailStreamResult = "dead" | "stopped" | "snapshotOnly";

/** The snapshot → seed → stream → resync loop. Returns "dead" if the credential
 * died (the session is over), "snapshotOnly" if the daemon serves no trail, and
 * "stopped" when the caller aborts. */
export async function runMailStream(o: MailStreamPorts): Promise<MailStreamResult> {
  const path = o.path ?? "/api/v1/mail";
  const streamPath = o.streamPath ?? "/api/v1/events";
  const floor = o.resyncFloorMs ?? 750;
  let resyncs = 0;

  while (!o.signal.aborted) {
    if (resyncs > 0) {
      o.setState("resyncing");
      // Backs off like the stream's own reconnect, and for the same reason: the
      // failure that made us resync may still be happening.
      try { await o.sleep(Math.min(floor * resyncs, 15000), o.signal); } catch { return "stopped"; }
    }

    let dto: MailDto;
    try {
      const resp = await o.fetchFn(path);
      if (resp.status === 401 || resp.status === 403) { o.setState("dead"); return "dead"; }
      if (!resp.ok) { resyncs++; continue; }          // a blip, a drain: try again
      dto = (await resp.json()) as MailDto;
    } catch {
      if (o.signal.aborted) return "stopped";
      resyncs++;
      continue;
    }
    if (o.signal.aborted) return "stopped";

    // Seed BEFORE opening the stream. Not an optimization — the stamp we are
    // about to open at describes THIS snapshot, so folding a frame into the
    // previous picture would apply it to a ledger it was never about.
    o.seed(dto);

    // No trail served ⇒ no id space to align to and no stream to open. The
    // picture is real and frozen, and the view says exactly that rather than
    // implying it is live. Deliberately NOT treated as "resume from 0": that
    // token means "the earliest still-reachable point" and would replay the
    // whole trail as though it were happening now.
    if (dto.trailEventId === null) { o.setState("snapshotOnly"); return "snapshotOnly"; }

    // One abort for this attachment: the caller's stop, or the reducer asking
    // for a fresh snapshot. Both end the stream; only the second loops.
    const attach = new AbortController();
    const onOuterAbort = () => attach.abort();
    o.signal.addEventListener("abort", onOuterAbort, { once: true });
    let asked = false;
    const unsubscribe = o.onResnapshotRequest(() => { asked = true; attach.abort(); });

    let result: "dead" | "stopped";
    try {
      result = await runEventStream({
        fetchFn: o.fetchFn,
        path: streamPath,
        initialCursor: dto.trailEventId,
        onFrame: o.fold,
        onState: (s) => o.setState(s === "idle" ? "idle" : s),
        sleep: o.sleep,
        signal: attach.signal,
      });
    } finally {
      unsubscribe();
      o.signal.removeEventListener("abort", onOuterAbort);
    }

    if (result === "dead") { o.setState("dead"); return "dead"; }
    if (o.signal.aborted) return "stopped";
    if (!asked) return "stopped";   // the stream ended for a reason that was not ours
    resyncs++;
  }
  return "stopped";
}

function defaultSleep(ms: number, signal: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
    if (signal.aborted) return reject(new Error("aborted"));
    const t = setTimeout(() => { signal.removeEventListener("abort", onAbort); resolve(); }, ms);
    const onAbort = () => { clearTimeout(t); reject(new Error("aborted")); };
    signal.addEventListener("abort", onAbort, { once: true });
  });
}

/** Bind the driver to the real store and the real credential. Started by the
 * Mail island on its first visit and left running for the session — the same
 * lifetime the trace's stream has, and for the same reason: leaving a view
 * renders it null but does not unmount it, and a picture that stopped following
 * the bus while you looked at Status would be wrong the moment you came back
 * with no way to tell. */
export function startMailStream(): { stop: () => void } {
  const ctrl = new AbortController();
  const store = useStore.getState();
  void runMailStream({
    fetchFn: apiFetch,
    seed: store.seedMailSnapshot,
    fold: store.foldMailFrame,
    setState: store.setMailStream,
    onResnapshotRequest: (cb) => {
      let last = useStore.getState().mail.resnapshot;
      return useStore.subscribe((s) => {
        const now = s.mail.resnapshot;
        // Edge-triggered: `seedMail` clears the field, so a fresh non-null
        // value is a NEW request. Level-triggering here would re-fire on every
        // unrelated store write while the flag stood.
        if (now !== null && now !== last) cb();
        last = now;
      });
    },
    sleep: defaultSleep,
    signal: ctrl.signal,
  }).then((result) => {
    if (result === "dead") {
      clearToken();
      useStore.getState().setSession("dead");
    }
  });
  return {
    stop: () => {
      ctrl.abort();
      useStore.getState().setMailStream("idle");
    },
  };
}
