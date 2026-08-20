import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { fileURLToPath } from "node:url";

// Imported through a variable specifier inside each test, the way the other
// tests in this folder do: a static `.ts` import path is a type error under this
// tsconfig, but `node --test --experimental-strip-types` needs the extension.
const CATALOG_MODULE_PATH = "../lib/choosing-a-model/catalog.ts";
const SCORING_MODULE_PATH = "../lib/choosing-a-model/scoring.ts";

const loadCatalog = () => import(CATALOG_MODULE_PATH);
const loadScoring = () => import(SCORING_MODULE_PATH);

type CatalogFile = {
  providers: {
    id: string;
    sttProvider: string;
    displayName: string;
    access: { cloudTierEligible: boolean; byokEligible: boolean };
    features?: { streaming?: boolean };
    languages?: { count?: number | string };
    models: {
      id: string;
      displayName: string;
      creditsPerMinute: number;
      isDefault?: boolean;
      previewStatus?: boolean;
      supportsCustomVocabulary?: boolean;
    }[];
  }[];
};

/**
 * The real catalog, read as data rather than imported — the point is precisely
 * that the two deployables do not share a module system.
 * shared-app-classification/cloud-stt-catalog.json lives outside the Next.js
 * build root, so lib/choosing-a-model/catalog.ts copies it. When the catalog
 * gains a model, reprices one, or renames a backend id, the copy goes stale and
 * the page quietly ranks a model we no longer sell, or prices one wrongly.
 */
function readCatalog(): CatalogFile {
  const catalogPath = fileURLToPath(
    new URL(
      "../../shared-app-classification/cloud-stt-catalog.json",
      import.meta.url,
    ),
  );
  return JSON.parse(readFileSync(catalogPath, "utf8")) as CatalogFile;
}

test("the page's cloud mirror matches the catalog the apps read", async () => {
  const { CLOUD_MODELS } = await loadCatalog();
  const catalog = readCatalog();

  const expected = catalog.providers.flatMap((provider) =>
    provider.models.map((model) => ({
      id: `${provider.id}:${model.id}`,
      name: model.displayName,
      vendorLabel: provider.displayName,
      sttProvider: provider.sttProvider,
      modelId: model.id,
      credits: model.creditsPerMinute,
      streaming: provider.features?.streaming === true,
      customVocabulary: model.supportsCustomVocabulary === true,
      preview: model.previewStatus === true,
      isDefault: model.isDefault === true,
      byok: provider.access.byokEligible === true,
    })),
  );

  const actual = CLOUD_MODELS.map((model: Record<string, unknown>) => ({
    id: model.id,
    name: model.name,
    vendorLabel: model.vendorLabel,
    sttProvider: model.sttProvider,
    modelId: model.modelId,
    credits: model.credits,
    streaming: model.streaming,
    customVocabulary: model.customVocabulary,
    preview: model.preview,
    isDefault: model.isDefault,
    byok: model.byok,
  }));

  assert.deepEqual(
    actual,
    expected,
    "lib/choosing-a-model/catalog.ts has drifted from cloud-stt-catalog.json",
  );
});

test("documented language counts match the catalog", async () => {
  const { CLOUD_MODELS } = await loadCatalog();
  const catalog = readCatalog();

  for (const model of CLOUD_MODELS) {
    const provider = catalog.providers.find(
      (entry) => entry.sttProvider === model.sttProvider,
    );
    assert.ok(provider, `no catalog provider for ${model.id}`);

    const count = provider.languages?.count;
    // A vendor that publishes no number is mirrored as null, never as a guess.
    const expected = typeof count === "number" ? count : null;
    assert.equal(
      model.languages,
      expected,
      `${model.id} language count drifted from the catalog`,
    );
  }
});

test("a benchmarked model never claims an unbenchmarked basis", async () => {
  const { ALL_MODELS } = await loadCatalog();

  for (const model of ALL_MODELS) {
    if (model.wer === null) {
      assert.notEqual(
        model.accuracyBasis,
        "measured",
        `${model.id} claims a measured WER but carries none`,
      );
    } else {
      assert.notEqual(
        model.accuracyBasis,
        "none",
        `${model.id} has a WER but is marked as having no basis`,
      );
    }
  }
});

test("the budget always totals 100 points", async () => {
  const { rebalance, PRIORITIES, DEFAULT_WEIGHTS, TOTAL_POINTS } =
    await loadScoring();

  let weights = DEFAULT_WEIGHTS;
  for (const priority of PRIORITIES) {
    for (const value of [0, 17, 50, 83, 100]) {
      weights = rebalance(weights, priority, value);
      const total = PRIORITIES.reduce(
        (sum: number, key: string) => sum + weights[key],
        0,
      );
      assert.ok(
        Math.abs(total - TOTAL_POINTS) < 1e-9,
        `budget totalled ${total} after setting ${priority} to ${value}`,
      );
      assert.ok(
        Math.abs(weights[priority] - value) < 1e-9,
        `${priority} did not take the value it was given`,
      );
    }
  }
});

test("zeroing three priorities still spreads the remainder", async () => {
  const { rebalance, PRIORITIES } = await loadScoring();

  // Drive everything but accuracy to zero, then pull accuracy back down. There
  // is no proportion left to preserve, so the remainder must split evenly
  // rather than vanishing and leaving a budget that totals 40.
  let weights: Record<string, number> = {
    accuracy: 100,
    latency: 0,
    cost: 0,
    privacy: 0,
  };
  weights = rebalance(weights, "accuracy", 40);

  const total = PRIORITIES.reduce(
    (sum: number, key: string) => sum + weights[key],
    0,
  );
  assert.ok(Math.abs(total - 100) < 1e-9, `budget totalled ${total}`);
  assert.ok(weights.latency > 0, "latency stayed at zero");
});

test("privacy weighting puts an on-device model first", async () => {
  const { isDevice, modelsForPlatform } = await loadCatalog();
  const { buildPool, rankModels, NO_REQUIREMENTS } = await loadScoring();

  const pool = buildPool(
    modelsForPlatform("macos"),
    "macos",
    "english",
    NO_REQUIREMENTS,
  );
  const ranked = rankModels(pool, {
    weights: { accuracy: 25, latency: 15, cost: 10, privacy: 50 },
    measured: {},
  });

  assert.ok(ranked.length > 0, "no models ranked");
  assert.ok(
    isDevice(ranked[0].model),
    `privacy-first ranked ${ranked[0].model.name}, which is not on-device`,
  );
});

test("accuracy weighting puts the lowest error rate first", async () => {
  const { modelsForPlatform } = await loadCatalog();
  const { buildPool, rankModels, NO_REQUIREMENTS } = await loadScoring();

  const pool = buildPool(
    modelsForPlatform("macos"),
    "macos",
    "english",
    NO_REQUIREMENTS,
  );
  const ranked = rankModels(pool, {
    weights: { accuracy: 100, latency: 0, cost: 0, privacy: 0 },
    measured: {},
  });

  const lowestWer = Math.min(
    ...pool
      .map((model: { wer: number | null }) => model.wer)
      .filter((wer: number | null): wer is number => wer !== null),
  );
  assert.equal(
    ranked[0].model.wer,
    lowestWer,
    `accuracy-first ranked ${ranked[0].model.name} above the ${lowestWer}% model`,
  );
});

test("a measured clip time is reported but never rewrites the per-minute estimate", async () => {
  const { CLOUD_MODELS } = await loadCatalog();
  const { estimateSeconds, measuredClipMs } = await loadScoring();

  const model = CLOUD_MODELS.find(
    (entry: { sttProvider: string; speedFactor: number | null }) =>
      entry.sttProvider === "deepgram" && entry.speedFactor !== null,
  );
  assert.ok(model, "no benchmarked Deepgram model to test with");

  // /latency stores one attempt's wall time on a clip under 10 seconds, and the
  // clip's length is never written down. It is not seconds per audio minute and
  // must not be spent as though it were.
  const published = estimateSeconds(model);
  assert.equal(published, 60 / model.speedFactor + 0.25);

  assert.equal(measuredClipMs(model, {}), null);

  // A model-level key wins over its provider-level one, and the answer stays in
  // milliseconds — the unit it was recorded in.
  assert.equal(
    measuredClipMs(model, {
      deepgram: 900,
      [`deepgram:${model.modelId}`]: 400,
    }),
    400,
  );
});

test("measuring a model cannot move it in the ranking", async () => {
  const { CLOUD_MODELS, modelsForPlatform } = await loadCatalog();
  const { buildPool, rankModels, NO_REQUIREMENTS, PRESETS } =
    await loadScoring();

  const pool = buildPool(
    modelsForPlatform("macos"),
    "macos",
    "english",
    NO_REQUIREMENTS,
  );
  const fastest = PRESETS.find(
    (preset: { id: string }) => preset.id === "fast",
  );
  assert.ok(fastest, "no Fastest preset");

  // A plausible short-clip median for every cloud provider at once: a few
  // hundred milliseconds of round trip, which is what a sub-10-second clip
  // mostly is. Under the old per-minute reading this reordered the board and
  // pushed the genuinely fastest providers down it.
  const measured: Record<string, number> = {};
  for (const model of CLOUD_MODELS) {
    measured[model.sttProvider] = 900;
  }

  const unmeasured = rankModels(pool, {
    weights: fastest.weights,
    measured: {},
  });
  const withMeasurements = rankModels(pool, {
    weights: fastest.weights,
    measured,
  });

  assert.deepEqual(
    withMeasurements.map((entry: { model: { id: string } }) => entry.model.id),
    unmeasured.map((entry: { model: { id: string } }) => entry.model.id),
    "measured timings reordered the ranking",
  );
  assert.ok(
    withMeasurements.some(
      (entry: { measuredClipMs: number | null }) =>
        entry.measuredClipMs === 900,
    ),
    "the measurement was dropped instead of being surfaced",
  );
});

test("requirements filter the pool rather than reordering it", async () => {
  const { modelsForPlatform } = await loadCatalog();
  const { buildPool } = await loadScoring();

  const macos = modelsForPlatform("macos");
  const all = buildPool(macos, "macos", "english", {
    streaming: false,
    customVocabulary: false,
    stableOnly: false,
  });
  const stable = buildPool(macos, "macos", "english", {
    streaming: false,
    customVocabulary: false,
    stableOnly: true,
  });

  assert.ok(stable.length < all.length, "no preview models were filtered out");
  assert.ok(
    stable.every(
      (model: { placement: string; preview?: boolean }) =>
        model.placement === "device" || model.preview === false,
    ),
    "a preview model survived the stable-only filter",
  );
});

test("the live-streaming chip keeps only models the platform can stream", async () => {
  const { modelsForPlatform, isDevice } = await loadCatalog();
  const { buildPool, supportsStreaming, NO_REQUIREMENTS } = await loadScoring();

  const streaming = { ...NO_REQUIREMENTS, streaming: true };

  for (const platform of ["macos", "windows"] as const) {
    const models = modelsForPlatform(platform);
    const all = buildPool(models, platform, "english", NO_REQUIREMENTS);
    const live = buildPool(models, platform, "english", streaming);

    assert.ok(
      live.length < all.length,
      `${platform}: the streaming chip removed nothing`,
    );
    for (const model of live) {
      assert.ok(
        supportsStreaming(model, platform),
        `${platform}: ${model.id} survived the streaming chip but cannot stream there`,
      );
    }
  }

  // Windows has no local streaming provider at all — the enum in
  // Models/StreamingTranscriptionProvider.cs is five cloud vendors. Ticking
  // "Live streaming" there must leave no on-device model standing, however
  // hard the reader then weights privacy or cost.
  const windowsLive = buildPool(
    modelsForPlatform("windows"),
    "windows",
    "english",
    streaming,
  );
  assert.equal(
    windowsLive.filter(isDevice).length,
    0,
    "Windows kept an on-device model under a live-streaming requirement",
  );

  // macOS has exactly two: parakeetLocal and nemotronLocal.
  const macosLive = buildPool(
    modelsForPlatform("macos"),
    "macos",
    "english",
    streaming,
  );
  assert.deepEqual(
    macosLive
      .filter(isDevice)
      .map((model: { id: string }) => model.id)
      .sort(),
    [
      "device:nemotron-latin",
      "device:nemotron-multilingual",
      "device:parakeet-v2",
      "device:parakeet-v3",
    ],
    "the macOS local streaming set drifted from StreamingProviderStrategy.swift",
  );
});

test("the custom-vocabulary chip removes the models that ignore one", async () => {
  const { modelsForPlatform } = await loadCatalog();
  const { buildPool, supportsCustomVocabulary, NO_REQUIREMENTS } =
    await loadScoring();

  const vocab = { ...NO_REQUIREMENTS, customVocabulary: true };

  for (const platform of ["macos", "windows"] as const) {
    const models = modelsForPlatform(platform);
    const all = buildPool(models, platform, "english", NO_REQUIREMENTS);
    const kept = buildPool(models, platform, "english", vocab);

    assert.ok(
      kept.length < all.length,
      `${platform}: the custom-vocabulary chip removed nothing`,
    );
    for (const model of kept) {
      assert.ok(
        supportsCustomVocabulary(model, platform),
        `${platform}: ${model.id} survived but takes no vocabulary list there`,
      );
    }
  }

  // Windows local Whisper accepts the argument and drops it on the floor —
  // TranscriptionService.cs says so in its own comment — so a Windows reader
  // who needs a vocabulary list is left with cloud models only.
  const windowsVocab = buildPool(
    modelsForPlatform("windows"),
    "windows",
    "english",
    vocab,
  );
  assert.ok(
    windowsVocab.every(
      (model: { placement: string }) => model.placement === "cloud",
    ),
    "Windows kept an on-device model under a custom-vocabulary requirement",
  );
});

test("language breadth is a coverage rung, not a headcount", async () => {
  const { modelsForPlatform, CLOUD_MODELS } = await loadCatalog();
  const { buildPool, NO_REQUIREMENTS } = await loadScoring();

  const ids = (need: string) =>
    new Set(
      buildPool(modelsForPlatform("macos"), "macos", need, NO_REQUIREMENTS).map(
        (model: { id: string }) => model.id,
      ),
    );

  const european = ids("european");
  const wide = ids("wide");

  // Six languages, all six of them European. A minimum count of 13 cut it;
  // the question the chip asks does not.
  assert.ok(
    european.has("device:nemotron-latin"),
    "a wholly European model was cut from the European filter",
  );
  // Twenty-five European languages is not "wide multilingual", however large
  // the number looks beside a six.
  assert.ok(
    !wide.has("device:parakeet-v3"),
    "an all-European model survived the wide-multilingual filter",
  );
  // Reaches past Europe with thirty languages, so it does survive.
  assert.ok(
    wide.has("device:nemotron-multilingual"),
    "a genuinely cross-family model was cut from the wide filter",
  );

  // An unpublished count is the conservative default, not a free pass:
  // shared-app-classification/CLAUDE.md requires the UI to read it that way,
  // and every Gemini row carries a literal "unverified".
  const unpublished = CLOUD_MODELS.filter(
    (model: { languages: number | null }) => model.languages === null,
  );
  assert.ok(unpublished.length > 0, "no unpublished-count model to test with");
  for (const model of unpublished) {
    assert.equal(
      model.languageScope,
      "unknown",
      `${model.id} has no published count but is not scoped unknown`,
    );
    assert.ok(
      !wide.has(model.id) && !european.has(model.id),
      `${model.id} passed a breadth filter on a count its vendor never published`,
    );
  }

  // Every model still transcribes English, so that chip narrows nothing.
  assert.equal(
    ids("english").size,
    modelsForPlatform("macos").length,
    "the English filter dropped a model that does transcribe English",
  );
});
