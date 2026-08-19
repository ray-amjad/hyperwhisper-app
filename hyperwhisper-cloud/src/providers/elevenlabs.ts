// ELEVENLABS SCRIBE PROVIDER
// High accuracy STT - $0.00983/min using Scribe v2

import { computeElevenLabsTranscriptionCost } from '../lib/cost-calculator';
import { resolveProviderLanguage } from '../lib/language-codes';
import { ProviderUnavailableError } from './types';
import type { ProviderRequestContext, TranscriptionResult } from './types';
import {
  audioExtensionFromContentType,
  fetchWithTimeout,
  logProviderEvent,
  providerHttpError,
  splitVocabularyTerms,
} from './utils';

// ElevenLabs treats mp3 as the *fallback* container rather than one it matches
// on, so an `mp3`/`mpeg` hint never wins over another container named in the
// same content type. Leaving mp3 out of the match set preserves that.
const ELEVENLABS_AUDIO_EXTENSIONS = ['wav', 'm4a', 'webm', 'ogg', 'flac'] as const;

// ElevenLabs `keyterms` limits (scribe_v2): 1000 terms / 50 chars / 5 words each.
// We cap term count to the platform's 100-term client cap.
const MAX_KEYTERMS = 100;
const MAX_KEYTERM_CHARS = 50;
const MAX_KEYTERM_WORDS = 5;

/**
 * Split an initial prompt into ElevenLabs Scribe `keyterms` (scribe_v2 only).
 * Mirrors the other adapters' splitter (comma/newline/semicolon, bullet-strip),
 * and additionally drops terms exceeding ElevenLabs' 50-char / 5-word limits.
 */
function toKeyterms(initialPrompt: string): string[] {
  return splitVocabularyTerms(initialPrompt, {
    maxTerms: MAX_KEYTERMS,
    maxTermChars: MAX_KEYTERM_CHARS,
    maxTermWords: MAX_KEYTERM_WORDS,
  });
}

/**
 * Transcribe audio with ElevenLabs Scribe v2
 */
export async function transcribeWithElevenLabs(
  audio: ArrayBuffer,
  contentType: string,
  language?: string,
  initialPrompt?: string,
  context: ProviderRequestContext = {},
): Promise<TranscriptionResult> {
  const startTime = performance.now();
  const provider = 'elevenlabs';
  const apiKey = process.env.ELEVENLABS_API_KEY;
  if (!apiKey) {
    throw new Error('ELEVENLABS_API_KEY not configured');
  }

  const ext = audioExtensionFromContentType(contentType, ELEVENLABS_AUDIO_EXTENSIONS) ?? 'mp3';

  const modelId = context.model || 'scribe_v2';
  const formData = new FormData();
  formData.append('file', new Blob([audio], { type: contentType }), `audio.${ext}`);
  formData.append('model_id', modelId);
  formData.append('tag_audio_events', 'false');

  // ElevenLabs Scribe `language_code` expects a bare ISO-639-1/639-3 code, not a
  // hyphenated BCP-47 locale — the resolver strips the region ("en-US" → "en")
  // like the sibling adapters so a region-tagged code isn't rejected.
  //
  // It also fixes the handful of codes where stripping alone lands on something
  // Scribe does not list (the picker's Tagalog `tl` has to become `fil`,
  // Mandarin `zh` has to become `cmn`) and — the part that matters most —
  // returns null for a language Scribe simply does not have. Forwarding one of
  // those was a 4xx, which `providerHttpError` turns into `ProviderInputError`,
  // which walks FALLBACK_CHAINS.elevenlabs down to deepgram/groq: the user is
  // moved off the provider they picked, silently. Auto-detect on the chosen
  // provider is the better answer, and now it is a logged one.
  const langCode = resolveProviderLanguage({ provider, model: modelId, language, context });
  if (langCode) {
    formData.append('language_code', langCode);
  }

  // Keyterm biasing is a scribe_v2-only feature. `keyterms` is an ARRAY field:
  // over multipart it must be sent as ONE repeated field per term (the official
  // SDKs append one `keyterms` per item), NOT a JSON-array string — the API
  // forbids literal `[`/`]`, so a JSON blob would be a single invalid term.
  // scribe_v1 has no biasing (registry marks supportsVocabulary:false), so terms
  // are dropped there rather than sent and ignored.
  const keyterms = initialPrompt && modelId === 'scribe_v2' ? toKeyterms(initialPrompt) : [];
  for (const term of keyterms) {
    formData.append('keyterms', term);
  }

  logProviderEvent(provider, 'prepare', {
    audioBytes: audio.byteLength,
    contentType,
    language: language || 'auto',
    // What actually went out. `auto` means either the user picked Automatic or
    // Scribe has no code for the language.
    languageCodeSent: langCode ?? 'auto',
    keytermCount: keyterms.length,
  }, context);

  // Zero-retention mode: `enable_logging=false` puts the request in ElevenLabs'
  // zero-retention mode (no audio/transcript stored), but the API documents it
  // as ENTERPRISE-ONLY — a standard account can have the request rejected for
  // sending it. So gate it behind an env flag (default off) rather than sending
  // unconditionally; flip ELEVENLABS_ZERO_RETENTION=true only once the account
  // is enterprise/ZRM-eligible. Until then ElevenLabs retains by default.
  const sttUrl = process.env.ELEVENLABS_ZERO_RETENTION === 'true'
    ? 'https://api.elevenlabs.io/v1/speech-to-text?enable_logging=false'
    : 'https://api.elevenlabs.io/v1/speech-to-text';

  // Explicit User-Agent + Accept: ElevenLabs has been observed serving a
  // text/html FAQ page ("Do you restrict access ... for any specific
  // countries?") in lieu of the JSON response when no UA is set — likely
  // a bot/proxy detection at their edge. Setting a real UA + Accept makes
  // the request indistinguishable from their official SDKs.
  const response = await fetchWithTimeout(provider, sttUrl, {
    method: 'POST',
    headers: {
      'xi-api-key': apiKey,
      'User-Agent': 'hyperwhisper-cloud/1.0',
      'Accept': 'application/json',
    },
    body: formData,
  }, context);

  if (!response.ok) {
    throw await providerHttpError(provider, response, startTime, context, {
      label: 'ElevenLabs',
      authMessage: 'ElevenLabs API key is invalid',
      attachUnavailableDetails: true,
    });
  }

  // Bun's `response.json()` has been observed to throw "Failed to parse JSON"
  // on gzip+chunked ElevenLabs 200 responses, even though the same payload
  // parses cleanly in Node. Read as text first, then JSON.parse — both
  // sidesteps the Bun quirk and surfaces actionable diagnostics if the body
  // is ever genuinely empty or non-JSON in future.
  const rawText = await response.text();
  if (!rawText) {
    const ct = response.headers.get('content-type') ?? 'unknown';
    const ce = response.headers.get('content-encoding') ?? 'none';
    logProviderEvent(provider, 'empty_body', {
      elapsedMs: Math.round(performance.now() - startTime),
      contentType: ct,
      contentEncoding: ce,
    }, context);
    // A malformed/empty 200 body is recoverable by failing over, not by
    // 500ing a request the siblings could serve.
    throw new ProviderUnavailableError('ElevenLabs', `empty 200 body (content-type=${ct}, content-encoding=${ce})`, {
      kind: 'bad_response',
      status: response.status,
      elapsedMs: Math.round(performance.now() - startTime),
    });
  }
  let data: {
    text?: string;
    language_code?: string;
    language_probability?: number;
    words?: Array<{ start: number; end: number; text: string }>;
  };
  try {
    data = JSON.parse(rawText);
  } catch (parseError) {
    const ct = response.headers.get('content-type') ?? 'unknown';
    logProviderEvent(provider, 'parse_error', {
      elapsedMs: Math.round(performance.now() - startTime),
      contentType: ct,
      bodyLength: rawText.length,
      bodyPreview: rawText.slice(0, 400),
    }, context);
    // A non-JSON 200 body is recoverable by failing over, not by 500ing a
    // request the siblings could serve.
    throw new ProviderUnavailableError('ElevenLabs', `non-JSON 200 body (content-type=${ct}, len=${rawText.length})`, {
      kind: 'bad_response',
      status: response.status,
      elapsedMs: Math.round(performance.now() - startTime),
    });
  }

  // Calculate duration from word timings
  let duration = 0;
  if (data.words && data.words.length > 0) {
    const lastWord = data.words[data.words.length - 1];
    duration = lastWord.end;
  }

  const transcript = data.text || '';

  if (!transcript || transcript.trim().length === 0) {
    logProviderEvent(provider, 'no_speech', {
      elapsedMs: Math.round(performance.now() - startTime),
      language: data.language_code,
    }, context);
    return {
      text: '',
      language: data.language_code,
      durationSeconds: 0,
      costUsd: 0,
      source: 'no_speech',
    };
  }

  logProviderEvent(provider, 'success', {
    elapsedMs: Math.round(performance.now() - startTime),
    transcriptChars: transcript.length,
    durationSeconds: duration,
    language: data.language_code,
  }, context);

  return {
    text: transcript,
    language: data.language_code,
    durationSeconds: duration,
    costUsd: computeElevenLabsTranscriptionCost(duration, keyterms.length > 0),
    source: 'elevenlabs',
  };
}
