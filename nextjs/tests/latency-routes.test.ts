/**
 * The two internal latency routes, driven as HTTP.
 *
 * `POST /api/internal/latency` is the write side of the public /latency page.
 * It sits behind the shared `x-internal-secret`, so a missed gate lets anyone
 * write rows the public page then draws. It also carries a privacy promise:
 * a stored row must not let a reader regroup one transcription's fallback
 * chain, so the exact clip length is dropped and the timestamp is cut to the
 * hour.
 *
 * `GET /api/internal/latency/prune` is the daily cron that makes the privacy
 * page's "we keep these rows for a year" true. A wrong cutoff deletes live
 * data or keeps old data; a broken auth branch makes every nightly run 401
 * without anyone noticing.
 *
 * So the tests below assert what a caller and the database actually receive —
 * the status, the reply body, the exact rows and the rendered delete clause —
 * not that a mock was called. Collaborators are replaced at the module
 * boundary in `latency-routes-harness.ts`; the route modules are the real ones.
 */
import assert from "node:assert/strict";
import { after, afterEach, before, beforeEach, describe, test } from "node:test";

import {
  behaviour,
  calls,
  fakeEnv,
  getRequest,
  loadKnownProviders,
  loadLatencyIngestRoute,
  loadLatencyPruneRoute,
  loadLatencySchema,
  logLines,
  postRequest,
  resetHarness,
  restoreRouteLogging,
  silenceRouteLogging,
} from "./latency-routes-harness";

const INGEST_PATH = "/api/internal/latency";
const PRUNE_PATH = "/api/internal/latency/prune";

/** Made-up secrets. Only the fake env in the harness holds them. */
const INTERNAL_SECRET = "internal-secret-for-tests";
const CRON_SECRET = "cron-secret-for-tests";

/** 2026-03-04T05:06:07.890Z — deliberately not on an hour boundary. */
const NOW = Date.UTC(2026, 2, 4, 5, 6, 7, 890);
const HOUR_START = new Date(Date.UTC(2026, 2, 4, 5, 0, 0, 0));
const DAY_MS = 24 * 60 * 60 * 1000;

type JsonBody = Record<string, unknown>;

async function readJson(response: Response): Promise<JsonBody> {
  return (await response.json()) as JsonBody;
}

let provider = "";
let otherProvider = "";

function sample(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    provider,
    model: "model-a",
    flyRegion: "fra",
    audioSeconds: 5,
    latencyMs: 800,
    ok: true,
    attempt: 1,
    ...overrides,
  };
}

function authed(extra: Record<string, string> = {}): Record<string, string> {
  return { "x-internal-secret": INTERNAL_SECRET, ...extra };
}

const realInternalSecret = process.env.HYPERWHISPER_INTERNAL_SECRET;
const realDateNow = Date.now;

before(async () => {
  silenceRouteLogging();
  const { KNOWN_PROVIDERS } = await loadKnownProviders();
  assert.ok(KNOWN_PROVIDERS.length >= 2, "the catalog must name two providers");
  [provider, otherProvider] = KNOWN_PROVIDERS;
});

after(() => {
  restoreRouteLogging();
});

beforeEach(() => {
  resetHarness();
  logLines.length = 0;
  Date.now = () => NOW;
  process.env.HYPERWHISPER_INTERNAL_SECRET = INTERNAL_SECRET;
});

afterEach(() => {
  Date.now = realDateNow;
  if (realInternalSecret === undefined) {
    delete process.env.HYPERWHISPER_INTERNAL_SECRET;
  } else {
    process.env.HYPERWHISPER_INTERNAL_SECRET = realInternalSecret;
  }
});

describe("POST /api/internal/latency — the secret gate", () => {
  test("answers 401 with no secret, and writes nothing", async () => {
    const { POST } = await loadLatencyIngestRoute();
    const res = await POST(postRequest(INGEST_PATH, { samples: [sample()] }));

    assert.equal(res.status, 401);
    assert.deepEqual(await readJson(res), { error: "Unauthorized" });
    assert.equal(calls.inserts.length, 0);
    assert.equal(calls.rateLimit.length, 0);
  });

  test("answers 401 to a wrong secret of the same length", async () => {
    const { POST } = await loadLatencyIngestRoute();
    const wrong = INTERNAL_SECRET.replace(/.$/, "X");
    const res = await POST(
      postRequest(INGEST_PATH, { samples: [sample()] }, { "x-internal-secret": wrong }),
    );

    assert.equal(res.status, 401);
    assert.equal(calls.inserts.length, 0);
  });

  test("answers 401 when the server has no secret configured", async () => {
    delete process.env.HYPERWHISPER_INTERNAL_SECRET;
    const { POST } = await loadLatencyIngestRoute();
    const res = await POST(
      postRequest(INGEST_PATH, { samples: [sample()] }, { "x-internal-secret": "" }),
    );

    assert.equal(res.status, 401);
    assert.equal(calls.inserts.length, 0);
  });
});

describe("POST /api/internal/latency — body guards", () => {
  test("answers 413 when the declared length is over 32 KB, before reading", async () => {
    const { POST } = await loadLatencyIngestRoute();
    const res = await POST(
      postRequest(
        INGEST_PATH,
        { samples: [sample()] },
        authed({ "content-length": String(32 * 1024 + 1) }),
      ),
    );

    assert.equal(res.status, 413);
    assert.deepEqual(await readJson(res), { error: "Payload too large" });
    assert.equal(calls.inserts.length, 0);
  });

  test("answers 413 when the real body is over 32 KB", async () => {
    const { POST } = await loadLatencyIngestRoute();
    const body = JSON.stringify({ samples: [sample()], pad: "x".repeat(32 * 1024) });
    const res = await POST(postRequest(INGEST_PATH, body, authed()));

    assert.equal(res.status, 413);
    assert.equal(calls.inserts.length, 0);
  });

  test("accepts a body of exactly 32 KB", async () => {
    const { POST } = await loadLatencyIngestRoute();
    const envelope = JSON.stringify({ samples: [sample()], pad: "" });
    const body = envelope.replace('"pad":""', `"pad":"${"x".repeat(32 * 1024 - envelope.length)}"`);
    assert.equal(body.length, 32 * 1024);
    const res = await POST(postRequest(INGEST_PATH, body, authed()));

    assert.equal(res.status, 200);
    assert.equal(calls.inserts.length, 1);
  });

  test("answers 400 to a body that is not JSON", async () => {
    const { POST } = await loadLatencyIngestRoute();
    const res = await POST(postRequest(INGEST_PATH, "{not json", authed()));

    assert.equal(res.status, 400);
    assert.deepEqual(await readJson(res), { error: "Invalid JSON" });
    assert.equal(calls.rateLimit.length, 0);
  });

  test("answers 400 with the validator's reason to a bad envelope", async () => {
    const { POST } = await loadLatencyIngestRoute();
    const res = await POST(postRequest(INGEST_PATH, { samples: [] }, authed()));

    assert.equal(res.status, 400);
    assert.deepEqual(await readJson(res), { error: "samples must not be empty" });
    assert.equal(calls.rateLimit.length, 0);
    assert.equal(calls.inserts.length, 0);
  });
});

describe("POST /api/internal/latency — what is stored", () => {
  test("stores one row per sample, bucketed, with no clip length and an hour timestamp", async () => {
    const { POST } = await loadLatencyIngestRoute();
    const { sttLatencySamples } = await loadLatencySchema();
    const res = await POST(
      postRequest(
        INGEST_PATH,
        {
          samples: [
            sample({ audioSeconds: 9, latencyMs: 700, ok: false, failureKind: "timeout" }),
            sample({ provider: otherProvider, model: "", audioSeconds: 10, attempt: 2 }),
            sample({ audioSeconds: 31, latencyMs: 4_000, attempt: 3 }),
          ],
        },
        authed(),
      ),
    );

    assert.equal(res.status, 200);
    assert.deepEqual(await readJson(res), { inserted: 3, skipped: [], max: 20 });

    assert.equal(calls.inserts.length, 1);
    const [insert] = calls.inserts;
    assert.equal(insert.table, sttLatencySamples);
    assert.deepEqual(insert.rows, [
      {
        provider,
        model: "model-a",
        flyRegion: "fra",
        audioSeconds: null,
        durationBucket: "short",
        latencyMs: 700,
        ok: false,
        failureKind: "timeout",
        attempt: 1,
        createdAt: HOUR_START,
      },
      {
        provider: otherProvider,
        model: null,
        flyRegion: "fra",
        audioSeconds: null,
        durationBucket: "medium",
        latencyMs: 800,
        ok: true,
        failureKind: null,
        attempt: 2,
        createdAt: HOUR_START,
      },
      {
        provider,
        model: "model-a",
        flyRegion: "fra",
        audioSeconds: null,
        durationBucket: "long",
        latencyMs: 4_000,
        ok: true,
        failureKind: null,
        attempt: 3,
        createdAt: HOUR_START,
      },
    ]);
  });

  test("rate-limits by the batch's region, not by the caller", async () => {
    const { POST } = await loadLatencyIngestRoute();
    await POST(
      postRequest(
        INGEST_PATH,
        { samples: [sample({ flyRegion: "iad" }), sample({ flyRegion: "fra" })] },
        authed({ "x-forwarded-for": "203.0.113.9" }),
      ),
    );

    assert.deepEqual(calls.rateLimit, ["iad"]);
  });

  test("keys the rate limit on the first STORED sample, not the first sent", async () => {
    const { POST } = await loadLatencyIngestRoute();
    await POST(
      postRequest(
        INGEST_PATH,
        { samples: [sample({ flyRegion: "local" }), sample({ flyRegion: "sin" })] },
        authed(),
      ),
    );

    assert.deepEqual(calls.rateLimit, ["sin"]);
  });

  test("answers 429 when the region is over its limit, and stores nothing", async () => {
    behaviour.rateLimitSuccess = false;
    const { POST } = await loadLatencyIngestRoute();
    const res = await POST(postRequest(INGEST_PATH, { samples: [sample()] }, authed()));

    assert.equal(res.status, 429);
    assert.deepEqual(await readJson(res), { error: "Rate limit exceeded" });
    assert.equal(calls.inserts.length, 0);
  });

  test("stores the good rows and reports the skipped ones by index", async () => {
    const { POST } = await loadLatencyIngestRoute();
    const res = await POST(
      postRequest(
        INGEST_PATH,
        {
          samples: [
            sample({ provider: "not-a-provider" }),
            sample(),
            sample({ flyRegion: "local" }),
            sample({ provider: "also-not-a-provider" }),
          ],
        },
        authed(),
      ),
    );

    assert.equal(res.status, 200);
    const body = await readJson(res);
    assert.equal(body.inserted, 1);
    assert.deepEqual(body.skipped, [
      { index: 0, reason: "unknown provider" },
      { index: 2, reason: "invalid flyRegion" },
      { index: 3, reason: "unknown provider" },
    ]);
    assert.equal(calls.inserts[0].rows.length, 1);

    const warning = logLines.find((line) => line.level === "warn");
    assert.ok(warning, "a dropped sample must be logged");
    assert.deepEqual(warning.args[1], {
      skipped: 3,
      received: 4,
      reasons: ["unknown provider", "invalid flyRegion"],
    });
  });

  test("logs nothing when no sample was dropped", async () => {
    const { POST } = await loadLatencyIngestRoute();
    await POST(postRequest(INGEST_PATH, { samples: [sample()] }, authed()));

    assert.equal(logLines.length, 0);
  });

  test("writes nothing, but still answers 200, when every sample is dropped", async () => {
    const { POST } = await loadLatencyIngestRoute();
    const res = await POST(
      postRequest(INGEST_PATH, { samples: [sample({ provider: "nope" })] }, authed()),
    );

    assert.equal(res.status, 200);
    assert.deepEqual(await readJson(res), {
      inserted: 0,
      skipped: [{ index: 0, reason: "unknown provider" }],
    });
    assert.equal(calls.inserts.length, 0);
    assert.deepEqual(calls.rateLimit, ["unknown"]);
  });

  test("answers 500, not 400, when the database write fails", async () => {
    behaviour.insertError = new Error("connection reset");
    const { POST } = await loadLatencyIngestRoute();
    const res = await POST(postRequest(INGEST_PATH, { samples: [sample()] }, authed()));

    assert.equal(res.status, 500);
    assert.deepEqual(await readJson(res), { error: "Internal server error" });
  });
});

describe("GET /api/internal/latency/prune — who may run it", () => {
  beforeEach(() => {
    fakeEnv.HYPERWHISPER_INTERNAL_SECRET = INTERNAL_SECRET;
    fakeEnv.CRON_SECRET = CRON_SECRET;
  });

  const accepted: Array<[string, Record<string, string>]> = [
    ["the internal secret in x-internal-secret", { "x-internal-secret": INTERNAL_SECRET }],
    ["the internal secret as a bearer", { authorization: `Bearer ${INTERNAL_SECRET}` }],
    ["the Vercel cron secret as a bearer", { authorization: `Bearer ${CRON_SECRET}` }],
    // Fetch strips the ends of a header value, so the only padding that reaches
    // the route is the gap between the scheme and the token.
    ["a bearer with an extra space after the scheme", { authorization: `Bearer  ${CRON_SECRET}` }],
  ];

  for (const [name, headers] of accepted) {
    test(`accepts ${name}`, async () => {
      const { GET } = await loadLatencyPruneRoute();
      const res = await GET(getRequest(PRUNE_PATH, headers));

      assert.equal(res.status, 200);
      assert.equal(calls.deletes.length, 1);
    });
  }

  const refused: Array<[string, Record<string, string>]> = [
    ["no credentials", {}],
    ["the cron secret in x-internal-secret", { "x-internal-secret": CRON_SECRET }],
    ["a bare secret with no Bearer scheme", { authorization: INTERNAL_SECRET }],
    ["a lowercase bearer scheme", { authorization: `bearer ${INTERNAL_SECRET}` }],
    ["a wrong bearer", { authorization: "Bearer not-the-secret" }],
  ];

  for (const [name, headers] of refused) {
    test(`refuses ${name}, and deletes nothing`, async () => {
      const { GET } = await loadLatencyPruneRoute();
      const res = await GET(getRequest(PRUNE_PATH, headers));

      assert.equal(res.status, 401);
      assert.deepEqual(await readJson(res), { error: "Unauthorized" });
      assert.equal(calls.deletes.length, 0);
    });
  }

  test("refuses an empty bearer even when CRON_SECRET is unset", async () => {
    delete fakeEnv.CRON_SECRET;
    const { GET } = await loadLatencyPruneRoute();
    const res = await GET(getRequest(PRUNE_PATH, { authorization: "Bearer " }));

    assert.equal(res.status, 401);
    assert.equal(calls.deletes.length, 0);
  });

  test("logs which credentials exist when it refuses, and never the values", async () => {
    delete fakeEnv.CRON_SECRET;
    const { GET } = await loadLatencyPruneRoute();
    await GET(getRequest(PRUNE_PATH, { authorization: `Bearer ${CRON_SECRET}` }));

    const line = logLines.find((entry) => entry.level === "error");
    assert.ok(line, "a refused prune must be logged");
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

describe("GET /api/internal/latency/prune — what it deletes", () => {
  beforeEach(() => {
    fakeEnv.HYPERWHISPER_INTERNAL_SECRET = INTERNAL_SECRET;
    fakeEnv.CRON_SECRET = CRON_SECRET;
  });

  const cron = () => getRequest(PRUNE_PATH, { authorization: `Bearer ${CRON_SECRET}` });
  const expectedCutoff = new Date(NOW - 365 * DAY_MS);

  test("deletes rows older than exactly 365 days, 5,000 at a time", async () => {
    const { GET } = await loadLatencyPruneRoute();
    const res = await GET(cron());

    assert.equal(res.status, 200);
    assert.deepEqual(await readJson(res), {
      deleted: 0,
      cutoff: expectedCutoff.toISOString(),
    });

    assert.equal(calls.deletes.length, 1);
    const [batch] = calls.deletes;
    assert.match(batch.sql, /"created_at" < \$1/);
    assert.match(batch.sql, /limit \$2/);
    assert.equal(batch.params.length, 2);
    assert.equal(new Date(String(batch.params[0])).getTime(), expectedCutoff.getTime());
    assert.equal(batch.params[1], 5_000);

    const { sttLatencySamples } = await loadLatencySchema();
    assert.equal(batch.table, sttLatencySamples);
  });

  test("keeps deleting while each batch is full, and sums the counts", async () => {
    behaviour.deleteRowCounts = [5_000, 5_000, 12];
    const { GET } = await loadLatencyPruneRoute();
    const res = await GET(cron());

    assert.equal(res.status, 200);
    assert.equal((await readJson(res)).deleted, 10_012);
    assert.equal(calls.deletes.length, 3);
  });

  test("stops after a batch that deleted exactly one row short of full", async () => {
    behaviour.deleteRowCounts = [4_999, 5_000];
    const { GET } = await loadLatencyPruneRoute();
    const res = await GET(cron());

    assert.equal((await readJson(res)).deleted, 4_999);
    assert.equal(calls.deletes.length, 1);
  });

  test("stops at 20 batches in one run, even when there is more to delete", async () => {
    behaviour.deleteRowCounts = Array.from({ length: 25 }, () => 5_000);
    const { GET } = await loadLatencyPruneRoute();
    const res = await GET(cron());

    assert.equal(res.status, 200);
    assert.equal((await readJson(res)).deleted, 100_000);
    assert.equal(calls.deletes.length, 20);
  });

  test("treats a missing rowCount as 0 and stops", async () => {
    behaviour.deleteRowCounts = [undefined, 5_000];
    const { GET } = await loadLatencyPruneRoute();
    const res = await GET(cron());

    assert.equal((await readJson(res)).deleted, 0);
    assert.equal(calls.deletes.length, 1);
  });

  test("answers 500 when the database delete fails", async () => {
    behaviour.deleteError = new Error("lock timeout");
    const { GET } = await loadLatencyPruneRoute();
    const res = await GET(cron());

    assert.equal(res.status, 500);
    assert.deepEqual(await readJson(res), { error: "Internal server error" });
  });
});
