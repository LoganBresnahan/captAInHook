import type { ExecEntry } from "./handlers.ts";
import type { HarnessesDto } from "./api.gen.ts";

// The template gallery's METADATA and mapping (ADR-0015 d3). The script TEXT
// lives in ./templateScripts.ts, which is the only file that touches Vite's
// `?raw` — `node --test` runs this suite with zero dependencies and cannot
// resolve a query-suffixed import, and the mapping is the part worth unit
// testing anyway. The gallery joins the two by id.
//
// The original note stands: Authoring in the GUI is a CURATED
// GALLERY, not a script-writing API: the daemon never gains a verb that writes
// an executable file, because that is a new trust surface — exactly the "code
// the user cannot read" shape ADR-0011 deferred behind a future ADR. The user
// saves the script themselves; the GUI installs the entry behind the verbatim
// confirm, which stays the gate.
//
// The script TEXT is single-sourced from `examples/payloads/` via Vite `?raw`
// (build-time inlining — no runtime fetch, no generation step, no second copy
// to drift). That import crosses `web/`'s boundary into the repo's examples on
// purpose (ADR-0015 N3): editing a starter script changes the shipped GUI on
// the next build, and the examples README says so.
//
// Only GENERIC starters belong here. The maintainer's dogfood payloads
// (git-orient, deploy-guard, session-pulse, doc-pointer, orient-brief) are
// deliberately absent: they encode one person's workflow and would read as
// recommendations.

export type Template = {
  id: string;
  title: string;
  /** One line: what it does, in the user's terms. */
  blurb: string;
  /** The effect verb the payload answers with — what it DOES to the loop. */
  effect: "decide" | "inject" | "noop";
  /** Why this event, in one clause — the latency/shape reasoning, not a label. */
  why: string;
  /** Repo-relative source, shown in the save instructions. */
  file: string;
  /** Everything the install form can be pre-filled with. `command` is NOT here:
   * it depends on where the user saves the file (see `suggestedCommand`). */
  entry: Omit<ExecEntry, "command">;
};

export const TEMPLATES: Template[] = [
  {
    id: "starter-inject",
    title: "Inject context",
    blurb: "Puts a note file plus one live fact (the session's git branch) in front of the model each turn.",
    effect: "inject",
    why: "UserPromptSubmit is per-turn and cheap to spawn on; the cost that matters is the context it spends, not the process.",
    file: "examples/payloads/starter-inject.sh",
    entry: { name: "inject-context", events: ["UserPromptSubmit"], mode: "oneshot", failMode: "open", budgetMs: 2000 },
  },
  {
    id: "starter-decide",
    title: "Guard a tool call",
    blurb: "Inspects the pending tool call and can DENY it, with a reason the agent sees.",
    effect: "decide",
    why: "PreToolUse is the only event that accepts `decide` on claude-code — a gate anywhere else cannot stop anything.",
    file: "examples/payloads/starter-decide.sh",
    entry: { name: "guard", events: ["PreToolUse"], mode: "oneshot", failMode: "open", budgetMs: 1500 },
  },
  {
    id: "starter-side-effect",
    title: "Side effect only",
    blurb: "Does work (logs, notifies, kicks off a build) and returns nothing to the loop.",
    effect: "noop",
    why: "Stop permits no loop effects at all, which makes it the right home for a hook whose value is the side effect — and keeps its spawn off every tool call.",
    file: "examples/payloads/starter-side-effect.sh",
    entry: { name: "side-effect", events: ["Stop"], mode: "oneshot", failMode: "open", budgetMs: 3000 },
  },
  {
    id: "starter-llm",
    title: "LLM-backed subsystem",
    blurb: "Asks a second, non-interactive model a narrow question and injects its answer. Degrades to noop if the model is absent or slow.",
    effect: "inject",
    why: "The hook blocks on a model call, so it belongs on a per-turn event with a deliberate budget — and it MUST NOT fire its own event (the script carries the `--setting-sources \"\"` reentrancy guard).",
    file: "examples/payloads/starter-llm.sh",
    entry: { name: "llm-context", events: ["UserPromptSubmit"], mode: "oneshot", failMode: "open", budgetMs: 25000 },
  },
  {
    id: "retriever",
    title: "Resident retriever (demo)",
    blurb: "A warm, lock-step child that stays alive across dispatches and injects notes relevant to the imminent tool call.",
    effect: "inject",
    why: "Resident because PreToolUse fires on every tool call: a oneshot there spawns an interpreter on the agent's critical path, serially, forever.",
    file: "examples/payloads/retriever.sh",
    entry: { name: "retriever", events: ["PreToolUse"], mode: "resident", failMode: "open", budgetMs: 2000, readinessTimeoutMs: 5000 },
  },
  {
    id: "memory",
    title: "Session memory (demo)",
    blurb: "Appends one line per session end to a durable log — the smallest possible oneshot.",
    effect: "noop",
    why: "Session edges are rare, so a warm process would just idle; the spawn cost is paid once, not per action.",
    file: "examples/payloads/memory.sh",
    entry: { name: "memory", events: ["Stop"], mode: "oneshot", failMode: "open", budgetMs: 3000 },
  },
];

/** Where to suggest saving a template's script: `<runtime home>/payloads/<file>`
 * — a real absolute path on THIS machine, never a `~` the daemon would not
 * expand. The home is derived from what the daemon actually reports, in order:
 *
 *   1. the handlers.json path (`~/.captainHook/handlers.json` ⇒ `~/.captainHook`)
 *      — the runtime home the daemon is really using, including a test sandbox;
 *   2. the resolved shim path (`~/.captainHook/bin/captainShim`), when no
 *      handlers file has been configured yet;
 *   3. nothing. The form then asks for the path outright rather than inventing
 *      one — a wrong absolute path is worse than an empty field, because the
 *      daemon would accept it and the handler would silently never run.
 */
export function suggestedCommand(
  template: Template,
  paths: { handlersPath?: string | null; shimPath?: string | null },
): string | null {
  const base = template.file.split("/").pop() ?? template.id;
  const join = (home: string, sep: string) => `${home}${sep}payloads${sep}${base}`;

  const { handlersPath, shimPath } = paths;
  if (handlersPath) {
    const sep = handlersPath.includes("\\") ? "\\" : "/";
    const parts = handlersPath.split(sep);
    if (parts.length >= 2) return join(parts.slice(0, -1).join(sep), sep);
  }
  if (shimPath) {
    const sep = shimPath.includes("\\") ? "\\" : "/";
    const parts = shimPath.split(sep);
    if (parts.length >= 3) return join(parts.slice(0, -2).join(sep), sep);   // strip /bin/captainShim
  }
  return null;
}

/** The form pre-fill for a template: its entry metadata plus the suggested
 * command (blank when unknown — an install with no command cannot pass the
 * form's own checks, which is the honest outcome). */
export function templateEntry(
  template: Template,
  paths: { handlersPath?: string | null; shimPath?: string | null },
): ExecEntry {
  return {
    ...template.entry,
    // Copy the array: a shallow spread would hand the form the TEMPLATE's own
    // events, and the first checkbox click would edit the gallery itself.
    events: [...template.entry.events],
    command: suggestedCommand(template, paths) ?? "",
  };
}

/** The effect verbs a harness permits per event — data the client has always
 * fetched and nothing displayed until ADR-0015 d3. An event with an EMPTY list
 * accepts no loop effects at all (Stop, SessionEnd): a payload there can still
 * do work, it just cannot change the turn. */
export function eventVerbs(harnesses: HarnessesDto | null, harness = "claude-code"): Record<string, string[]> {
  const spec = harnesses?.harnesses.find((h) => h.name === harness) ?? harnesses?.harnesses[0];
  return spec?.events ?? {};
}

/** Does this template's effect actually land on this event? Used to warn in the
 * gallery rather than to forbid: the harness registry is data, a user may know
 * something we do not, and the daemon is the authority either way. */
export function effectLandsOn(verbs: Record<string, string[]>, event: string, effect: Template["effect"]): boolean {
  if (effect === "noop") return true;               // always permitted; it changes nothing
  const allowed = verbs[event];
  return allowed === undefined || allowed.includes(effect);
}
