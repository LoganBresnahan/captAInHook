// gui-handlers-editor + gui-enable-disable (ADR-0011 d3/d4) pure-logic pins:
// the compose helpers (upsert preserves order, remove touches one), the PUT
// client's If-Match discipline INCLUDING the 412 inversion (conflict never
// hands back a tag to retry with — the lost-update guard), the toggle compose
// (prepend-deny first-match-wins, enable removes only unconditional toggle
// denies, never composes over unparsable policy), the disabled/scoped badge
// classification, and the wiring-hint substitution.
import { test } from "node:test";
import assert from "node:assert/strict";
import {
  parseEntries, serializeEntries, upsertEntry, removeEntry, formProblems,
  submitHandlers, disabledState, togglePolicyText, kebab, wiringHint,
  type ExecEntry,
} from "./handlers.ts";

const json = (status: number, body: unknown, headers?: Record<string, string>) =>
  new Response(JSON.stringify(body), { status, headers });

const entry = (name: string, over: Partial<ExecEntry> = {}): ExecEntry => ({
  name, command: "/bin/payload", events: ["UserPromptSubmit"], ...over,
});

// ---- compose ----------------------------------------------------------------

test("parseEntries: valid file round-trips; absent/malformed/wrong-shape → null", () => {
  const text = serializeEntries([entry("a"), entry("b")]);
  const parsed = parseEntries(text);
  assert.equal(parsed?.length, 2);
  assert.equal(parsed?.[0].name, "a");
  assert.equal(parseEntries(null), null);
  assert.equal(parseEntries("{nope"), null);
  assert.equal(parseEntries('{"version":2,"handlers":[]}'), null);
  assert.equal(parseEntries('{"version":1}'), null);
});

test("upsertEntry: append when new, replace IN PLACE on edit (order preserved — registration order is load-bearing)", () => {
  const list = [entry("a"), entry("b"), entry("c")];
  const edited = upsertEntry(list, entry("b", { command: "/new" }), "b");
  assert.deepEqual(edited.map((e) => e.name), ["a", "b", "c"]);
  assert.equal(edited[1].command, "/new");
  const renamed = upsertEntry(list, entry("b2"), "b");
  assert.deepEqual(renamed.map((e) => e.name), ["a", "b2", "c"]);
  const added = upsertEntry(list, entry("d"), null);
  assert.deepEqual(added.map((e) => e.name), ["a", "b", "c", "d"]);
});

test("removeEntry removes exactly the named entry", () => {
  assert.deepEqual(
    removeEntry([entry("a"), entry("b")], "a").map((e) => e.name), ["b"]);
});

test("formProblems: absolute-command mandate (the d2 'resolved' decision) and basics", () => {
  assert.deepEqual(formProblems(entry("ok")), []);
  assert.ok(formProblems(entry("ok", { command: "payload.sh" })).some((p) => p.includes("absolute")));
  assert.ok(formProblems(entry("", {})).some((p) => p.includes("name")));
  assert.ok(formProblems(entry("a#b")).some((p) => p.includes("#")));
  assert.ok(formProblems(entry("ok", { events: [] })).some((p) => p.includes("event")));
});

// ---- the PUT client ---------------------------------------------------------

test("If-Match rides the PUT when an etag is known; null etag sends none (create)", async () => {
  let seen: string | null = "unset";
  const fetchFn = (_p: string, init?: RequestInit) => {
    seen = new Headers(init?.headers).get("If-Match");
    return Promise.resolve(json(200, { etag: '"n"' }, { ETag: '"n"' }));
  };
  await submitHandlers(fetchFn, "{}", '"tag1"');
  assert.equal(seen, '"tag1"');
  await submitHandlers(fetchFn, "{}", null);
  assert.equal(seen, null);
});

test("200 adopts the header tag and carries the echoed dto (no re-GET)", async () => {
  const v = await submitHandlers(
    () => Promise.resolve(json(200, { etag: '"body"', source: "loaded" }, { ETag: '"hdr"' })),
    "{}", null);
  assert.equal(v.kind, "written");
  assert.equal(v.kind === "written" && v.etag, '"hdr"');
  assert.equal(v.kind === "written" && v.dto?.source, "loaded");
});

test("412 → conflict WITHOUT a retry tag — the PolicyPanel-pin-3 INVERSION", async () => {
  // The lost-update guard: a composed read-modify-write must never adopt the
  // server's `current` and retry, because the compose was built over a stale
  // file — retrying would clobber the other editor's entries. The verdict
  // deliberately carries NO tag, making blind retry unrepresentable.
  const v = await submitHandlers(
    () => Promise.resolve(json(412, { error: "etag_mismatch", current: '"newer"' }, { ETag: '"newer"' })),
    "{}", '"stale"');
  assert.deepEqual(v, { kind: "conflict" });
});

test("422 surfaces the daemon's violations (includes d3's skip-refusal)", async () => {
  const v = await submitHandlers(
    () => Promise.resolve(json(422, { error: "invalid_handlers", violations: ["broken: 'command' is required"] })),
    "{}", null);
  assert.equal(v.kind, "invalid");
  assert.ok(v.kind === "invalid" && v.violations[0].includes("broken"));
});

test("network throw and odd statuses degrade to failed — submitHandlers never throws", async () => {
  const v1 = await submitHandlers(() => Promise.reject(new Error("net down")), "{}", null);
  assert.equal(v1.kind, "failed");
  const v2 = await submitHandlers(() => Promise.resolve(json(500, { error: "write_failed" })), "{}", null);
  assert.equal(v2.kind, "failed");
});

// ---- enable/disable compose -------------------------------------------------

test("disable PREPENDS the unconditional deny; a hand-written allow is KEPT (inert behind it), so off→on is identity", () => {
  const raw = JSON.stringify({
    version: 1, default: "allow",
    rules: [{ handler: "x", decision: "allow" }, { event: "Stop", decision: "deny" }],
  });
  const off = JSON.parse(togglePolicyText(raw, "x", true)!);
  // Prepended deny first (position 0 wins every context); the hand-written
  // allow survives BEHIND it (adversarial-verify F4 — deleting it made the
  // round-trip destructive); the handler-less Stop rule untouched.
  assert.deepEqual(off.rules, [
    { handler: "x", decision: "deny" },
    { handler: "x", decision: "allow" },
    { event: "Stop", decision: "deny" },
  ]);
  assert.equal(off.default, "allow");   // hand-written fields preserved
  // The identity: enable removes exactly the toggle's deny, restoring the
  // original document rule-for-rule.
  const on = JSON.parse(togglePolicyText(JSON.stringify(off), "x", false)!);
  assert.deepEqual(on.rules, JSON.parse(raw).rules);
});

test("enable removes ONLY unconditional handler-denies; scoped rules survive", () => {
  const raw = JSON.stringify({
    version: 1, default: "allow",
    rules: [
      { handler: "x", decision: "deny" },
      { handler: "x", project: "/repo", decision: "deny" },   // scoped: someone's policy
      { handler: "y", decision: "deny" },
    ],
  });
  const doc = JSON.parse(togglePolicyText(raw, "x", false)!);
  assert.deepEqual(doc.rules, [
    { handler: "x", project: "/repo", decision: "deny" },
    { handler: "y", decision: "deny" },
  ]);
});

test("toggle over NO policy file composes a fresh minimal policy", () => {
  const doc = JSON.parse(togglePolicyText(null, "x", true)!);
  assert.deepEqual(doc, { version: 1, default: "allow", rules: [{ handler: "x", decision: "deny" }] });
});

test("toggle NEVER composes over unparsable policy text", () => {
  assert.equal(togglePolicyText("{broken", "x", true), null);
});

test("disabledState follows the daemon's first-match: first handler-mentioning rule decides; scoped-first → 'scoped'", () => {
  const mk = (rules: unknown[]) => JSON.stringify({ version: 1, rules });
  assert.equal(disabledState(mk([{ handler: "x", decision: "deny" }]), "x"), "disabled");
  assert.equal(disabledState(mk([
    { handler: "x", decision: "allow" }, { handler: "x", decision: "deny" },
  ]), "x"), "enabled");
  assert.equal(disabledState(mk([{ handler: "x", event: "Stop", decision: "deny" }]), "x"), "scoped");
  // Adversarial-verify F2: an earlier SCOPED allow really does run the
  // handler on its event, whatever a later unconditional deny says — the
  // badge must say "scoped", never "disabled".
  assert.equal(disabledState(mk([
    { handler: "x", event: "Stop", decision: "allow" }, { handler: "x", decision: "deny" },
  ]), "x"), "scoped");
  assert.equal(disabledState(mk([]), "x"), "enabled");
  assert.equal(disabledState(null, "x"), "enabled");
  assert.equal(disabledState("{broken", "x"), "enabled");
});

test("F1: valid-JSON-but-malformed shapes never throw — rules:{}/[null]/'str', entries [null,42]", () => {
  // The daemon tolerates these (malformed policy = loud deny-all; a non-object
  // handlers entry = warn-and-skip) — the boundary-less GUI must not crash on
  // what the daemon survives (adversarial-verify F1).
  for (const rules of [{}, [null], "str"]) {
    assert.equal(disabledState(JSON.stringify({ version: 1, rules }), "x"), "enabled");
    assert.equal(togglePolicyText(JSON.stringify({ version: 1, rules }), "x", true), null);
  }
  assert.equal(parseEntries('{"version":1,"handlers":[null,42]}'), null);
  assert.equal(parseEntries('{"version":1,"handlers":[{"noName":1}]}'), null);
});

// ---- wiring hint ------------------------------------------------------------

test("kebab: canonical event names to hook-command spelling", () => {
  assert.equal(kebab("UserPromptSubmit"), "user-prompt-submit");
  assert.equal(kebab("PreToolUse"), "pre-tool-use");
  assert.equal(kebab("SessionStart"), "session-start");
  assert.equal(kebab("Stop"), "stop");
});

test("wiringHint substitutes the resolved shim and event; falls back to the deploy home; null on no template", () => {
  const install = {
    configFile: "~/.claude/settings.json",
    hooksPath: "hooks.{event}",
    entry: { type: "command", command: "{captainShim} hook {event-kebab}" },
  };
  assert.deepEqual(wiringHint(install, "/home/u/.captainHook/bin/captainShim", "PreToolUse"), {
    configFile: "~/.claude/settings.json",
    command: "/home/u/.captainHook/bin/captainShim hook pre-tool-use",
  });
  assert.equal(
    wiringHint(install, null, "Stop")!.command,
    "~/.captainHook/bin/captainShim hook stop");
  assert.equal(wiringHint(null, "/x", "Stop"), null);
  assert.equal(wiringHint({ entry: {} }, "/x", "Stop"), null);
});
