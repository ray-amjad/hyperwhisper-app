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

// The one list of provider ids, derived from the page's catalog mirror. The
// ingest takes it as an argument rather than importing it, so the tests exercise
// the real list by handing it in the same way route.ts does.
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

  assert.equal(KNOWN_PROVIDERS.length, 12);
  for (const provider of KNOWN_PROVIDERS) {
    const result = validateSample(goodSample({ provider }));
    assert.ok("sample" in result, `${provider} should be accepted`);
  }
});

test("a retired provider is never also a live one", async () => {
  const { KNOWN_PROVIDERS, RETIRED_PROVIDERS } = await loadProviders();

  // RETIRED_PROVIDERS is applied as a NOT IN against the whole scan, so an id
  // that appears in both lists would silently blank a provider the apps still
  // offer — the page would draw nothing for it and say nothing about why. The
  // two lists are maintained by hand at opposite ends of a provider's life, so
  // this is the only thing stopping a retirement being written against the
  // wrong id.
  for (const provider of RETIRED_PROVIDERS) {
    assert.ok(
      !KNOWN_PROVIDERS.includes(provider),
      `${provider} is retired but still in the catalog mirror`,
    );
  }
});

test("chirp_3 is retired, so ingest refuses it and the page hides its history", async () => {
  const { validateSample } = await load();
  const { RETIRED_PROVIDERS } = await loadProviders();

  // Chirp 3 stopped being selectable at catalog v8 (2026-08-27), but the sweep
  // that filled this page ran before that, so the 90-day window still holds its
  // rows. Both halves have to hold: no new rows arrive, and the old ones are
  // not drawn.
  assert.ok(RETIRED_PROVIDERS.includes("google-chirp"));
  assert.deepEqual(
    validateSample(goodSample({ provider: "google-chirp" })),
    { reason: "unknown provider" },
  );
});

type CatalogFile = {
  providers: {
    sttProvider: string;
    vendor: string;
    vendorDisplayName: string;
    models: { id: string; displayName: string; isDefault?: boolean }[];
  }[];
};

/**
 * The real catalog, read as data rather than imported — the point is precisely
 * that the two deployables do not share a module system.
 * shared-app-classification/cloud-stt-catalog.json lives outside the Next.js
 * build root, so lib/latency/providers.ts copies it. If the catalog gains a
 * provider, renames a backend id, or renames a model, the copy goes stale: the
 * page silently stops rendering that provider AND the ingest starts rejecting
 * its rows as an unknown provider.
 */
function readCatalog(): CatalogFile {
  const catalogPath = fileURLToPath(
    new URL("../../shared-app-classification/cloud-stt-catalog.json", import.meta.url),
  );
  return JSON.parse(readFileSync(catalogPath, "utf8")) as CatalogFile;
}

test("the page's catalog mirror matches the catalog the apps read", async () => {
  const { STT_CATALOG } = await loadProviders();

  const shape = (entry: CatalogFile["providers"][number]) => ({
    sttProvider: entry.sttProvider,
    vendor: entry.vendor,
    vendorDisplayName: entry.vendorDisplayName,
    // Order matters as well as content: it is what puts a vendor's model rows in
    // the same order as the app's Model dropdown.
    models: entry.models.map((model) => ({
      id: model.id,
      displayName: model.displayName,
      isDefault: model.isDefault === true,
    })),
  });

  assert.deepEqual(
    (STT_CATALOG as CatalogFile["providers"]).map(shape),
    readCatalog().providers.map(shape),
  );
});

test("a provider row is named after a vendor, never after one of its models", async () => {
  const { STT_CATALOG, vendorDisplayName } = await loadProviders();

  // A provider row blends every model of that vendor, so a label naming one of
  // them makes a claim about the rows underneath that the aggregate does not —
  // "Deepgram Nova 3" over cells that also hold nova-2 timings. Naming models is
  // the model rows' job. The catalog's per-entry `displayName` carries those
  // suffixes, which is why the page reads `vendorDisplayName` instead.
  //
  // Two shapes to catch: a version number ("Nova 3", "Scribe v2",
  // "MAI-Transcribe 1.5"), and a bare model family name.
  const version = /\d/;
  const modelFamily = /\b(whisper|nova|voxtral|universal|scribe|chirp|gemini|grok)/i;
  for (const entry of STT_CATALOG as CatalogFile["providers"]) {
    const label = vendorDisplayName(entry.vendor);
    assert.ok(
      !version.test(label),
      `${entry.vendor}: "${label}" names a model version, but the row blends every model of that vendor`,
    );
    assert.ok(
      !modelFamily.test(label),
      `${entry.vendor}: "${label}" names a model family, but the row blends every model of that vendor`,
    );
  }
});

test("Google's two entries share one row, the way the app's Provider menu shows them", async () => {
  const { STT_CATALOG, vendorDisplayName } = await loadProviders();

  // The merge is the catalog's, not this app's: both entries carry
  // vendor "google", which is what macOS's cloudTierVendorGroups collapses on.
  // Splitting them here would put two rows on the page that the app never names
  // separately. Which two entries they are comes from the catalog, so retiring
  // one (google-chirp → gemini-transcribe) cannot leave this assertion behind.
  const google = (STT_CATALOG as CatalogFile["providers"]).filter(
    (entry) => entry.vendor === "google",
  );
  assert.deepEqual(
    google.map((entry) => entry.sttProvider).sort(),
    readCatalog()
      .providers.filter((entry) => entry.vendor === "google")
      .map((entry) => entry.sttProvider)
      .sort(),
  );
  assert.equal(google.length, 2);
  assert.equal(vendorDisplayName("google"), "Google");
});

test("every model row is named the way the app's Model menu names it", async () => {
  const { modelDisplayName, isDefaultModel, modelSortIndex } = await loadProviders();

  for (const entry of readCatalog().providers) {
    for (const model of entry.models) {
      assert.equal(
        modelDisplayName(entry.sttProvider, model.id),
        model.displayName,
        `${entry.sttProvider}/${model.id}`,
      );
      assert.ok(modelSortIndex(entry.sttProvider, model.id) < Number.MAX_SAFE_INTEGER);
    }
  }

  // The ingest stores null for a provider whose endpoint takes no model id, and
  // the catalog spells that same model with an empty id. Both must resolve to
  // the one model, or xAI's only row would print its raw backend id.
  assert.equal(modelDisplayName("grok", null), "Grok Speech-to-Text");
  assert.equal(modelDisplayName("grok", ""), "Grok Speech-to-Text");
  assert.equal(isDefaultModel("grok", null), true);

  // A model the mirror has not learned yet keeps its raw id and sorts last,
  // rather than borrowing another model's name or position.
  assert.equal(modelDisplayName("openai", "gpt-5-transcribe"), "gpt-5-transcribe");
  assert.equal(modelSortIndex("openai", "gpt-5-transcribe"), Number.MAX_SAFE_INTEGER);
});

test("exactly one model per vendor is badged as the default", async () => {
  const { STT_CATALOG, isDefaultModel } = await loadProviders();

  // A row on the page is a vendor, so "default" has to mean what the app means
  // by it: what picking that vendor and touching nothing else gives you. Google
  // marks a default inside BOTH its entries (gemini-3.5-transcribe and
  // gemini-2.5-flash), but selecting Google lands on the first entry in catalog
  // order and then on that entry's default — one model, not two.
  const badged = new Map<string, string[]>();
  for (const entry of STT_CATALOG as CatalogFile["providers"]) {
    for (const model of entry.models) {
      if (!isDefaultModel(entry.sttProvider, model.id)) continue;
      badged.set(entry.vendor, [...(badged.get(entry.vendor) ?? []), model.displayName]);
    }
  }

  for (const entry of STT_CATALOG as CatalogFile["providers"]) {
    assert.deepEqual(
      badged.get(entry.vendor)?.length,
      1,
      `${entry.vendor}: ${JSON.stringify(badged.get(entry.vendor))}`,
    );
  }
  // Named from the catalog, not from the mirror: the badge must follow the
  // first Google entry in catalog order whichever entry that is.
  const firstGoogle = readCatalog().providers.find((entry) => entry.vendor === "google");
  const expectedDefault = firstGoogle?.models.find((model) => model.isDefault === true);
  assert.deepEqual(badged.get("google"), [expectedDefault?.displayName]);
  assert.equal(expectedDefault?.displayName, "Gemini 3.5 Transcribe");
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
  // The next hour is a different value, so the trailing window still moves.
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
