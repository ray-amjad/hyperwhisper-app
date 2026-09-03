/**
 * The names the /latency page prints, mirrored from the catalog the desktop
 * apps read.
 *
 * The source of truth is `shared-app-classification/cloud-stt-catalog.json`.
 * That file sits outside this Next.js app's build root, so it is copied here
 * rather than imported; `tests/latency-report-validation.test.ts` reads the real
 * catalog as data and fails when this copy drifts from it.
 *
 * Two names are mirrored, and they are the two the apps show:
 *
 *  - `vendorDisplayName` — the app's Provider dropdown. A row on the page is a
 *    vendor, so it is named the way the app names it. Chirp and Gemini both
 *    carry `vendor: "google"`, so the app collapses them into one **Google**
 *    row and picking a Gemini model under it is what switches the backend
 *    provider (macOS `CloudSTTCatalog.cloudTierVendorGroups` /
 *    `tierOwningModel`). The page does the same, so a visitor comparing the page
 *    against their own Provider dropdown sees one list, not two spellings of
 *    one.
 *  - a model's `displayName` — the app's Model dropdown, printed on the model
 *    rows the page shows when "Break down by model" is on.
 *
 * Names are never invented here. The page used to keep its own vendor-only
 * labels ("Microsoft MAI-Transcribe", "xAI Grok") because a provider row blends
 * every model of that provider and a label naming one model would be wrong for
 * most of the rows under it. That reasoning still holds — it is why the vendor
 * row is a vendor NAME and the model rows underneath are where models get named
 * — but the labels themselves now come from the catalog, so the page cannot
 * drift from the app by an edit in one place.
 *
 * An unmapped provider or model still renders — it shows its raw backend id — so
 * something added to the edge service appears on the page immediately, looking
 * unfinished rather than being silently dropped.
 */
export type CatalogModel = {
  /** The model id the edge service reports. Empty when the provider takes none. */
  id: string;
  displayName: string;
  isDefault?: boolean;
};

export type CatalogEntry = {
  /** Backend provider id, as stored in `stt_latency_samples.provider`. */
  sttProvider: string;
  /** Catalog `vendor` key — what collapses Chirp and Gemini into one row. */
  vendor: string;
  vendorDisplayName: string;
  /** In catalog order, which is the order the app's Model dropdown lists them. */
  models: readonly CatalogModel[];
};

/** Mirrors `cloud-stt-catalog.json`'s `providers[]`, in catalog order. */
export const STT_CATALOG: readonly CatalogEntry[] = [
  {
    sttProvider: "groq",
    vendor: "groq",
    vendorDisplayName: "Groq",
    models: [
      { id: "whisper-large-v3-turbo", displayName: "Whisper Large v3 Turbo", isDefault: true },
      { id: "whisper-large-v3", displayName: "Whisper Large v3" },
    ],
  },
  {
    sttProvider: "deepgram",
    vendor: "deepgram",
    vendorDisplayName: "Deepgram",
    models: [
      { id: "nova-3-general", displayName: "Nova 3 General", isDefault: true },
      { id: "nova-3-medical", displayName: "Nova 3 Medical" },
      { id: "nova-2-general", displayName: "Nova 2 General" },
      { id: "nova-2-medical", displayName: "Nova 2 Medical" },
    ],
  },
  {
    sttProvider: "grok",
    vendor: "xai",
    vendorDisplayName: "SpaceXAI",
    models: [
      // Empty id, like the catalog: the endpoint takes no model parameter, so
      // the ingest stores null and modelDisplayName() normalises the two.
      { id: "", displayName: "Grok Speech-to-Text", isDefault: true },
    ],
  },
  {
    sttProvider: "azure-mai",
    vendor: "azure",
    vendorDisplayName: "Microsoft",
    models: [{ id: "mai-transcribe-1.5", displayName: "MAI-Transcribe 1.5", isDefault: true }],
  },
  {
    sttProvider: "gemini-transcribe",
    vendor: "google",
    vendorDisplayName: "Google",
    models: [
      { id: "gemini-3.5-transcribe", displayName: "Gemini 3.5 Transcribe", isDefault: true },
      { id: "gemini-3.5-transcribe-live", displayName: "Gemini 3.5 Transcribe Live" },
    ],
  },
  {
    sttProvider: "elevenlabs",
    vendor: "elevenlabs",
    vendorDisplayName: "ElevenLabs",
    models: [{ id: "scribe_v2", displayName: "Scribe v2", isDefault: true }],
  },
  {
    sttProvider: "openai",
    vendor: "openai",
    vendorDisplayName: "OpenAI",
    models: [
      { id: "gpt-4o-transcribe", displayName: "GPT-4o Transcribe", isDefault: true },
      { id: "gpt-4o-mini-transcribe", displayName: "GPT-4o Mini Transcribe" },
      { id: "whisper-1", displayName: "Whisper" },
      { id: "gpt-transcribe", displayName: "GPT Transcribe" },
      { id: "gpt-live-transcribe", displayName: "GPT Live Transcribe" },
    ],
  },
  {
    sttProvider: "assemblyai",
    vendor: "assemblyai",
    vendorDisplayName: "AssemblyAI",
    models: [
      { id: "universal-3-5-pro", displayName: "Universal-3.5 Pro", isDefault: true },
      { id: "universal-2", displayName: "Universal-2" },
    ],
  },
  {
    sttProvider: "mistral",
    vendor: "mistral",
    vendorDisplayName: "Mistral",
    models: [{ id: "voxtral-mini-latest", displayName: "Voxtral Mini", isDefault: true }],
  },
  {
    sttProvider: "soniox",
    vendor: "soniox",
    vendorDisplayName: "Soniox",
    models: [
      { id: "stt-async-v5", displayName: "Async v5", isDefault: true },
    ],
  },
  {
    sttProvider: "gemini",
    vendor: "google",
    vendorDisplayName: "Google",
    models: [
      { id: "gemini-2.5-flash", displayName: "Gemini 2.5 Flash", isDefault: true },
      { id: "gemini-2.5-flash-lite", displayName: "Gemini 2.5 Flash Lite" },
      { id: "gemini-2.5-pro", displayName: "Gemini 2.5 Pro" },
      { id: "gemini-3-flash-preview", displayName: "Gemini 3 Flash" },
      { id: "gemini-3.1-pro-preview", displayName: "Gemini 3.1 Pro" },
    ],
  },
  {
    sttProvider: "meta",
    vendor: "meta",
    vendorDisplayName: "Meta",
    models: [
      { id: "muse-voice-transcribe-1.0", displayName: "Muse Voice Transcribe 1.0", isDefault: true },
    ],
  },
];

const ENTRY_BY_PROVIDER = new Map(STT_CATALOG.map((entry) => [entry.sttProvider, entry]));

/**
 * The provider ids the ingest route stores, derived from the catalog mirror
 * rather than listed a second time.
 *
 * A hand-kept second copy drifts silently and asymmetrically: a provider added
 * to the mirror above renders on the page while every row the edge service
 * reports for it is rejected at ingest as an unknown provider, so the row stays
 * permanently empty and nothing says why. Deriving makes "these two lists match
 * 1:1" true by construction instead of by comment.
 *
 * The edge service keeps its own copy (hyperwhisper-cloud's `SttProviderId`) —
 * that one is justified: it is a different deployable across a service
 * boundary, which is exactly what the ingest is validating.
 */
export const KNOWN_PROVIDERS: readonly string[] = STT_CATALOG.map(
  (entry) => entry.sttProvider,
);

/**
 * Providers the page no longer draws, even though the window still holds their
 * rows.
 *
 * This is NOT the same as "absent from STT_CATALOG". An unmapped provider
 * renders under its raw backend id on purpose — that is how something added to
 * the edge service shows up here immediately, looking unfinished rather than
 * being silently dropped (see the file header). A RETIRED provider is the
 * opposite case: the apps dropped it, no client can select it any more, and its
 * stored rows are history. Drawing it invites a visitor to shop for a provider
 * their app does not offer, and a raw id like `google-chirp` reads as a row
 * someone forgot to finish rather than one deliberately left behind.
 *
 * Nothing is deleted. The rows stay for the retention the privacy page states,
 * WINDOW_DAYS ages them out of the aggregate on its own, and this list only
 * stops them being drawn in the meantime. Ingest already refuses new rows for
 * them, because KNOWN_PROVIDERS is derived from the catalog above.
 *
 *  - `google-chirp` — Google Cloud Speech-to-Text V2 Chirp 3. Replaced by
 *    geminiTranscribe as the Google HyperWhisper Cloud tier at catalog v8
 *    (2026-08-27); the app's picker has not offered it since.
 */
export const RETIRED_PROVIDERS: readonly string[] = ["google-chirp"];

/** The name the app's Provider dropdown shows for a vendor key. */
export function vendorDisplayName(vendorKey: string): string {
  const entry = STT_CATALOG.find((candidate) => candidate.vendor === vendorKey);
  return entry?.vendorDisplayName ?? vendorKey;
}

/**
 * The name the app's Model dropdown shows for one stored row's model.
 *
 * A null model and an empty one are the same thing — the ingest stores null for
 * a provider whose endpoint takes no model id, and the catalog spells that same
 * model with an empty id — so both resolve to the provider's single entry rather
 * than to an unnamed row.
 */
export function modelDisplayName(providerId: string, modelId: string | null): string {
  const id = modelId ?? "";
  const model = ENTRY_BY_PROVIDER.get(providerId)?.models.find(
    (candidate) => candidate.id === id,
  );
  return model?.displayName ?? (id === "" ? providerId : id);
}

/**
 * Whether a stored row's model is the one a fresh pick of its VENDOR lands on.
 *
 * Scoped to the vendor, not the entry, because a row on the page is a vendor.
 * Google owns two entries and each marks a default of its own —
 * gemini-3.5-transcribe and gemini-2.5-flash — but selecting Google in the app
 * lands on the vendor group's
 * first entry in catalog order and then on that entry's default
 * (`VendorGroup.defaultEntry` / `CloudSTTCatalog.defaultModel`), so exactly one
 * of the two is what a visitor would actually get. Badging both would tell them
 * a choice exists that the app never offers.
 */
export function isDefaultModel(providerId: string, modelId: string | null): boolean {
  const entry = ENTRY_BY_PROVIDER.get(providerId);
  if (!entry) return false;
  const first = STT_CATALOG.find((candidate) => candidate.vendor === entry.vendor);
  if (!first || first.sttProvider !== providerId) return false;
  const fallsBackTo = first.models.find((model) => model.isDefault) ?? first.models[0];
  return fallsBackTo?.id === (modelId ?? "");
}

/**
 * Catalog position of one (provider, model) pair, used to order the model rows
 * under a vendor exactly as the app's Model dropdown orders them. A pair the
 * catalog does not know sorts last, so a model the edge service starts reporting
 * before this mirror is updated appears at the bottom of its vendor instead of
 * silently taking someone else's place.
 */
export function modelSortIndex(providerId: string, modelId: string | null): number {
  const id = modelId ?? "";
  let index = 0;
  for (const entry of STT_CATALOG) {
    for (const model of entry.models) {
      if (entry.sttProvider === providerId && model.id === id) return index;
      index += 1;
    }
  }
  return Number.MAX_SAFE_INTEGER;
}
