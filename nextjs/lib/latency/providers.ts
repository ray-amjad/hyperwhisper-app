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

export function providerDisplayName(id: string): string {
  return PROVIDER_DISPLAY_NAMES[id] ?? id;
}