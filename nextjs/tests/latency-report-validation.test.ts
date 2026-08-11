import assert from "node:assert/strict";
import test from "node:test";

// Imported through a variable specifier inside each test, the way the other
// tests in this folder do: a static `.ts` import path is a type error under this
// tsconfig, but `node --test --experimental-strip-types` needs the extension.
const MODULE_PATH = "../app/api/internal/latency/validation.ts";
const load = () => import(MODULE_PATH);

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

test("accepts a well-formed success sample and buckets it", async () => {
  const { validateSample } = await load();
  const result = validateSample(goodSample());
  assert.ok("sample" in result);
  assert.equal(result.sample.provider, "deepgram");
  assert.equal(result.sample.durationBucket, "short");
  assert.equal(result.sample.failureKind, null);
});

test("buckets clip length at the documented boundaries", async () => {
  const { bucketForSeconds } = await load();
  assert.equal(bucketForSeconds(0), "short");
  assert.equal(bucketForSeconds(9), "short");
  assert.equal(bucketForSeconds(10), "medium");
  assert.equal(bucketForSeconds(30), "medium");
  assert.equal(bucketForSeconds(31), "long");
});

test("rejects a provider that is not a backend id", async () => {
  const { validateSample } = await load();
  // The catalog spells this provider `grokStt`; only the backend id is stored.
  const result = validateSample(goodSample({ provider: "grokStt" }));
  assert.deepEqual(result, { reason: "unknown provider" });
});

test("accepts every known backend provider id", async () => {
  const { validateSample } = await load();
  for (const provider of [
    "deepgram",
    "groq",
    "elevenlabs",
    "grok",
    "azure-mai",
    "google-chirp",
    "openai",
    "gemini",
    "assemblyai",
    "mistral",
    "soniox",
  ]) {
    const result = validateSample(goodSample({ provider }));
    assert.ok("sample" in result, `${provider} should be accepted`);
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

test("rejects non-positive, fractional, and oversized latency", async () => {
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
  assert.deepEqual(validateSample(goodSample({ latencyMs: 60 * 60 * 1000 })), {
    reason: "invalid latencyMs",
  });
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

test("rejects a region that is not a short lowercase code", async () => {
  const { validateSample } = await load();
  assert.deepEqual(validateSample(goodSample({ flyRegion: "FRA" })), {
    reason: "invalid flyRegion",
  });
  assert.deepEqual(validateSample(goodSample({ flyRegion: "fr" })), {
    reason: "invalid flyRegion",
  });
  assert.deepEqual(validateSample(goodSample({ flyRegion: 42 })), {
    reason: "invalid flyRegion",
  });
});

test("accepts the off-Fly region a local machine reports", async () => {
  const { validateSample } = await load();
  const result = validateSample(goodSample({ flyRegion: "local" }));
  assert.ok("sample" in result);
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
  assert.equal(result.sample.durationBucket, "short");
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

test("rejects a batch larger than one fallback chain could produce", async () => {
  const { MAX_SAMPLES_PER_REQUEST, validateBatch } = await load();
  const samples = Array.from({ length: MAX_SAMPLES_PER_REQUEST + 1 }, () => goodSample());
  const result = validateBatch({ samples });
  assert.equal(result.ok, false);
});