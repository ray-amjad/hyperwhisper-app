// DEEPGRAM NOVA-3 PROVIDER
// Primary STT provider - $0.0055/min, best accuracy with vocabulary boosting

import { computeDeepgramTranscriptionCost } from '../lib/cost-calculator';
import { ProviderInputError, ProviderUnavailableError } from './types';
import type { ProviderRequestContext, TranscriptionResult } from './types';
import { fetchWithTimeout, logProviderEvent, readErrorBodyPreview } from './utils';

// Maximum keywords Deepgram accepts
const MAX_KEYWORDS = 100;

/**
 * Split an initial prompt into individual vocabulary terms.
 * Input: "HyperWhisper,SwiftUI,Claude" → ["HyperWhisper", "SwiftUI", "Claude"]
 * Deepgram's keyterm/keywords params take ONE repeated query value per term, so
 * the caller appends each term separately — never a single comma-joined string
 * (that boosts one literal phrase containing commas, which does nothing).
 */
function convertToKeyterms(initialPrompt: string): string[] {
  return initialPrompt
    .split(/[,\n;]+/)
    .map(t => t.trim().replace(/^[-*]\s*/, ''))
    .filter(t => t.length > 0 && t.length <= 50)
    .slice(0, MAX_KEYWORDS);
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
 * Build Deepgram API URL with query parameters
 */
function buildDeepgramUrl(model: string, language?: string, vocabularyTerms: string[] = []): string {
  const dgModel = deepgramModelParam(model);
  const params = new URLSearchParams({
    model: dgModel,
    smart_format: 'true',
    utterances: 'true',
    mip_opt_out: 'true',
  });

  const isMonolingual = language && language.toLowerCase() !== 'auto';

  if (isMonolingual) {
    params.set('language', language.toLowerCase());
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
  const url = buildDeepgramUrl(model, language, keyterms);
  const provider = 'deepgram';

  logProviderEvent(provider, 'prepare', {
    model,
    audioBytes: audio.byteLength,
    contentType,
    language: language || 'auto',
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
    const errorText = await readErrorBodyPreview(response);
    const elapsedMs = Math.round(performance.now() - startedAt);
    const kind = response.status >= 500 ? 'upstream_5xx' : response.status === 429 ? 'rate_limit' : 'http_error';

    logProviderEvent(provider, 'http_error', {
      elapsedMs,
      status: response.status,
      kind,
      bodyPreview: errorText,
    }, context);

    if (response.status === 401) {
      throw new Error('Deepgram API key is invalid or expired');
    }
    if (response.status === 402) {
      // Billing exhaustion on this provider only — siblings may still have
      // budget, so fail over instead of hard-500ing the request.
      throw new ProviderUnavailableError('Deepgram', 'insufficient funds');
    }
    if (response.status === 429) {
      throw new ProviderUnavailableError('Deepgram', 'rate limit exceeded');
    }
    if (response.status >= 500) {
      throw new ProviderUnavailableError('Deepgram', `upstream 5xx: ${response.status}`);
    }

    // Other 4xx (e.g. 400 on an unaccepted language code/format) — a sibling
    // provider may accept this input, so let the chain fall through.
    throw new ProviderInputError('Deepgram', response.status, errorText || `HTTP ${response.status}`);
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
