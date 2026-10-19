/**
 * `GET /api/internal/sessions/prune` is the daily cron that makes the privacy
 * page's "we delete its session record within a day of it expiring" true.
 * Every session row holds the IP address and browser user agent of a sign-in,
 * and Better Auth itself only deletes an expired row when its holder comes
 * back. A wrong cutoff signs live members out or keeps expired rows; a broken
 * auth branch makes every nightly run 401 without anyone noticing.
 *
 * The route module is the real one. The database and the validated env are
 * replaced at the module boundary by `latency-routes-harness.ts`, which the
 * two internal cron routes share.
 */
import assert from "node:assert/strict";
import { after, afterEach, before, beforeEach, describe, test } from "node:test";

import {
  behaviour,
  calls,
  fakeEnv,
  getRequest,
  loadAuthSchema,
  loadSessionPruneRoute,
  logLines,
  resetHarness,
  restoreRouteLogging,
  silenceRouteLogging,
} from "./latency-routes-harness";

const PRUNE_PATH = "/api/internal/sessions/prune";

/** Made-up secrets. Only the fake env in the harness holds them. */
const INTERNAL_SECRET = "internal-secret-for-tests";
const CRON_SECRET = "cron-secret-for-tests";

const NOW = Date.UTC(2026, 9, 19, 4, 37, 0, 123);
const realDateNow = Date.now;

type JsonBody = Record<string, unknown>;

async function readJson(response: Response): Promise<JsonBody> {
  return (await response.json()) as JsonBody;
}

const cron = () => getRequest(PRUNE_PATH, { authorization: `Bearer ${CRON_SECRET}` });

before(() => {
  silenceRouteLogging();
});

after(() => {
  restoreRouteLogging();
});

beforeEach(() => {
  resetHarness();
  logLines.length = 0;
  Date.now = () => NOW;
  fakeEnv.HYPERWHISPER_INTERNAL_SECRET = INTERNAL_SECRET;
  fakeEnv.CRON_SECRET = CRON_SECRET;
});

afterEach(() => {
  Date.now = realDateNow;
});

describe("GET /api/internal/sessions/prune — who may run it", () => {
  const accepted: Array<[string, Record<string, string>]> = [
    ["the internal secret in x-internal-secret", { "x-internal-secret": INTERNAL_SECRET }],
    ["the internal secret as a bearer", { authorization: `Bearer ${INTERNAL_SECRET}` }],
    ["the Vercel cron secret as a bearer", { authorization: `Bearer ${CRON_SECRET}` }],
  ];

  for (const [name, headers] of accepted) {
    test(`accepts ${name}`, async () => {
      const { GET } = await loadSessionPruneRoute();
      const res = await GET(getRequest(PRUNE_PATH, headers));

      assert.equal(res.status, 200);
      assert.equal(calls.deletes.length, 1);
    });
  }

  const refused: Array<[string, Record<string, string>]> = [
    ["no credentials", {}],
    ["the cron secret in x-internal-secret", { "x-internal-secret": CRON_SECRET }],
    ["a bare secret with no Bearer scheme", { authorization: INTERNAL_SECRET }],
    ["a wrong bearer", { authorization: "Bearer not-the-secret" }],
  ];

  for (const [name, headers] of refused) {
    test(`refuses ${name}, and deletes nothing`, async () => {
      const { GET } = await loadSessionPruneRoute();
      const res = await GET(getRequest(PRUNE_PATH, headers));

      assert.equal(res.status, 401);
      assert.deepEqual(await readJson(res), { error: "Unauthorized" });
      assert.equal(calls.deletes.length, 0);
    });
  }

  test("logs the refusal under its own name, and never a secret", async () => {
    delete fakeEnv.CRON_SECRET;
    const { GET } = await loadSessionPruneRoute();
    await GET(getRequest(PRUNE_PATH, { authorization: `Bearer ${CRON_SECRET}` }));

    const line = logLines.find((entry) => entry.level === "error");
    assert.ok(line, "a refused prune must be logged");
    assert.equal(line.args[0], "Unauthorized session prune request:");
    assert.deepEqual(line.args[1], {
      hasInternalSecretHeader: false,
      hasBearer: true,
      internalSecretConfigured: true,
      cronSecretConfigured: false,
    });
    const logged = JSON.stringify(line.args);
    assert.ok(!logged.includes(CRON_SECRET));
    assert.ok(!logged.includes(INTERNAL_SECRET));
  });
});

describe("GET /api/internal/sessions/prune — what it deletes", () => {
  test("deletes session rows whose expires_at is before now, 5,000 at a time", async () => {
    const { GET } = await loadSessionPruneRoute();
    const res = await GET(cron());

    assert.equal(res.status, 200);
    assert.deepEqual(await readJson(res), {
      deleted: 0,
      cutoff: new Date(NOW).toISOString(),
    });

    assert.equal(calls.deletes.length, 1);
    const [batch] = calls.deletes;
    assert.match(batch.sql, /"session"\."id" in \(/);
    assert.match(batch.sql, /"expires_at" < \$1/);
    assert.match(batch.sql, /limit \$2/);
    assert.equal(batch.params.length, 2);
    assert.equal(new Date(String(batch.params[0])).getTime(), NOW);
    assert.equal(batch.params[1], 5_000);

    const { session } = await loadAuthSchema();
    assert.equal(batch.table, session);
  });

  test("keeps deleting while each batch is full, and sums the counts", async () => {
    behaviour.deleteRowCounts = [5_000, 5_000, 7];
    const { GET } = await loadSessionPruneRoute();
    const res = await GET(cron());

    assert.equal(res.status, 200);
    assert.equal((await readJson(res)).deleted, 10_007);
    assert.equal(calls.deletes.length, 3);
  });

  test("stops at 20 batches in one run, even when there is more to delete", async () => {
    behaviour.deleteRowCounts = Array.from({ length: 25 }, () => 5_000);
    const { GET } = await loadSessionPruneRoute();
    const res = await GET(cron());

    assert.equal((await readJson(res)).deleted, 100_000);
    assert.equal(calls.deletes.length, 20);
  });

  test("answers 500 when the database delete fails", async () => {
    behaviour.deleteError = new Error("lock timeout");
    const { GET } = await loadSessionPruneRoute();
    const res = await GET(cron());

    assert.equal(res.status, 500);
    assert.deepEqual(await readJson(res), { error: "Internal server error" });
  });
});

describe("the Vercel cron that runs it", () => {
  test("vercel.json schedules the session prune daily", async () => {
    const { readFile } = await import("node:fs/promises");
    const config = JSON.parse(
      await readFile(new URL("../vercel.json", import.meta.url), "utf8"),
    ) as { crons: Array<{ path: string; schedule: string }> };
    const entry = config.crons.find((c) => c.path === PRUNE_PATH);
    assert.ok(entry, "vercel.json must schedule /api/internal/sessions/prune");
    assert.match(entry.schedule, /^\d+ \d+ \* \* \*$/);
  });
});
