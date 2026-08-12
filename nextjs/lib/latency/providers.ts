/**
 * Backend provider id → display name.
 *
 * The source of truth is `shared-app-classification/cloud-stt-catalog.json`,
 * where each entry's `sttProvider` field is the backend id and `displayName` is
 * the name users see. That file sits outside this Next.js app's build root, so
 * the mapping is mirrored here rather than imported. The catalog's `sttProvider`
 * values match these keys 1:1 for all 11 providers.
 *
 * An unmapped provider still renders — it shows its raw backend id — so a
 * provider added to the edge service appears on the page immediately, looking
 * unfinished rather than being silently dropped.
 */
export const PROVIDER_DISPLAY_NAMES: Record<string, string> = {
  deepgram: "Deepgram Nova 3",
  groq: "Groq Whisper",
  elevenlabs: "ElevenLabs Scribe v2",
  grok: "Grok STT",
  "azure-mai": "Microsoft MAI-Transcribe 1.5",
  "google-chirp": "Google Chirp 3",
  openai: "OpenAI Whisper",
  gemini: "Google Gemini",
  assemblyai: "AssemblyAI",
  mistral: "Mistral Voxtral",
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