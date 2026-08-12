import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { fileURLToPath } from "node:url";

// Imported through a variable specifier inside each test, the way the other
// tests in this folder do: a static `.ts` import path is a type error under this
// tsconfig, but `node --test --experimental-strip-types` needs the extension.
const MODULE_PATH = "../app/api/internal/latency/validation.ts";

// The clip-length model the ingest and the page both derive from.
const TYPES_MODULE_PATH = "../lib/latency/types.ts";
const loadTypes = () => import(TYPES_MODULE_PATH);

// The one list of provider ids, derived from the page's display map. The ingest
// takes it as an argument rather than importing it, so the tests exercise the
// real list by handing it in the same way route.ts does.
const PROVIDERS_MODULE_PATH = "../lib/latency/providers.ts";
const loadProviders = () => import(PROVIDERS_MODULE_PATH);

async function load() {
  const [validation, providers] = await Promise.all([
    import(MODULE_PATH),
    loadProviders(),
  ]);
  const known = providers.KNOWN_PROVIDERS;
  return {
    ...validation,
    validateSample: (raw: unknown) => validation.validateSample(raw, known),
    validateBatch: (body: unknown) => validation.validateBatch(body, known),
  };
}

function goodSample(overrides: Record<string, unknown> = {}) {
  return {
    provider: "deepgram",
    flyRegion: "fra",
    latencyMs: 812,
    ok: true,
    attempt: 1,
    audioSeconds: 7,
    model: "nova-3-general",
    ...overrides,
  };
}

test("accepts a well-formed success sample", async () => {
  const { validateSample } = await load();
  const result = validateSample(goodSample());
  assert.ok("sample" in result);
  assert.equal(result.sample.provider, "deepgram");
  assert.equal(result.sample.audioSeconds, 7);
  assert.equal(result.sample.failureKind, null);
});

test("buckets clip length at the documented boundaries", async () => {
  const { bucketForSeconds } = await loadTypes();
  assert.equal(bucketForSeconds(0), "short");
  assert.equal(bucketForSeconds(9), "short");
  assert.equal(bucketForSeconds(10), "medium");
  assert.equal(bucketForSeconds(30), "medium");
  assert.equal(bucketForSeconds(31), "long");
});

test("the bucket labels describe the boundaries they are derived from", async () => {
  const { BUCKET_LABELS, DURATION_BUCKETS, DURATION_BUCKET_MODEL, bucketForSeconds } =
    await loadTypes();

  // Ids, labels and thresholds all come from one declaration, so a boundary
  // change cannot leave the page describing a cell it no longer contains.
  assert.deepEqual([...DURATION_BUCKETS], ["short", "medium", "long"]);
  assert.deepEqual(
    DURATION_BUCKET_MODEL.map((bucket: { label: string }) => bucket.label),
    ["Under 10 seconds", "10 to 30 seconds", "Over 30 seconds"],
  );
  for (const bucket of DURATION_BUCKET_MODEL) {
    assert.equal(BUCKET_LABELS[bucket.id], bucket.label);
    if (bucket.maxSeconds !== null) {
      assert.equal(bucketForSeconds(bucket.maxSeconds), bucket.id);
      assert.notEqual(bucketForSeconds(bucket.maxSeconds + 1), bucket.id);
    }
  }
});

test("p99 needs far more attempts than the other metrics", async () => {
  const { MIN_SAMPLES_PER_CELL, MIN_SAMPLES_FOR_P99, minSamplesForMetric } =
    await loadTypes();

  assert.equal(minSamplesForMetric("p50"), MIN_SAMPLES_PER_CELL);
  assert.equal(minSamplesForMetric("p95"), MIN_SAMPLES_PER_CELL);
  assert.equal(minSamplesForMetric("errorRate"), MIN_SAMPLES_PER_CELL);
  assert.equal(minSamplesForMetric("p99"), MIN_SAMPLES_FOR_P99);

  // percentile_cont(0.99) interpolates at index 0.99 x (n - 1): at the p50
  // threshold that is the gap between the two slowest calls in the cell, so one
  // timeout is the whole number. The p99 bar must leave several observations
  // above the reported value.
  const samplesAboveP99 = (n: number) => n - 1 - 0.99 * (n - 1);
  assert.ok(samplesAboveP99(MIN_SAMPLES_PER_CELL) < 1);
  assert.ok(samplesAboveP99(MIN_SAMPLES_FOR_P99) >= 4);
});

test("rejects a provider that is not a backend id", async () => {
  const { validateSample } = await load();
  // The catalog spells this provider `grokStt`; only the backend id is stored.
  const result = validateSample(goodSample({ provider: "grokStt" }));
  assert.deepEqual(result, { reason: "unknown provider" });
});

test("accepts every provider the page can display", async () => {
  const { validateSample } = await load();
  const { KNOWN_PROVIDERS } = await loadProviders();

  assert.equal(KNOWN_PROVIDERS.length, 11);
  for (const provider of KNOWN_PROVIDERS) {
    const result = validateSample(goodSample({ provider }));
    assert.ok("sample" in result, `${provider} should be accepted`);
  }
});

test("the page's provider ids are the catalog's backend ids", async () => {
  const { PROVIDER_DISPLAY_NAMES } = await loadProviders();

  // KNOWN_PROVIDERS is `Object.keys(PROVIDER_DISPLAY_NAMES)`, so comparing the
  // two only restates that line. The claim worth pinning is the one this app
  // cannot enforce by construction: PROVIDER_DISPLAY_NAMES mirrors
  // shared-app-classification/cloud-stt-catalog.json, which lives outside the
  // Next.js build root and so is copied rather than imported. If the catalog
  // gains a provider, or renames a backend id, the copy here goes stale — the
  // page silently stops rendering that provider's column AND the ingest starts
  // rejecting its rows as an unknown provider. Read as data, not imported: the
  // point is precisely that the two deployables do not share a module system.
  const catalogPath = fileURLToPath(
    new URL("../../shared-app-classification/cloud-stt-catalog.json", import.meta.url),
  );
  const catalog = JSON.parse(readFileSync(catalogPath, "utf8")) as {
    providers: { sttProvider: string }[];
  };
  const catalogIds = catalog.providers.map((entry) => entry.sttProvider).sort();

  assert.deepEqual(Object.keys(PROVIDER_DISPLAY_NAMES).sort(), catalogIds);
});

test("no display label names a model the cell does not isolate", async () => {
  const { PROVIDER_DISPLAY_NAMES } = await loadProviders();

  // src/content/latency.ts groups by provider and region, never by model, while
  // users can pin a per-provider model in either desktop app. So a label naming
  // a model makes a claim about the rows underneath it that the query does not
  // — "Deepgram Nova 3" over a cell that also holds nova-2 timings, "OpenAI
  // Whisper" over a cell that is mostly gpt-4o-transcribe. The catalog's own
  // displayName carries those suffixes, which is exactly why this app keeps its
  // own vendor-only labels instead of reusing them.
  //
  // Two shapes to catch: a version number ("Nova 3", "Scribe v2",
  // "MAI-Transcribe 1.5"), and a bare model family name. `\b` keeps
  // "MAI-Transcribe" out of the `scribe` case — that is a product name, not a
  // model this page blends.
  const version = /\d/;
  const modelFamily = /\b(whisper|nova|voxtral|universal|scribe)/i;
  const labels = PROVIDER_DISPLAY_NAMES as Record<string, string>;
  for (const [id, label] of Object.entries(labels)) {
    assert.ok(
      !version.test(label),
      `${id}: "${label}" names a model version, but the cell blends every model of that provider`,
    );
    assert.ok(
      !modelFamily.test(label),
      `${id}: "${label}" names a model family, but the cell blends every model of that provider`,
    );
  }
});

test("rejects a failure sample with no failure kind", async () => {
  const { validateSample } = await load();
  const result = validateSample(goodSample({ ok: false }));
  assert.deepEqual(result, { reason: "invalid failureKind" });
});

test("keeps the failure kind on a failed attempt", async () => {
  const { validateSample } = await load();
  const result = validateSample(goodSample({ ok: false, failureKind: "timeout" }));
  assert.ok("sample" in result);
  assert.equal(result.sample.ok, false);
  assert.equal(result.sample.failureKind, "timeout");
});

test("rejects a failure kind outside the known set", async () => {
  const { validateSample } = await load();
  const result = validateSample(goodSample({ ok: false, failureKind: "exploded" }));
  assert.deepEqual(result, { reason: "invalid failureKind" });
});

test("rejects non-positive and fractional latency", async () => {
  const { validateSample } = await load();
  assert.deepEqual(validateSample(goodSample({ latencyMs: 0 })), {
    reason: "invalid latencyMs",
  });
  assert.deepEqual(validateSample(goodSample({ latencyMs: -5 })), {
    reason: "invalid latencyMs",
  });
  assert.deepEqual(validateSample(goodSample({ latencyMs: 12.5 })), {
    reason: "invalid latencyMs",
  });
  assert.deepEqual(validateSample(goodSample({ latencyMs: Number.POSITIVE_INFINITY })), {
    reason: "invalid latencyMs",
  });
});

test("keeps a very slow attempt instead of dropping the row", async () => {
  const { validateSample, MAX_LATENCY_MS } = await load();

  // A 300 MB clip's upload budget alone is ~50 minutes (max(30 s, 1 s/100 KB)),
  // and the "Over 30 seconds" bucket's percentiles are meant to include exactly
  // these. Rejecting dropped the whole row — the latency AND the ok/failed flag —
  // so the slowest attempts vanished from the tail and from the error rate.
  const slow = validateSample(goodSample({ latencyMs: 50 * 60 * 1000 }));
  assert.ok("sample" in slow);
  assert.equal(slow.sample.latencyMs, 50 * 60 * 1000);

  // Past the ceiling the row still survives; only the value is clamped.
  const absurd = validateSample(goodSample({ latencyMs: MAX_LATENCY_MS * 10 }));
  assert.ok("sample" in absurd);
  assert.equal(absurd.sample.latencyMs, MAX_LATENCY_MS);
});

test("clamps rather than drops a clip length past the ceiling", async () => {
  const { validateSample, MAX_AUDIO_SECONDS } = await load();
  const result = validateSample(goodSample({ audioSeconds: MAX_AUDIO_SECONDS * 4 }));
  assert.ok("sample" in result);
  assert.equal(result.sample.audioSeconds, MAX_AUDIO_SECONDS);
});

test("rejects an attempt outside the fallback chain length", async () => {
  const { validateSample } = await load();
  assert.deepEqual(validateSample(goodSample({ attempt: 0 })), {
    reason: "invalid attempt",
  });
  assert.deepEqual(validateSample(goodSample({ attempt: 9 })), {
    reason: "invalid attempt",
  });
});

test("rejects a region that is not a three-letter lowercase code", async () => {
  const { validateSample } = await load();
  assert.deepEqual(validateSample(goodSample({ flyRegion: "FRA" })), {
    reason: "invalid flyRegion",
  });
  assert.deepEqual(validateSample(goodSample({ flyRegion: "fr" })), {
    reason: "invalid flyRegion",
  });
  assert.deepEqual(validateSample(goodSample({ flyRegion: "frankfurt" })), {
    reason: "invalid flyRegion",
  });
  assert.deepEqual(validateSample(goodSample({ flyRegion: 42 })), {
    reason: "invalid flyRegion",
  });
});

test("rejects the off-Fly region a local machine reports", async () => {
  const { validateSample } = await load();
  // A developer's laptop must never raise a "Local machine" column on the
  // public page.
  assert.deepEqual(validateSample(goodSample({ flyRegion: "local" })), {
    reason: "invalid flyRegion",
  });
});

test("rejects a sample with no clip length, because it cannot be compared", async () => {
  const { validateSample } = await load();
  const sample = goodSample();
  delete (sample as Record<string, unknown>).audioSeconds;
  assert.deepEqual(validateSample(sample), { reason: "missing audioSeconds" });
});

test("accepts a zero-length clip", async () => {
  const { validateSample } = await load();
  const result = validateSample(goodSample({ audioSeconds: 0 }));
  assert.ok("sample" in result);
  assert.equal(result.sample.audioSeconds, 0);
});

test("drops an empty model rather than storing an empty string", async () => {
  const { validateSample } = await load();
  const result = validateSample(goodSample({ model: "" }));
  assert.ok("sample" in result);
  assert.equal(result.sample.model, null);
});

test("skips a malformed sample and keeps the good ones beside it", async () => {
  const { validateBatch } = await load();
  const result = validateBatch({
    samples: [goodSample(), { provider: "nope" }, goodSample({ provider: "groq" })],
  });
  assert.ok(result.ok);
  assert.equal(result.samples.length, 2);
  assert.deepEqual(result.skipped, [{ index: 1, reason: "unknown provider" }]);
});

test("rejects a malformed envelope outright", async () => {
  const { validateBatch } = await load();
  assert.equal(validateBatch(null).ok, false);
  assert.equal(validateBatch({}).ok, false);
  assert.equal(validateBatch({ samples: "x" }).ok, false);
  assert.equal(validateBatch({ samples: [] }).ok, false);
});

test("stores the arrival hour, not the instant, so a chain cannot be reassembled", async () => {
  const { coarseCreatedAt } = await load();
  const at = Date.UTC(2026, 7, 4, 13, 47, 12, 345);

  assert.equal(coarseCreatedAt(at).toISOString(), "2026-08-04T13:00:00.000Z");
  // Every row of one multi-row INSERT lands on the same value as every other
  // row written in that hour — there is nothing left to group a request by.
  assert.equal(
    coarseCreatedAt(at).getTime(),
    coarseCreatedAt(Date.UTC(2026, 7, 4, 13, 0, 0, 0)).getTime(),
  );
  assert.equal(
    coarseCreatedAt(at).getTime(),
    coarseCreatedAt(Date.UTC(2026, 7, 4, 13, 59, 59, 999)).getTime(),
  );
  // The next hour is a different value, so a 30-day window still moves.
  assert.notEqual(
    coarseCreatedAt(at).getTime(),
    coarseCreatedAt(Date.UTC(2026, 7, 4, 14, 0, 0, 0)).getTime(),
  );
});

test("rejects a batch larger than one fallback chain could produce", async () => {
  const { MAX_SAMPLES_PER_REQUEST, validateBatch } = await load();
  const samples = Array.from({ length: MAX_SAMPLES_PER_REQUEST + 1 }, () => goodSample());
  const result = validateBatch({ samples });
  assert.equal(result.ok, false);
});