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
