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
      /** "HyperWhisper Cloud routes this model live" — see LIVE_STREAMING_ROW_IDS. */
      streaming?: boolean;
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

// ---------------------------------------------------------------------------
// The on-device mirror, and reading two native registries as data
// ---------------------------------------------------------------------------
//
// The cloud half of the mirror has been guarded since it was written, because
// cloud-stt-catalog.json is JSON and reading JSON is free. The on-device half
// was guarded by nothing at all — `DEVICE_MODELS` appeared in no test — and it
// shipped with every Windows download size copied from macOS and four shipped
// Whisper builds missing entirely.
//
// Swift and C# are not JSON, so this scrapes them. That is worth doing anyway:
// the alternative on offer was no guard, and the registries this reads are flat
// literal tables that have held their shape for the life of the files. What it
// deliberately does NOT do is parse arbitrary code. Two macOS rows —
// Qwen3 ASR and Apple Speech — have their 1-5 ratings written inline inside
// `ModelLibraryManager.swift` function bodies rather than in a rating table, so
// their ratings are unguarded and only their sizes and language sets are
// checked here. If a scrape stops matching, the failure is loud and says so;
// fix the reader rather than deleting the assertion.

function readSource(relative: string): string {
  return readFileSync(
    fileURLToPath(new URL(`../../${relative}`, import.meta.url)),
    "utf8",
  );
}

const MACOS = "app/macos/hyperwhisper";
const WINDOWS = "app/windows/HyperWhisper";

/**
 * The bracketed literal a declaration opens. `marker` must match through to and
 * including the opening bracket; nesting is counted, so inner arrays survive.
 */
function blockAfter(source: string, marker: RegExp, where: string): string {
  const found = marker.exec(source);
  assert.ok(
    found,
    `could not find ${where} — the registry moved, so this reader needs updating`,
  );
  const open = found[0][found[0].length - 1];
  const close = open === "[" ? "]" : "}";
  const start = found.index + found[0].length - 1;

  let depth = 0;
  for (let i = start; i < source.length; i += 1) {
    if (source[i] === open) depth += 1;
    else if (source[i] === close) {
      depth -= 1;
      if (depth === 0) return source.slice(start + 1, i);
    }
  }
  assert.fail(`unbalanced ${open} reading ${where}`);
}

/** `"id": (speed, accuracy)` in Swift, `["id"] = (speed, accuracy)` in C#. */
function parseRatings(block: string): Record<string, [number, number]> {
  const out: Record<string, [number, number]> = {};
  const entry = /\[?"([^"]+)"\]?\s*[:=]\s*\(\s*(\d+)\s*,\s*(\d+)\s*\)/g;
  let match: RegExpExecArray | null;
  while ((match = entry.exec(block)) !== null) {
    out[match[1]] = [Number(match[2]), Number(match[3])];
  }
  return out;
}

/** Every quoted string in a block, in order. Used for flat code lists. */
function quotedStrings(block: string): string[] {
  return Array.from(block.matchAll(/"([^"]*)"/g), (match) => match[1]);
}

/** The keys of a Swift `[String: String]` literal. */
function dictionaryKeys(block: string): string[] {
  return Array.from(block.matchAll(/"([^"]+)"\s*:/g), (match) => match[1]);
}

/** `static let someName = "value"` in Swift. */
function swiftConstant(source: string, name: string): string {
  const found = new RegExp(`static let ${name}\\s*=\\s*"([^"]*)"`).exec(source);
  assert.ok(found, `no Swift constant ${name}`);
  return found[1];
}

type RegistryEntry = { size: string; codes?: string[] };

/** macOS: the flat registries, keyed by the id each app uses internally. */
function readMacosRegistry(): Record<string, RegistryEntry> {
  const whisperSource = readSource(
    `${MACOS}/Managers/Transcription/Models/WhisperModelManager.swift`,
  );
  const parakeetSource = readSource(
    `${MACOS}/Managers/Transcription/Models/ParakeetModelManager.swift`,
  );
  const nemotronSource = readSource(
    `${MACOS}/Managers/Transcription/Models/NemotronModelManager.swift`,
  );
  const qwen3Source = readSource(
    `${MACOS}/Managers/Transcription/Models/Qwen3AsrModelManager.swift`,
  );
  const languageSource = readSource(
    `${MACOS}/Views/Modes/Components/ModeLanguageSettings.swift`,
  );

  const entries: Record<string, RegistryEntry> = {};

  // `"tiny": ("39 MB", 39_000_000),`
  const sizes = blockAfter(
    whisperSource,
    /let modelSizes: \[String: \(String, Int64\)\] = \[/,
    "macOS Whisper modelSizes",
  );
  // Array.from rather than iterating the match iterator directly: this tsconfig
  // targets es5, where a for-of over one is a type error (TS2802).
  for (const match of Array.from(
    sizes.matchAll(/"([^"]+)":\s*\(\s*"([^"]+)"/g),
  )) {
    entries[match[1]] = { size: match[2] };
  }

  entries["parakeet-tdt-0.6b-v2"] = {
    size: swiftConstant(parakeetSource, "v2SizeDescription"),
    codes: dictionaryKeys(
      blockAfter(
        parakeetSource,
        /v2Languages: \[String: String\] = \[/,
        "macOS Parakeet v2 languages",
      ),
    ),
  };
  entries["parakeet-tdt-0.6b-v3"] = {
    size: swiftConstant(parakeetSource, "v3SizeDescription"),
    codes: dictionaryKeys(
      blockAfter(
        parakeetSource,
        /v3Languages: \[String: String\] = \[/,
        "macOS Parakeet v3 languages",
      ),
    ),
  };
  entries["nemotron-asr-3.5-latin"] = {
    size: swiftConstant(nemotronSource, "latinSize"),
    codes: dictionaryKeys(
      blockAfter(
        nemotronSource,
        /latinLanguages: \[String: String\] = \[/,
        "macOS Nemotron latin languages",
      ),
    ),
  };
  entries["nemotron-asr-3.5-multilingual"] = {
    size: swiftConstant(nemotronSource, "multilingualSize"),
    codes: dictionaryKeys(
      blockAfter(
        nemotronSource,
        /multilingualLanguages: \[String: String\] = \[/,
        "macOS Nemotron multilingual languages",
      ),
    ),
  };
  // The two picker lists lead with `LanguageData.automaticCode`, which is a
  // symbol rather than a quoted code, so it does not survive `quotedStrings`.
  entries["qwen3-asr-0.6b"] = {
    size: swiftConstant(qwen3Source, "sizeDescription"),
    codes: quotedStrings(
      blockAfter(
        languageSource,
        /qwen3AsrLanguageCodes: \[String\] = \[/,
        "macOS Qwen3 language codes",
      ),
    ),
  };
  entries["apple-speech-analyzer"] = {
    size: "Built-in",
    codes: quotedStrings(
      blockAfter(
        languageSource,
        /speechAnalyzerLanguageCodes: \[String\] = \[/,
        "macOS Apple Speech language codes",
      ),
    ),
  };

  return entries;
}

/** Windows: `WhisperModelInfo.AllModels` and `ParakeetModelInfo.AllModels`. */
function readWindowsRegistry(): Record<string, RegistryEntry> {
  const entries: Record<string, RegistryEntry> = {};

  // `new WhisperModelInfo("tiny", "Tiny", "78 MB", false, …)`
  const whisper = blockAfter(
    readSource(`${WINDOWS}/Models/WhisperModelInfo.cs`),
    /AllModels = new\[\]\s*\{/,
    "Windows WhisperModelInfo.AllModels",
  );
  for (const match of Array.from(
    whisper.matchAll(
      /new WhisperModelInfo\(\s*"([^"]+)",\s*"[^"]*",\s*"([^"]+)"/g,
    ),
  )) {
    entries[match[1]] = { size: match[2] };
  }

  // Named arguments over several lines, so each `new ParakeetModelInfo(` opens
  // a chunk that is read for the three fields the page mirrors.
  const parakeet = blockAfter(
    readSource(`${WINDOWS}/Models/ParakeetModelInfo.cs`),
    /ParakeetModelInfo\[\] AllModels\s*=\s*\[/,
    "Windows ParakeetModelInfo.AllModels",
  );
  for (const chunk of parakeet.split("new ParakeetModelInfo(").slice(1)) {
    const id = /id:\s*"([^"]+)"/.exec(chunk);
    const size = /size:\s*"([^"]+)"/.exec(chunk);
    const codes = /supportedLanguages:\s*\[([^\]]*)\]/.exec(chunk);
    assert.ok(id && size && codes, "unreadable ParakeetModelInfo entry");
    entries[id[1]] = { size: size[1], codes: quotedStrings(codes[1]) };
  }

  return entries;
}

function readRatings(): {
  macos: Record<string, [number, number]>;
  windows: Record<string, [number, number]>;
} {
  const swift = readSource(`${MACOS}/Managers/ModelLibraryManager.swift`);
  const csharp = readSource(`${WINDOWS}/Services/ModelLibraryManager.cs`);
  const table = /: \[String: \(speed: Int, accuracy: Int\)\] = \[/;

  return {
    macos: {
      ...parseRatings(
        blockAfter(
          swift,
          new RegExp(`whisperRatings${table.source}`),
          "macOS whisperRatings",
        ),
      ),
      ...parseRatings(
        blockAfter(
          swift,
          new RegExp(`parakeetRatings${table.source}`),
          "macOS parakeetRatings",
        ),
      ),
      ...parseRatings(
        blockAfter(
          swift,
          new RegExp(`nemotronRatings${table.source}`),
          "macOS nemotronRatings",
        ),
      ),
    },
    windows: {
      ...parseRatings(
        blockAfter(csharp, /WhisperRatings = new\(\)\s*\{/, "WhisperRatings"),
      ),
      ...parseRatings(
        blockAfter(csharp, /ParakeetRatings = new\(\)\s*\{/, "ParakeetRatings"),
      ),
    },
  };
}

/**
 * Mirror id to the id each app's registry uses. Hand-written because the two
 * apps genuinely disagree — macOS spells turbo `large-v3_turbo` with an
 * underscore and Windows `large-v3-turbo` with a hyphen, and the Parakeet
 * family is named after upstream weights on macOS and after the product on
 * Windows. A model missing from this table on a platform it lists is a failure,
 * which is what catches a row the page forgot to add.
 */
const REGISTRY_IDS: Record<
  string,
  Partial<Record<"macos" | "windows", string>>
> = {
  "device:whisper-large-v3-turbo": { macos: "large-v3_turbo", windows: "large-v3-turbo" },
  "device:whisper-large-v3": { macos: "large-v3", windows: "large-v3" },
  "device:whisper-large-v2": { macos: "large-v2", windows: "large-v2" },
  "device:whisper-medium": { macos: "medium", windows: "medium" },
  "device:whisper-medium-en": { macos: "medium.en", windows: "medium.en" },
  "device:whisper-small": { macos: "small", windows: "small" },
  "device:whisper-small-en": { macos: "small.en", windows: "small.en" },
  "device:whisper-base": { macos: "base", windows: "base" },
  "device:whisper-base-en": { macos: "base.en", windows: "base.en" },
  "device:whisper-tiny": { macos: "tiny", windows: "tiny" },
  "device:whisper-tiny-en": { macos: "tiny.en", windows: "tiny.en" },
  "device:parakeet-v3": { macos: "parakeet-tdt-0.6b-v3", windows: "parakeet-v3" },
  "device:parakeet-v2": { macos: "parakeet-tdt-0.6b-v2", windows: "parakeet-v2" },
  "device:nemotron-multilingual": { macos: "nemotron-asr-3.5-multilingual" },
  "device:nemotron-latin": { macos: "nemotron-asr-3.5-latin" },
  "device:nemotron-streaming": { windows: "nemotron-3.5-ml-560ms" },
  "device:qwen3-asr": { macos: "qwen3-asr-0.6b" },
  "device:qwen3-asr-0.6b": { windows: "qwen3-asr-0.6b" },
  "device:apple-speech": { macos: "apple-speech-analyzer" },
};

/** What the page shows a reader on this platform. */
function shownSize(
  model: { size: string; sizeWindows?: string },
  platform: string,
): string {
  return platform === "windows" && model.sizeWindows
    ? model.sizeWindows
    : model.size;
}

/**
 * The rows HyperWhisper can transcribe LIVE, and the route that proves each one.
 *
 * Held here rather than derived from the catalog, because the catalog cannot
 * answer this question. Its `features.streaming` is a vendor-level hint —
 * `CloudSTTCatalog.swift` calls it "true for six vendors we serve no WebSocket
 * route for" — and deriving the column from it published a live claim for
 * AssemblyAI, Mistral and Soniox, and for the pre-recorded
 * `gemini-3.5-transcribe`.
 *
 * Its per-model `streaming` flag cannot answer it either, and means something
 * narrower than it reads: "HyperWhisper Cloud routes this model live". The
 * assertion below holds this list to that flag in the one direction that is
 * true — a model the Cloud routes live must be a row this page calls live — so
 * a new live model landing in the catalog fails here. The reverse does not
 * hold: a row can be live without the Cloud flag, if a BYOK route reaches it.
 *
 * A hand-kept list is the cost of a fact that lives in Swift, C# and Rust
 * sources a test cannot usefully parse. Keep the citation on every entry: it is
 * what makes the next edit checkable.
 */
const LIVE_STREAMING_ROW_IDS = new Set([
  // The two ids the live Deepgram pickers offer: `StreamingView.swift` on
  // macOS, and `SettingsService.StreamingDeepgramModel` on Windows, which
  // rewrites anything else to `nova-3-general`. `ws-streaming-deepgram.ts`
  // hard-codes `model: 'nova-3'` besides. The Nova 2 rows are batch-only, which
  // is why a route that forwards a model id is not on its own a live claim.
  "deepgramNova3:nova-3-general",
  "deepgramNova3:nova-3-medical",
  // `XAIStreamingStrategy` builds a wss://api.x.ai URL with no model parameter,
  // which is also why this row's model id is the empty string.
  "grokStt:",
  // `LIVE_MODEL` in hw-net `providers/gemini_transcribe.rs`, substituted by
  // `live/gemini.rs` and `GeminiStreamingStrategy` and proxied by
  // `ws-streaming-gemini-transcribe.ts`. The pre-recorded row is deliberately
  // absent: it is the Interactions API model, and no caller sends its id live.
  "geminiTranscribe:gemini-3.5-transcribe-live",
  // No OpenAI or ElevenLabs row. Both have live routes, but they run
  // `gpt-realtime-whisper` and `scribe_v2_realtime`, ids this page does not
  // list. `gpt-live-transcribe` is `IsAvailable = false` in
  // `CloudTranscriptionModel.cs` and runs on neither the batch nor the live path.
]);

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
      // The one column the catalog does not decide. See LIVE_STREAMING_ROW_IDS.
      streaming: LIVE_STREAMING_ROW_IDS.has(`${provider.id}:${model.id}`),
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

test("every model the Cloud routes live is a row the page calls live", () => {
  const catalog = readCatalog();

  const cloudLive = catalog.providers.flatMap((provider) =>
    provider.models
      .filter((model) => model.streaming === true)
      .map((model) => `${provider.id}:${model.id}`),
  );

  // Deliberately one-directional. A row can stream under BYOK without the Cloud
  // routing it — all four Deepgram rows do, and only two carry the flag — so
  // the page's list is a superset, never an equality.
  assert.ok(cloudLive.length > 0, "no catalog model carries `streaming: true`");
  for (const id of cloudLive) {
    assert.ok(
      LIVE_STREAMING_ROW_IDS.has(id),
      `cloud-stt-catalog.json routes ${id} live, but LIVE_STREAMING_ROW_IDS at the top of this ` +
        `file does not list it, so /choosing-a-model shows it as not live. Add it there with the ` +
        `route that runs it, and set \`streaming: true\` on its row in lib/choosing-a-model/catalog.ts.`,
    );
  }
});

test("no row is live for a vendor whose live route runs none of its rows", async () => {
  const { CLOUD_MODELS } = await loadCatalog();

  /**
   * Vendors where NO row on this page is reachable live, for either of the two
   * reasons the vendor hint conflated. Named rather than derived: deriving the
   * expectation from the same list the mirror uses would assert nothing.
   *
   *  - `assemblyai`, `mistral`, `soniox` — no live route exists at all, though
   *    `features.streaming` is true for all three.
   *  - `openai`, `elevenlabs` — a live route exists, but it runs
   *    `gpt-realtime-whisper` / `scribe_v2_realtime`, which are not rows here.
   */
  const noLiveRow = ["assemblyai", "mistral", "soniox", "openai", "elevenlabs"];
  for (const model of CLOUD_MODELS as {
    id: string;
    sttProvider: string;
    streaming: boolean;
  }[]) {
    if (!noLiveRow.includes(model.sttProvider)) continue;
    assert.equal(
      model.streaming,
      false,
      `${model.id} is marked live, but no live route in this app reaches it. If one shipped — a new ` +
        `route, or a picker that can now select this model — add the row to LIVE_STREAMING_ROW_IDS ` +
        `with that source, and drop its provider from this list.`,
    );
  }
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

test("every on-device model both apps ship is on the page, and no others", async () => {
  const { DEVICE_MODELS } = await loadCatalog();

  const registries: Record<string, Record<string, RegistryEntry>> = {
    macos: readMacosRegistry(),
    windows: readWindowsRegistry(),
  };

  for (const platform of ["macos", "windows"] as const) {
    const mirrored = new Set(
      DEVICE_MODELS.filter((model: { platforms: readonly string[] }) =>
        model.platforms.includes(platform),
      ).map((model: { id: string }) => {
        const registryId = REGISTRY_IDS[model.id]?.[platform];
        assert.ok(
          registryId,
          `${model.id} claims to ship on ${platform} but REGISTRY_IDS has no id for it there`,
        );
        return registryId;
      }),
    );

    assert.deepEqual(
      Array.from(mirrored).sort(),
      Object.keys(registries[platform]).sort(),
      `the ${platform} device list has drifted from the app's model registry`,
    );
  }
});

test("on-device download sizes match each platform's own registry", async () => {
  const { DEVICE_MODELS } = await loadCatalog();

  const registries: Record<string, Record<string, RegistryEntry>> = {
    macos: readMacosRegistry(),
    windows: readWindowsRegistry(),
  };

  for (const model of DEVICE_MODELS) {
    for (const platform of model.platforms as readonly ("macos" | "windows")[]) {
      const registryId = REGISTRY_IDS[model.id]?.[platform];
      assert.ok(registryId, `no ${platform} registry id for ${model.id}`);
      assert.equal(
        shownSize(model, platform),
        registries[platform][registryId].size,
        `${model.id} shows the wrong download size to a ${platform} reader`,
      );
    }
  }
});

test("on-device speed and accuracy ratings match both apps' rating tables", async () => {
  const { DEVICE_MODELS } = await loadCatalog();
  const ratings = readRatings();

  // Ratings the apps write inline rather than in a rating table; see the note
  // above readSource. Their sizes and language sets are still guarded.
  const UNTABLED = new Set(["device:qwen3-asr", "device:apple-speech"]);

  let checked = 0;
  for (const model of DEVICE_MODELS) {
    if (UNTABLED.has(model.id)) continue;
    for (const platform of model.platforms as readonly ("macos" | "windows")[]) {
      const registryId = REGISTRY_IDS[model.id]?.[platform];
      assert.ok(registryId, `no ${platform} registry id for ${model.id}`);
      const rating = ratings[platform][registryId];
      assert.ok(
        rating,
        `${registryId} has no entry in the ${platform} rating table`,
      );
      assert.deepEqual(
        [model.speedRating, model.accuracyRating],
        rating,
        `${model.id} ratings drifted from the ${platform} app`,
      );
      checked += 1;
    }
  }
  assert.ok(checked > 20, `only ${checked} ratings were checked`);
});

test("on-device language scopes are the rung the registries' code lists earn", async () => {
  const { DEVICE_MODELS, scopeForCodes } = await loadCatalog();

  const registries: Record<string, Record<string, RegistryEntry>> = {
    macos: readMacosRegistry(),
    windows: readWindowsRegistry(),
  };

  // Whisper's registries declare no code list — the multilingual builds are
  // flagged "supports all languages" and the .en builds are pinned to English
  // by their id — so the two ends are asserted by shape instead.
  let derived = 0;
  for (const model of DEVICE_MODELS) {
    for (const platform of model.platforms as readonly ("macos" | "windows")[]) {
      const registryId = REGISTRY_IDS[model.id]?.[platform];
      assert.ok(registryId, `no ${platform} registry id for ${model.id}`);

      if (registryId.startsWith("large-") || /^(tiny|base|small|medium)$/.test(registryId)) {
        assert.equal(
          model.languageScope,
          "wide",
          `${model.id} is a multilingual Whisper build but is not scoped wide`,
        );
        continue;
      }
      if (registryId.endsWith(".en")) {
        assert.equal(
          model.languageScope,
          "narrow",
          `${model.id} is an English-only build but is not scoped narrow`,
        );
        assert.equal(model.languages, 1, `${model.id} is English-only`);
        continue;
      }

      const codes = registries[platform][registryId].codes;
      assert.ok(codes, `${registryId} declares no language codes on ${platform}`);
      assert.equal(
        model.languageScope,
        scopeForCodes(codes),
        `${model.id} scope drifted from the ${platform} registry's ${codes.length} codes`,
      );
      derived += 1;
    }
  }
  assert.ok(derived >= 8, `only ${derived} scopes were derived from a code list`);
});

test("on-device capability flags stay inside what the platform ships", async () => {
  const { DEVICE_MODELS } = await loadCatalog();
  const catalog = (
    JSON.parse(readSource("shared-models/models-catalog.json")) as {
      models: {
        provider: string;
        id: string;
        supportsCustomVocabulary?: boolean;
      }[];
    }
  ).models;

  const seen = new Set<string>();
  for (const model of DEVICE_MODELS) {
    assert.ok(!seen.has(model.id), `duplicate device id ${model.id}`);
    seen.add(model.id);
    assert.ok(model.platforms.length > 0, `${model.id} ships nowhere`);

    for (const rating of [model.speedRating, model.accuracyRating]) {
      assert.ok(
        Number.isInteger(rating) && rating >= 1 && rating <= 5,
        `${model.id} has a rating outside 1-5`,
      );
    }

    // A capability cannot be claimed on a platform the model is not on.
    for (const platform of [
      ...model.streamingPlatforms,
      ...model.customVocabularyPlatforms,
    ]) {
      assert.ok(
        model.platforms.includes(platform),
        `${model.id} claims a capability on ${platform}, where it does not ship`,
      );
    }
  }

  // Models/StreamingTranscriptionProvider.cs has five members and every one is
  // a cloud vendor. Nothing on-device streams on Windows.
  const windowsEnum = blockAfter(
    readSource(`${WINDOWS}/Models/StreamingTranscriptionProvider.cs`),
    /enum StreamingTranscriptionProvider\s*\{/,
    "Windows StreamingTranscriptionProvider",
  );
  assert.ok(
    !/local/i.test(windowsEnum),
    "Windows gained a local streaming provider — the device mirror must follow",
  );
  for (const model of DEVICE_MODELS) {
    assert.ok(
      !model.streamingPlatforms.includes("windows"),
      `${model.id} claims Windows streaming, which the app has no provider for`,
    );
  }

  // macOS exposes exactly the two the Swift enum calls local.
  const macosLocal = blockAfter(
    readSource(
      `${MACOS}/Managers/AudioRecording/Streaming/Protocols/StreamingProviderStrategy.swift`,
    ),
    /var isLocal: Bool \{/,
    "macOS isLocal",
  );
  const localCases = Array.from(
    macosLocal.matchAll(/case ([.\w, ]+): return true/g),
  ).flatMap((match) =>
    match[1].split(",").map((entry) => entry.trim().replace(/^\./, "")),
  );
  assert.deepEqual(
    localCases.sort(),
    ["nemotronLocal", "parakeetLocal"],
    "the macOS local streaming providers changed",
  );

  // Custom vocabulary on macOS is the shared catalog's per-provider wildcard.
  const vocabulary = (provider: string) =>
    catalog.find((row) => row.provider === provider && row.id === "*")
      ?.supportsCustomVocabulary === true;

  const PROVIDER_OF: Record<string, string> = {
    Whisper: "localWhisper",
    Parakeet: "parakeet",
    Nemotron: "nemotron",
    Qwen3: "qwen3ASR",
    Apple: "appleSpeech",
  };
  for (const model of DEVICE_MODELS) {
    if (!model.platforms.includes("macos")) continue;
    const provider = PROVIDER_OF[model.vendorLabel];
    assert.ok(provider, `no catalog provider for vendor ${model.vendorLabel}`);
    assert.equal(
      model.customVocabularyPlatforms.includes("macos"),
      vocabulary(provider),
      `${model.id} disagrees with models-catalog.json about custom vocabulary`,
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
