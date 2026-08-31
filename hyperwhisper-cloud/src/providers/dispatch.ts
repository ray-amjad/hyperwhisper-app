// PROVIDER DISPATCH
// The providers layer's own answer to "which adapter runs this provider id".
//
// The transcribe route walks the fallback chain the registry hands it. Before
// this file it also had to know, for every provider in that chain, which module
// the adapter lives in and what the adapter is called — twelve deep imports and
// a hand-written id → function table, next to a second hand-written id → label
// table. Adding a provider meant editing `lib/stt-models.ts` AND the route, and
// the route's tables were private, so the deploy smoke test kept its own copy of
// the label rule.
//
// Now the route asks for a transcription by id and never names an adapter. The
// label half of the same problem lives in the registry (`servedNameFor()` /
// `formatProviderName()` in `lib/stt-models.ts`), because a public label is
// registry data rather than adapter behaviour.
//
// To add a provider: register it in `lib/stt-models.ts`, then add its row here.
// The `Record<SttProviderId, ...>` type makes a missing row a compile error.

import type { SttProviderId } from '../lib/stt-models';
import type { ProviderRequestContext, TranscriptionResult } from './types';

import { transcribeWithDeepgram } from './deepgram';
import { transcribeWithGroq } from './groq';
import { transcribeWithElevenLabs } from './elevenlabs';
import { transcribeWithXaiGrok } from './xai-stt';
import { transcribeWithAzureMai } from './azure-mai';
import { transcribeWithGoogleChirp } from './google-chirp';
import { transcribeWithOpenAI } from './openai';
import { transcribeWithGemini } from './gemini';
import { transcribeWithGeminiTranscribe } from './gemini-transcribe';
import { transcribeWithAssemblyAI } from './assemblyai';
import { transcribeWithMistral } from './mistral';
import { transcribeWithSoniox } from './soniox';

/** The one shape every STT adapter in this folder implements. */
export type TranscribeFn = (
  audio: ArrayBuffer,
  contentType: string,
  language?: string,
  initialPrompt?: string,
  context?: ProviderRequestContext,
) => Promise<TranscriptionResult>;

const PROVIDER_FN: Record<SttProviderId, TranscribeFn> = {
  deepgram: transcribeWithDeepgram,
  groq: transcribeWithGroq,
  elevenlabs: transcribeWithElevenLabs,
  grok: transcribeWithXaiGrok,
  'azure-mai': transcribeWithAzureMai,
  'google-chirp': transcribeWithGoogleChirp,
  openai: transcribeWithOpenAI,
  gemini: transcribeWithGemini,
  'gemini-transcribe': transcribeWithGeminiTranscribe,
  assemblyai: transcribeWithAssemblyAI,
  mistral: transcribeWithMistral,
  soniox: transcribeWithSoniox,
};

/**
 * Run `provider`'s adapter. The arguments and the returned
 * `TranscriptionResult` are the adapter contract in `types.ts`, unchanged — the
 * only thing this hides is which function answers.
 *
 * Every error an adapter throws still reaches the caller as thrown: the route's
 * chain logic is built on those types (`ProviderUnavailableError`,
 * `ProviderInputError`, `AudioTooLargeError`, `UnsupportedAudioFormatError`),
 * so this must never catch or rewrap.
 */
export function transcribeWithProvider(
  provider: SttProviderId,
  audio: ArrayBuffer,
  contentType: string,
  language?: string,
  initialPrompt?: string,
  context?: ProviderRequestContext,
): Promise<TranscriptionResult> {
  return PROVIDER_FN[provider](audio, contentType, language, initialPrompt, context);
}

/**
 * Whether an adapter is wired for `provider`. For the completeness test and any
 * future caller that must ask before dispatching; the route never needs it,
 * because it only ever dispatches ids the registry gave it.
 */
export function hasProviderAdapter(provider: SttProviderId): boolean {
  return typeof PROVIDER_FN[provider] === 'function';
}
