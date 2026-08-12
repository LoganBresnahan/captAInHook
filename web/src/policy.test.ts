// The ETag discipline (ADR-0008 phase-4 verify pins), tested against a
// scripted server: If-Match ALWAYS sent when an etag is known, the 200's ETag
// adopted without a re-GET, the 412's `current` adopted before retry, and the
// null-etag create sending no If-Match at all.
import { test } from "node:test";
import assert from "node:assert/strict";
import { submitPolicy } from "./policy.ts";

const json = (status: number, body: unknown, headers?: Record<string, string>) =>
  new Response(JSON.stringify(body), { status, headers });

test("If-Match rides every PUT once an etag is known — verbatim", async () => {
  let seen: string | null = "unset";
  await submitPolicy(
    (_p, init) => {
      seen = new Headers(init?.headers).get("If-Match");
      return Promise.resolve(json(200, { etag: '"next"' }, { ETag: '"next"' }));
    },
    "{}",
    '"abc123"',
  );
  assert.equal(seen, '"abc123"');
});

test("no etag (absent policy, first write) sends NO If-Match — a create has nothing to protect", async () => {
  let seen: string | null = "unset";
  await submitPolicy(
    (_p, init) => {
      seen = new Headers(init?.headers).get("If-Match");
      return Promise.resolve(json(200, { etag: '"v1"' }, { ETag: '"v1"' }));
    },
    "{}",
    null,
  );
  assert.equal(seen, null);
});

test("200 adopts the response ETag (header authoritative) and carries the echoed policy", async () => {
  const dto = { state: "loaded", etag: '"body-tag"', raw: "{}" };
  const r = await submitPolicy(
    () => Promise.resolve(json(200, dto, { ETag: '"header-tag"' })),
    "{}",
    '"old"',
  );
  assert.deepEqual(r.verdict, { kind: "written", etag: '"header-tag"' });
  assert.equal((r.policy as { etag?: string }).etag, '"body-tag"');
});

test("412 surfaces the server's current tag for adoption — the retry is deliberate, not blind", async () => {
  const r = await submitPolicy(
    () => Promise.resolve(json(412, { error: "etag_mismatch", current: '"theirs"' }, { ETag: '"theirs"' })),
    "{}",
    '"mine"',
  );
  assert.deepEqual(r.verdict, { kind: "mismatch", current: '"theirs"' });
});

test("422 carries the daemon's own violations, verbatim", async () => {
  const r = await submitPolicy(
    () => Promise.resolve(json(422, { error: "invalid_policy", violations: ["rules[0]: unknown event"] })),
    "{}",
    null,
  );
  assert.deepEqual(r.verdict, { kind: "invalid", violations: ["rules[0]: unknown event"] });
});

test("a 200 with NO tag anywhere maps to null — never '' (the server IGNORES an empty If-Match, so '' would pretend to protect while writing blind)", async () => {
  const r = await submitPolicy(
    () => Promise.resolve(json(200, { state: "loaded", etag: null })),
    "{}",
    '"old"',
  );
  assert.deepEqual(r.verdict, { kind: "written", etag: null });
});

test("a non-JSON body on a JSON-promising status degrades to failed, never throws", async () => {
  const r = await submitPolicy(
    () => Promise.resolve(new Response("<html>proxy error</html>", { status: 200 })),
    "{}",
    null,
  );
  assert.equal(r.verdict.kind, "failed");
});

test("network failure and odd statuses degrade to failed, never throw", async () => {
  const netdown = await submitPolicy(() => Promise.reject(new TypeError("down")), "{}", null);
  assert.equal(netdown.verdict.kind, "failed");
  const draining = await submitPolicy(() => Promise.resolve(json(503, { error: "draining" })), "{}", null);
  assert.deepEqual(draining.verdict, { kind: "failed", detail: "HTTP 503" });
});

// ---------------------------------------------------------------------------
// The rule builder's round-trip (ADR-0015 d4 / slice 6). These tests are the
// point of the slice: a builder that silently rewrites a hand-written
// dispatch.json is the ONE failure here that no screenshot and no e2e would
// catch — the page looks right, the file is quietly different, and the daemon
// starts enforcing something the user never wrote.
//
// The guard is stated as a PROPERTY rather than a checklist: parse → rows →
// serialize must preserve MEANING exactly, and anything that does not is
// refused (the builder locks to raw). Written before the implementation.

const parsedJson = (s: string) => JSON.parse(s) as Record<string, unknown>;

/** Meaning, not text: `default` absent means allow and `rules` absent means
 * none, so those two normalizations are the only differences a faithful
 * round-trip is allowed to introduce. Everything else must match exactly. */
const meaning = (raw: string) => {
  const doc = parsedJson(raw);
  return {
    version: doc.version,
    default: doc.default ?? "allow",
    rules: (doc.rules ?? []) as unknown[],
  };
};

const ROUND_TRIPPABLE: [string, string][] = [
  ["minimal", '{"version":1}'],
  ["explicit empty", '{"version":1,"default":"allow","rules":[]}'],
  ["default deny — the pause", '{"version":1,"default":"deny","rules":[]}'],
  ["one event rule", '{"version":1,"rules":[{"event":"SessionStart","decision":"deny"}]}'],
  ["kebab event spelling is the user's, and stays theirs",
    '{"version":1,"rules":[{"event":"user-prompt-submit","decision":"deny"}]}'],
  ["all four criteria at once",
    '{"version":1,"default":"deny","rules":[{"event":"PreToolUse","handler":"guard","project":"/repo","session":"abc","decision":"allow"}]}'],
  ["order is load-bearing (first match wins)",
    '{"version":1,"rules":[{"handler":"a","decision":"allow"},{"handler":"a","decision":"deny"},{"event":"Stop","decision":"deny"}]}'],
  ["paths and sessions with awkward characters",
    '{"version":1,"rules":[{"project":"/home/me/my repo (v2)/–dash","decision":"deny"},{"session":"a\\"b\\\\c","decision":"allow"}]}'],
  ["unicode survives", '{"version":1,"rules":[{"project":"/tmp/проект/日本語","decision":"deny"}]}'],
];

test("PROPERTY: parse → rows → serialize preserves meaning exactly", async (t) => {
  const { parsePolicyRows, serializePolicyRows } = await import("./policy.ts");
  for (const [name, raw] of ROUND_TRIPPABLE) {
    await t.test(name, () => {
      const doc = parsePolicyRows(raw);
      assert.ok(doc !== null, `${name} should be representable`);
      const out = serializePolicyRows(doc);
      assert.deepEqual(meaning(out), meaning(raw), `${name} round-tripped unchanged`);
    });
  }
});

test("PROPERTY: rows → JSON → rows is the identity on every generated policy", async () => {
  const { parsePolicyRows, serializePolicyRows } = await import("./policy.ts");
  // A deterministic generator — a flake here would be unreproducible, and this
  // is the test that guards a silent-destruction hazard.
  let seed = 0x5eed;
  const rnd = () => (seed = (seed * 1103515245 + 12345) & 0x7fffffff) / 0x7fffffff;
  const pick = <T>(xs: readonly T[]): T => xs[Math.floor(rnd() * xs.length)];
  const maybe = (s: string) => (rnd() < 0.5 ? s : "");

  for (let i = 0; i < 300; i++) {
    const doc = {
      default: pick(["allow", "deny"] as const),
      rules: Array.from({ length: Math.floor(rnd() * 5) }, () => {
        const row = {
          event: maybe(pick(["SessionStart", "UserPromptSubmit", "PreToolUse", "user-prompt-submit"])),
          handler: maybe(pick(["echo", "guard", "a-b_c"])),
          project: maybe(pick(["/repo", "/home/me/x y", "/tmp/日本語"])),
          session: maybe(pick(["abc123", 'a"b'])),
          decision: pick(["allow", "deny"] as const),
        };
        // A criteria-less row is legal to COMPOSE (the daemon is the validator
        // and will 422 it); force one criterion so this property is about the
        // round trip rather than about validity.
        if (!row.event && !row.handler && !row.project && !row.session) row.handler = "forced";
        return row;
      }),
    };
    const back = parsePolicyRows(serializePolicyRows(doc));
    assert.deepEqual(back, doc, `iteration ${i} survived rows → JSON → rows`);
  }
});

test("serializePolicyRows emits the strict dialect: no empty criteria, decision always present", async () => {
  const { serializePolicyRows } = await import("./policy.ts");
  const out = serializePolicyRows({
    default: "allow",
    rules: [{ event: "Stop", handler: "", project: "", session: "", decision: "deny" }],
  });
  const doc = parsedJson(out);
  assert.equal(doc.version, 1, "version 1 is written, not assumed");
  assert.deepEqual(doc.rules, [{ event: "Stop", decision: "deny" }],
    "an unset criterion is OMITTED — the daemon rejects an empty-string criterion");
  assert.ok(out.endsWith("\n"), "trailing newline, like a hand-written file");
});

test("RAW-LOCK: anything the builder cannot rebuild faithfully is refused", async (t) => {
  const { parsePolicyRows } = await import("./policy.ts");
  const unrepresentable: [string, string][] = [
    ["not JSON at all", "{nope"],
    ["a JSON array, not an object", "[]"],
    ["a bare string", '"hello"'],
    ["version missing", '{"rules":[]}'],
    ["version 2 — a dialect we do not know", '{"version":2,"rules":[]}'],
    ["an unknown top-level field we would DROP", '{"version":1,"pause":true,"rules":[]}'],
    ["rules is not an array", '{"version":1,"rules":{}}'],
    ["a rule that is not an object", '{"version":1,"rules":["nope"]}'],
    ["an unknown field inside a rule", '{"version":1,"rules":[{"event":"Stop","decision":"deny","note":"why"}]}'],
    ["a non-string criterion", '{"version":1,"rules":[{"event":1,"decision":"deny"}]}'],
    ["an empty-string criterion", '{"version":1,"rules":[{"event":"","decision":"deny"}]}'],
    ["a decision we do not know", '{"version":1,"rules":[{"event":"Stop","decision":"ask"}]}'],
    ["a default we do not know", '{"version":1,"default":"maybe","rules":[]}'],
    ["a missing decision", '{"version":1,"rules":[{"event":"Stop"}]}'],
    ["a criteria-less rule (the daemon calls it malformed too)", '{"version":1,"rules":[{"decision":"deny"}]}'],
  ];
  for (const [name, raw] of unrepresentable) {
    await t.test(name, () => {
      assert.equal(parsePolicyRows(raw), null, `${name} must lock to raw, never be silently rewritten`);
    });
  }
});

test("an absent file is representable as an empty policy, not a lock", async () => {
  const { parsePolicyRows } = await import("./policy.ts");
  assert.deepEqual(parsePolicyRows(null), { default: "allow", rules: [] },
    "no file yet ⇒ the builder starts from the zero-config default");
});
