import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync, statSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import {
  TEMPLATES, suggestedCommand, templateEntry, eventVerbs, effectLandsOn,
  verbsLabel, verbColumns,
} from "./templates.ts";
import type { HarnessesDto } from "./api.gen.ts";

// The gallery's pure half (ADR-0015 d3): the template → form mapping and the
// per-event verb derivation. The scripts themselves are smoke-run through a
// real daemon (they are executable examples, not prose); this covers the
// mapping the GUI does with them.

const t = (id: string) => {
  const found = TEMPLATES.find((x) => x.id === id);
  assert.ok(found, `template ${id} exists`);
  return found;
};

const repoRoot = dirname(dirname(dirname(fileURLToPath(import.meta.url))));   // web/src → repo

test("every template names a REAL, executable script in examples/payloads/", () => {
  // The `?raw` import is resolved by Vite at build time, so nothing else would
  // catch a template pointing at a path that no longer exists — the gallery
  // would just fail to build, or ship an empty <pre>. Check the files
  // themselves (ADR-0015 N3: this is the coupling that crosses web/'s boundary).
  assert.ok(TEMPLATES.length >= 4);
  for (const tpl of TEMPLATES) {
    assert.ok(tpl.file.startsWith("examples/payloads/"), `${tpl.id} names its source`);
    const path = join(repoRoot, tpl.file);
    const text = readFileSync(path, "utf8");
    assert.ok(text.startsWith("#!/bin/sh"), `${tpl.file} is a POSIX sh script`);
    assert.ok((statSync(path).mode & 0o111) !== 0, `${tpl.file} is executable`);
    assert.ok(tpl.entry.events.length > 0, `${tpl.id} has an event`);
    assert.ok(tpl.entry.name.length > 0, `${tpl.id} suggests a name`);
  }
});

test("the LLM starter carries the reentrancy guard (ADR-0010 N7)", () => {
  // A template that fires its own event is a footgun that ships to everyone who
  // clicks it — the one property of that script worth pinning here.
  const llm = t("starter-llm");
  const text = readFileSync(join(repoRoot, llm.file), "utf8");
  assert.match(text, /--setting-sources/, "the guard flag is present");
});

test("the maintainer's dogfood payloads are NOT offered as templates", () => {
  // d3: they encode one person's workflow; shipping them as starters would read
  // as a recommendation.
  const files = TEMPLATES.map((x) => x.file);
  for (const dogfood of ["git-orient", "deploy-guard", "session-pulse", "doc-pointer", "orient-brief"])
    assert.ok(!files.some((f) => f.includes(dogfood)), `${dogfood} is not a template`);
});

test("suggestedCommand prefers the handlers.json directory — the daemon's real runtime home", () => {
  const path = suggestedCommand(t("starter-decide"), {
    handlersPath: "/tmp/sandbox-1/handlers.json",
    shimPath: "/home/me/.captainHook/bin/captainShim",
  });
  assert.equal(path, "/tmp/sandbox-1/payloads/starter-decide.sh",
    "the file the daemon actually loads wins over the deploy home");
});

test("suggestedCommand falls back to the shim's deploy home when no handlers file is configured", () => {
  const path = suggestedCommand(t("starter-decide"), { shimPath: "/home/me/.captainHook/bin/captainShim" });
  assert.equal(path, "/home/me/.captainHook/payloads/starter-decide.sh");
});

test("suggestedCommand invents nothing when the daemon reported neither path", () => {
  assert.equal(suggestedCommand(t("starter-decide"), {}), null);
  assert.equal(suggestedCommand(t("starter-decide"), { handlersPath: null, shimPath: null }), null);
  // A path too short to strip /bin/<exe> from is refused rather than mangled —
  // a WRONG absolute path is worse than none, since the daemon would take it.
  assert.equal(suggestedCommand(t("starter-decide"), { shimPath: "captainShim" }), null);
});

test("templateEntry pre-fills the form, leaving command blank when unknown", () => {
  const withShim = templateEntry(t("retriever"), { shimPath: "/home/me/.captainHook/bin/captainShim" });
  assert.equal(withShim.name, "retriever");
  assert.deepEqual(withShim.events, ["PreToolUse"]);
  assert.equal(withShim.mode, "resident");
  assert.equal(withShim.failMode, "open");
  assert.equal(withShim.budgetMs, 2000);
  assert.equal(withShim.readinessTimeoutMs, 5000);
  assert.equal(withShim.command, "/home/me/.captainHook/payloads/retriever.sh");

  const blind = templateEntry(t("retriever"), {});
  assert.equal(blind.command, "", "an unknown path becomes an empty field, never a guess");
});

test("templateEntry does not alias the template's own entry object", () => {
  const a = templateEntry(t("memory"), { shimPath: "/h/.captainHook/bin/captainShim" });
  a.events.push("SessionEnd");
  assert.deepEqual(t("memory").entry.events, ["Stop"], "the template is unchanged");
});

const dto = (events: Record<string, string[]>): HarnessesDto => ({
  harnesses: [{
    name: "claude-code", responseAdapter: "claude-hook-json",
    request: { eventNameField: "hook_event_name", sessionIdField: "session_id", cwdField: "cwd" },
    events, install: null,
  }],
});

test("eventVerbs projects the harness's per-event effect lists", () => {
  const verbs = eventVerbs(dto({ PreToolUse: ["decide", "inject"], Stop: [] }));
  assert.deepEqual(verbs.PreToolUse, ["decide", "inject"]);
  assert.deepEqual(verbs.Stop, [], "an empty list is data, not absence");
});

test("eventVerbs is empty — never throws — before the harnesses fetch lands", () => {
  assert.deepEqual(eventVerbs(null), {});
  assert.deepEqual(eventVerbs({ harnesses: [] }), {});
});

test("eventVerbs falls back to the first harness when the named one is absent", () => {
  const verbs = eventVerbs(dto({ Stop: ["inject"] }), "not-a-harness");
  assert.deepEqual(verbs.Stop, ["inject"]);
});

test("effectLandsOn: decide lands on PreToolUse, not on Stop", () => {
  const verbs = { PreToolUse: ["decide", "inject"], Stop: [] };
  assert.equal(effectLandsOn(verbs, "PreToolUse", "decide"), true);
  assert.equal(effectLandsOn(verbs, "Stop", "decide"), false);
  assert.equal(effectLandsOn(verbs, "Stop", "inject"), false);
});

test("effectLandsOn: noop always lands, and an UNKNOWN event is not accused", () => {
  const verbs = { Stop: [] };
  assert.equal(effectLandsOn(verbs, "Stop", "noop"), true, "noop changes nothing, so it is always fine");
  assert.equal(effectLandsOn(verbs, "SomeFutureEvent", "decide"), true,
    "the registry is data and may lag the daemon — warn on what we know, not on what we do not");
});

// The shared render helper (slice 7): the gallery's per-event line and the
// Harnesses matrix both read capability through these, so the two views cannot
// come to disagree about what an empty list — or an unknown event — means.

test("verbsLabel: declared verbs, declared-empty, and NOT declared are three different sentences", () => {
  const verbs = { PreToolUse: ["decide", "inject"], Stop: [] };
  assert.equal(verbsLabel(verbs, "PreToolUse"), "decide inject");
  assert.equal(verbsLabel(verbs, "Stop"), "no loop effects",
    "the harness declares that nothing lands here — that is a fact about Stop");
  assert.equal(verbsLabel(verbs, "SomeFutureEvent"), "not declared here",
    "an event the registry never mentions is UNKNOWN, not empty — claiming 'no loop effects' " +
    "would be the same false accusation effectLandsOn refuses to make");
});

test("verbColumns: the union of declared verbs, in first-declared order, deduped", () => {
  const columns = verbColumns({
    SessionStart: ["inject"],
    PreToolUse: ["decide", "inject"],
    PostToolUse: ["inject", "replace"],
    Stop: [],
  });
  assert.deepEqual(columns, ["inject", "decide", "replace"]);
});

test("verbColumns: a verb this build has never heard of still gets a column", () => {
  // ADR-0003's rule is that capabilities are DECLARED IN DATA. A hardcoded
  // column list would render a future verb as a silent blank — the harness
  // would permit something the matrix says it does not.
  assert.deepEqual(verbColumns({ SomeEvent: ["teleport"] }), ["teleport"]);
  assert.deepEqual(verbColumns({}), [], "no declarations ⇒ no matrix to draw");
  assert.deepEqual(verbColumns({ Stop: [], SessionEnd: [] }), [],
    "a harness whose every event declares nothing has no columns either");
});

test("effectLandsOn answers about verbs the gallery has no template for", () => {
  // The matrix asks per COLUMN, so it asks about `replace` — which no starter
  // uses. The predicate is shared, so it has to answer for any verb string.
  const verbs = { PostToolUse: ["inject", "replace"], PreToolUse: ["decide", "inject"] };
  assert.equal(effectLandsOn(verbs, "PostToolUse", "replace"), true);
  assert.equal(effectLandsOn(verbs, "PreToolUse", "replace"), false);
});

test("every shipped template's effect lands on every event it suggests", () => {
  // The gallery's own claims, checked against the real claude-code registry
  // shape. A starter that cannot work as configured would be a trap.
  const verbs = {
    SessionStart: ["inject"], UserPromptSubmit: ["inject"],
    PreToolUse: ["decide", "inject"], PostToolUse: ["inject", "replace"],
    Stop: [], SessionEnd: [],
  };
  for (const tpl of TEMPLATES)
    for (const ev of tpl.entry.events)
      assert.equal(effectLandsOn(verbs, ev, tpl.effect), true,
        `${tpl.id}: ${tpl.effect} lands on ${ev}`);
});
