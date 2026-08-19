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

  const pool = buildPool(modelsForPlatform("macos"), "english", NO_REQUIREMENTS);
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

  const pool = buildPool(modelsForPlatform("macos"), "english", NO_REQUIREMENTS);
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

test("a measured timing beats the published speed factor", async () => {
  const { CLOUD_MODELS } = await loadCatalog();
  const { estimateSeconds } = await loadScoring();

  const model = CLOUD_MODELS.find(
    (entry: { sttProvider: string; speedFactor: number | null }) =>
      entry.sttProvider === "deepgram" && entry.speedFactor !== null,
  );
  assert.ok(model, "no benchmarked Deepgram model to test with");

  const published = estimateSeconds(model, {});
  assert.equal(published.isMeasured, false);

  // A model-level key wins over its provider-level one.
  const measured = estimateSeconds(model, {
    deepgram: 900,
    [`deepgram:${model.modelId}`]: 400,
  });
  assert.equal(measured.isMeasured, true);
  assert.equal(measured.seconds, 0.4);
});

test("requirements filter the pool rather than reordering it", async () => {
  const { modelsForPlatform } = await loadCatalog();
  const { buildPool } = await loadScoring();

  const macos = modelsForPlatform("macos");
  const all = buildPool(macos, "english", {
    streaming: false,
    customVocabulary: false,
    stableOnly: false,
  });
  const stable = buildPool(macos, "english", {
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
