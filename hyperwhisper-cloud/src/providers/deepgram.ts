// DEEPGRAM NOVA-3 PROVIDER
// Primary STT provider - $0.0055/min, best accuracy with vocabulary boosting

import { computeDeepgramTranscriptionCost } from '../lib/cost-calculator';
import { ProviderUnavailableError } from './types';
import type { ProviderRequestContext, TranscriptionResult } from './types';
import { resolveProviderLanguage } from '../lib/language-codes';
import { fetchWithTimeout, logProviderEvent, providerHttpError, splitVocabularyTerms } from './utils';

// Maximum keywords Deepgram accepts
const MAX_KEYWORDS = 100;
const MAX_KEYWORD_CHARS = 50;

/**
 * Split an initial prompt into individual vocabulary terms.
 * Input: "HyperWhisper,SwiftUI,Claude" → ["HyperWhisper", "SwiftUI", "Claude"]
 * Deepgram's keyterm/keywords params take ONE repeated query value per term, so
 * the caller appends each term separately — never a single comma-joined string
 * (that boosts one literal phrase containing commas, which does nothing).
 */
function convertToKeyterms(initialPrompt: string): string[] {
  return splitVocabularyTerms(initialPrompt, {
    maxTerms: MAX_KEYWORDS,
    maxTermChars: MAX_KEYWORD_CHARS,
  });
}

/**
 * Map a catalog model id to Deepgram's `model` query value. The catalog spells
 * the default variant `nova-3-general` / `nova-2-general`; Deepgram expresses
 * those as the bare `nova-3` / `nova-2`. Medical variants map 1:1.
 */
function deepgramModelParam(model: string): string {
  if (model === 'nova-3-general') return 'nova-3';
  if (model === 'nova-2-general') return 'nova-2';
  return model;
}

/**
 * Build Deepgram API URL with query parameters.
 *
 * `language` is ALREADY RESOLVED — `null` means "this request runs on
 * auto-detect". Deciding the fallback here was the wrong home for it: the
 * caller then had to re-run the resolver just to learn what this function had
 * decided, and the two call sites had to agree forever for the telemetry to be
 * honest. This function now only formats.
 *
 * On the deliberate keyterm behaviour when `language` is null: Nova-3 honours
 * `keyterm` (and Nova-2 `keywords`) only in monolingual mode and silently drops
 * it under `detect_language` — see references/custom-vocab.md. The terms are
 * still appended anyway, because omitting them can only lose boosting if that
 * documented behaviour is ever narrower than stated, while sending them costs
 * nothing but URL bytes. What must NOT happen is the loss being invisible: the
 * `language_unmappable` event carries `vocabularyTermsAtRisk` so a request that
 * quietly forfeited the user's custom vocabulary is countable.
 */
function buildDeepgramUrl(model: string, language: string | null, vocabularyTerms: string[] = []): string {
  const dgModel = deepgramModelParam(model);
  const params = new URLSearchParams({
    model: dgModel,
    smart_format: 'true',
    utterances: 'true',
    mip_opt_out: 'true',
  });

  if (language) {
    params.set('language', language);
  } else {
    params.set('detect_language', 'true');
  }

  if (vocabularyTerms.length > 0) {
    // Both keyterm (Nova-3) and keywords (Nova-2) take ONE repeated query value
    // per term — `keyterm=a&keyterm=b`, NOT a comma-joined `keyterm=a,b`. A
    // single joined value boosts one literal phrase (commas and all), so the
    // boost effectively does nothing. Append each term individually.
    // Keyterm prompting is Nova-3 exclusive; Nova-2 uses the legacy `keywords`.
    const param = dgModel.startsWith('nova-3') ? 'keyterm' : 'keywords';
    for (const term of vocabularyTerms) {
      params.append(param, term);
    }
  }

  return `https://api.deepgram.com/v1/listen?${params.toString()}`;
}

/**
 * Transcribe audio with Deepgram Nova-3
 */
export async function transcribeWithDeepgram(
  audio: ArrayBuffer,
  contentType: string,
  language?: string,
  initialPrompt?: string,
  context: ProviderRequestContext = {},
): Promise<TranscriptionResult> {
  const startedAt = performance.now();
  const apiKey = process.env.DEEPGRAM_API_KEY;
  if (!apiKey) {
    throw new Error('DEEPGRAM_API_KEY not configured');
  }

  const keyterms = initialPrompt ? convertToKeyterms(initialPrompt) : [];
  const model = context.model || 'nova-3-general';
  const provider = 'deepgram';

  // Language support is per-MODEL here, not per-provider: the medical models are
  // English-only, and nova-2 predates the nova-3 language expansion while also
  // keeping two languages (`th`, `zh`) nova-3 dropped. The picker scopes its
  // language list by TIER, so switching models leaves every language selectable.
  // One call: resolves, logs the fallback if there is one, and returns what to
  // send.
  const languageSent = resolveProviderLanguage({
    provider, model, language, context, vocabularyTermCount: keyterms.length,
  });
  const url = buildDeepgramUrl(model, languageSent, keyterms);

  logProviderEvent(provider, 'prepare', {
    model,
    audioBytes: audio.byteLength,
    contentType,
    language: language || 'auto',
    languageSent: languageSent ?? 'detect',
    keytermCount: keyterms.length,
  }, context);

  const response = await fetchWithTimeout(provider, url, {
    method: 'POST',
    headers: {
      'Authorization': `Token ${apiKey}`,
      'Content-Type': contentType,
    },
    body: audio,
  }, context);

  if (!response.ok) {
    throw await providerHttpError(provider, response, startedAt, context, {
      label: 'Deepgram',
      authMessage: 'Deepgram API key is invalid or expired',
      failoverOn402: true,
    });
  }

  let data: {
    results?: {
      channels?: Array<{
        alternatives?: Array<{ transcript?: string }>;
        detected_language?: string;
      }>;
    };
    metadata?: {
      duration?: number;
      request_id?: string;
    };
  };
  try {
    data = await response.json();
  } catch {
    // A truncated/invalid 200 body (edge-proxy hiccup) is recoverable by
    // failing over, not by 500ing a request the siblings could serve.
    throw new ProviderUnavailableError('Deepgram', 'malformed 200 response body');
  }

  const channel = data.results?.channels?.[0];
  const transcript = channel?.alternatives?.[0]?.transcript || '';
  const duration = data.metadata?.duration || 0;

  if (!transcript || transcript.trim().length === 0) {
    logProviderEvent(provider, 'no_speech', {
      elapsedMs: Math.round(performance.now() - startedAt),
      detectedLanguage: channel?.detected_language,
      upstreamDurationSeconds: duration > 0 ? duration : null,
    }, context);
    return {
      text: '',
      language: channel?.detected_language,
      durationSeconds: 0,
      costUsd: 0,
      source: 'no_speech',
      requestId: data.metadata?.request_id,
    };
  }

  logProviderEvent(provider, 'success', {
    elapsedMs: Math.round(performance.now() - startedAt),
    transcriptChars: transcript.length,
    durationSeconds: duration,
    detectedLanguage: channel?.detected_language,
  }, context);

  return {
    text: transcript,
    language: channel?.detected_language,
    durationSeconds: duration,
    costUsd: computeDeepgramTranscriptionCost(duration),
    source: 'deepgram',
    requestId: data.metadata?.request_id,
  };
}
