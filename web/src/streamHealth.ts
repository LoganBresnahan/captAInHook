import type { StreamStats } from "./store.ts";

// The Trace island's empty-state verdict (2026-08-16), pure so it can be pinned
// under node:test. Born of the dogfood finding that a "streaming" badge over a
// panel at 0 lines was unfalsifiable from the screen: the badge said connected,
// the daemon was dispatching, and the page could not say which of the two it
// disbelieved. Every sentence here is chosen from what is KNOWN — the stream
// state, what this connection has received, and the daemon's own `served`
// counter as of the connect — never from a guess about the future.

/** The empty list's sentence — chosen from what is KNOWN, never a guess.
 * `starved` is the one that used to be invisible: connected, the daemon has
 * dispatched since, and not one frame came through. */
export function emptyTraceReason(
  filter: string, stream: string, stats: StreamStats, served: number | null,
): { kind: string; text: string } {
  if (filter !== "") return { kind: "filtered", text: "No lines match the filter." };
  if (stream === "stalled")
    return { kind: "stalled", text: "Stream stalled — nothing arrived for the stall window, not even a heartbeat. Reconnecting from the last position." };
  if (stream === "retrying")
    return { kind: "retrying", text: "Reconnecting to the daemon…" };
  if (stream !== "live")
    return { kind: "idle", text: "Waiting for the stream to connect." };
  const dispatchedSince = served !== null && stats.servedAtConnect !== null ? served - stats.servedAtConnect : 0;
  if (stats.frames === 0 && dispatchedSince > 0)
    return {
      kind: "starved",
      text: `Connected, but no frames have arrived — the daemon reports ${dispatchedSince.toLocaleString()} dispatch${dispatchedSince === 1 ? "" : "es"} since this stream connected. The stream is not delivering to this page.`,
    };
  return { kind: "quiet", text: "Connected — no hook activity since this stream opened. Fire a prompt or tool call." };
}

