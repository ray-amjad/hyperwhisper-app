/**
 * #2915: an admin refund that REVOKES the Account Key must tell HyperWhisper
 * Cloud, as a rotation and an owner delete do. The cloud caches each key's
 * licence verdict for an hour (`LICENSE_CACHE_TTL_SECONDS`), so without
 * `POST /internal/license-cache/invalidate` it keeps admitting the revoked
 * key until that hour runs out.
 *
 * The router runs through `admin-customers-harness.ts` (DB layer, Stripe,
 * auth and mail mocked); the purge helper `lib/services/cloud-license-cache.ts`
 * runs for real against a stubbed `fetch` for the cloud origin only.
 */
// `src/env/server.mjs` snapshots process.env on first import (#682), which
// happens when the harness first loads the router (inside a test), not here.
process.env.SKIP_ENV_VALIDATION = "1";
process.env.HYPERWHISPER_CLOUD_URLS = "https://cloud.test";
process.env.HYPERWHISPER_INTERNAL_SECRET = ["test", "internal", "1"].join("-");

import assert from "node:assert/strict";
import { afterEach, beforeEach, test } from "node:test";

import {
  accountKeyRow,
  behaviour,
  callCustomers,
  calls,
  resetHarness,
} from "./admin-customers-harness";
import { captureConsole, type ConsoleCapture } from "./module-mock";

const LICENSE_ID = "11111111-1111-4111-8111-111111111111";
const KEY = "HW-FAKE-0000-0001";
const PURGE_URL = "https://cloud.test/internal/license-cache/invalidate";

type CloudAnswer = "ok" | "500" | "throw";

const realFetch = globalThis.fetch;
let sent: Array<{ url: string; body: unknown; secret: string | null }> = [];
let cloudAnswer: CloudAnswer = "ok";
let logs: string[] = [];
let capture: ConsoleCapture | null = null;

beforeEach(() => {
  resetHarness();
  behaviour.accountsById.set(
    LICENSE_ID,
    accountKeyRow({ id: LICENSE_ID, key: KEY, status: "granted" }),
  );
  sent = [];
  cloudAnswer = "ok";
  logs = [];
  capture = captureConsole(["log", "warn", "error"], logs);

  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    if (!url.startsWith("https://cloud.test")) {
      return realFetch(input as RequestInfo, init);
    }
    const headers = new Headers(init?.headers);
    sent.push({
      url,
      body: init?.body ? JSON.parse(String(init.body)) : null,
      secret: headers.get("x-internal-secret"),
    });
    if (cloudAnswer === "throw") throw new TypeError("fetch failed");
    return new Response("{}", { status: cloudAnswer === "500" ? 500 : 200 });
  }) as typeof fetch;
});

afterEach(() => {
  globalThis.fetch = realFetch;
  capture?.restore();
  capture = null;
});

/** The refund mutation's one `admin.customers.refund` outcome line. */
function refundLine(): Record<string, unknown> {
  const lines = logs
    .filter((line) => line.includes('"admin.customers.refund"'))
    .map((line) => JSON.parse(line.slice(line.indexOf("{"))) as Record<string, unknown>);
  assert.equal(lines.length, 1, `one refund outcome line, got ${JSON.stringify(logs)}`);
  return lines[0];
}

test("admin refund with revokeLicense purges the cloud licence cache for the revoked key", async () => {
  const result = await callCustomers("refund", "mutation", {
    licenseKeyId: LICENSE_ID,
    revokeLicense: true,
  });

  assert.deepEqual(result, { success: true, revoked: true });
  assert.equal(calls.revokeAccountKey.length, 1, "the key was revoked in the DB");
  assert.deepEqual(
    sent.map(({ url, body }) => ({ url, body })),
    [{ url: PURGE_URL, body: { account_key: KEY } }],
    "a revoked key must be purged from the cloud's 1-hour licence cache, as a rotated key is",
  );
  assert.equal(sent[0].secret, process.env.HYPERWHISPER_INTERNAL_SECRET);

  const line = refundLine();
  assert.equal(line.outcome, "ok");
  assert.equal(line.revoked, true);
  assert.equal(line.cloudPurge, "confirmed");
  assert.equal(line.cloudPurged, 1);
  assert.equal(line.cloudPurgeFailed, 0);
  assert.equal(line.cloudPurgeSkipped, false);
  for (const entry of logs) assert.ok(!entry.includes(KEY), `the key is never logged: ${entry}`);
});

test("admin refund without revokeLicense sends nothing to the cloud", async () => {
  const result = await callCustomers("refund", "mutation", {
    licenseKeyId: LICENSE_ID,
    revokeLicense: false,
  });

  assert.deepEqual(result, { success: true, revoked: false });
  assert.equal(calls.revokeAccountKey.length, 0);
  assert.deepEqual(sent, []);
  assert.equal(refundLine().cloudPurge, undefined);
});

test("a key the owner already deleted (revoked: false) is not purged again", async () => {
  behaviour.revokeRevoked = false;

  const result = await callCustomers("refund", "mutation", {
    licenseKeyId: LICENSE_ID,
    revokeLicense: true,
  });

  assert.deepEqual(result, { success: true, revoked: false });
  assert.equal(calls.revokeAccountKey.length, 1);
  assert.deepEqual(sent, [], "an owner-deleted key was purged at delete time");
  assert.equal(refundLine().cloudPurge, undefined);
});

for (const answer of ["500", "throw"] as const) {
  test(`a cloud that ${answer === "500" ? "answers 500" : "cannot be reached"} does not fail the refund`, async () => {
    cloudAnswer = answer;

    const result = await callCustomers("refund", "mutation", {
      licenseKeyId: LICENSE_ID,
      revokeLicense: true,
    });

    assert.deepEqual(result, { success: true, revoked: true });
    assert.equal(sent.length, 1, "the purge was attempted once");

    const line = refundLine();
    assert.equal(line.outcome, "ok");
    assert.equal(line.cloudPurge, "unconfirmed");
    assert.equal(line.cloudPurged, 0);
    assert.equal(line.cloudPurgeFailed, 1);
    for (const entry of logs) assert.ok(!entry.includes(KEY), `the key is never logged: ${entry}`);
  });
}

test("control: the real purge helper reaches the stub cloud in this env", async () => {
  const { purgeCloudLicenseCache } = await import("../lib/services/cloud-license-cache");
  const outcome = await purgeCloudLicenseCache(KEY);
  assert.deepEqual(outcome, { purged: 1, failed: 0, skipped: false });
  assert.deepEqual(sent.map(({ url }) => url), [PURGE_URL]);
});
