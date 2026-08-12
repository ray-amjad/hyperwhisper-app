/**
 * Backend provider id → display name.
 *
 * The source of truth is `shared-app-classification/cloud-stt-catalog.json`,
 * where each entry's `sttProvider` field is the backend id and `displayName` is
 * the name users see. That file sits outside this Next.js app's build root, so
 * the mapping is mirrored here rather than imported. The catalog's `sttProvider`
 * values match these keys 1:1 for all 11 providers.
 *
 * VENDOR ONLY — no model or version suffix. The aggregate in
 * src/content/latency.ts groups by provider and region, not by model, while
 * users can pin a per-provider model in the desktop apps and both of them pass
 * that pick through. So a Deepgram cell blends nova-3, nova-2-general and
 * nova-2-medical timings, and an OpenAI cell is mostly gpt-4o-transcribe, its
 * default. A label naming one of those models would be wrong for most of the
 * rows underneath it — and would get quietly wronger every time a provider
 * moves its default, which this codebase has already documented happening twice
 * (stt-models.ts, AssemblyAI and Soniox). The page copy states plainly that a
 * cell covers whichever model of that provider ran. The `model` column is
 * written on every row, so cutting the aggregate by model later is a query
 * change, not a data migration.
 *
 * An unmapped provider still renders — it shows its raw backend id — so a
 * provider added to the edge service appears on the page immediately, looking
 * unfinished rather than being silently dropped.
 */
export const PROVIDER_DISPLAY_NAMES: Record<string, string> = {
  deepgram: "Deepgram",
  groq: "Groq",
  elevenlabs: "ElevenLabs",
  grok: "xAI Grok",
  "azure-mai": "Microsoft MAI-Transcribe",
  "google-chirp": "Google Chirp",
  openai: "OpenAI",
  gemini: "Google Gemini",
  assemblyai: "AssemblyAI",
  mistral: "Mistral",
  soniox: "Soniox",
};

/**
 * The provider ids the ingest route stores, derived from the display map rather
 * than listed a second time.
 *
 * A hand-kept second copy drifts silently and asymmetrically: a provider added
 * to the map above renders on the page while every row the edge service reports
 * for it is rejected at ingest as an unknown provider, so the column stays
 * permanently empty and nothing says why. Deriving makes "these two lists match
 * 1:1" true by construction instead of by comment.
 *
 * The edge service keeps its own copy (hyperwhisper-cloud's `SttProviderId`) —
 * that one is justified: it is a different deployable across a service
 * boundary, which is exactly what the ingest is validating.
 */
export const KNOWN_PROVIDERS: readonly string[] = Object.keys(PROVIDER_DISPLAY_NAMES);

export function providerDisplayName(id: string): string {
  return PROVIDER_DISPLAY_NAMES[id] ?? id;
}