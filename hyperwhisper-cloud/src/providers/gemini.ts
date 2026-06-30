// GEMINI TRANSCRIPTION PROVIDER
// Gemini is an LLM, not a dedicated STT API: we send the audio inline (base64)
// to generateContent with a verbatim-transcription instruction. Audio is billed
// at 32 tokens/sec; cost is read from usageMetadata. Thinking is minimised
// per-model (only 2.5-flash/flash-lite can zero it) to avoid runaway output
// tokens. There is no vocabulary API — terms are injected into the prompt.

import { computeGeminiTranscriptionCost } from '../lib/cost-calculator';
import { BYTES_PER_MINUTE_ESTIMATE, GEMINI_INLINE_MAX_BYTES } from '../lib/constants';
import { AudioTooLargeError, ProviderInputError, ProviderUnavailableError } from './types';
import type { ProviderRequestContext, TranscriptionResult } from './types';
import { fetchWithTimeout, logProviderEvent, readErrorBodyPreview } from './utils';

const GEMINI_BASE = 'https://generativelanguage.googleapis.com/v1beta/models';
const DEFAULT_MODEL = 'gemini-2.5-flash';
const AUDIO_TOKENS_PER_SECOND = 32;
// Total request (incl. base64) must stay under 20 MB; base64 inflates ~33%, so
// cap raw audio at ~14 MB and 413 anything larger (no Files-API path in v1).
// GEMINI_INLINE_MAX_BYTES is shared with the route's pre-buffer gate.

const GEMINI_MIME: Record<string, string> = {
  wav: 'audio/wav', mp3: 'audio/mp3', mpeg: 'audio/mp3', m4a: 'audio/m4a',
  mp4: 'audio/m4a', aac: 'audio/aac', ogg: 'audio/ogg', flac: 'audio/flac', aiff: 'audio/aiff',
};

export function geminiMimeType(contentType: string): string {
  // Strip any parameters (e.g. "audio/webm;codecs=opus" → "audio/webm").
  const base = contentType.toLowerCase().split(';')[0].trim();
  for (const [needle, mime] of Object.entries(GEMINI_MIME)) {
    if (base.includes(needle)) return mime;
  }
  // Unknown audio subtype (e.g. a browser "audio/webm" recording): forward the
  // caller's ACTUAL audio/* type rather than mislabeling the bytes as WAV.
  // Gemini is self-only, so a truthful type lets it accept a supported format or
  // reject cleanly — far better than a silent mis-decode of wav-labeled webm.
  // Only fall back to audio/wav when there's no usable audio/* type at all.
  if (base.startsWith('audio/') && base.length > 'audio/'.length) {
    return base;
  }
  return 'audio/wav';
}

/** Per-model thinking config — keep thinking as low as each model allows. */
function thinkingConfig(model: string): Record<string, unknown> {
  if (model === 'gemini-3.1-pro-preview') return { thinkingLevel: 'low' };
  if (model.startsWith('gemini-3')) return { thinkingLevel: 'minimal' };
  if (model === 'gemini-2.5-pro') return { thinkingBudget: 128 }; // 0 invalid on Pro
  return { thinkingBudget: 0 }; // 2.5-flash / 2.5-flash-lite
}

function buildPrompt(language?: string, initialPrompt?: string): string {
  let prompt = 'Transcribe the speech in this audio verbatim. Output only the transcript text with no commentary, labels, timestamps, or preamble.';
  if (language && language.toLowerCase() !== 'auto') {
    prompt += ` The audio is in language code "${language.toLowerCase()}"; transcribe it in that language.`;
  }
  if (initialPrompt) {
    // Match the other adapters' splitter: strip leading `- `/`* ` bullet markers
    // so a bulleted vocab list doesn't bias the model toward literal dashes.
    const terms = initialPrompt
      .split(/[,\n;]+/)
      .map((t) => t.trim().replace(/^[-*]\s*/, ''))
      .filter(Boolean)
      .slice(0, 100);
    if (terms.length) {
      prompt += ` Spell these terms exactly when you hear them: ${terms.map((t) => `"${t}"`).join(', ')}.`;
    }
  }
  return prompt;
}

function arrayBufferToBase64(buffer: ArrayBuffer): string {
  return Buffer.from(buffer).toString('base64');
}

export interface GeminiUsageMetadata {
  promptTokenCount?: number;
  candidatesTokenCount?: number;
  thoughtsTokenCount?: number;
  totalTokenCount?: number;
  promptTokensDetails?: Array<{ modality?: string; tokenCount?: number }>;
}

/**
 * Resolve the audio-input token count used for billing + duration.
 *
 * The prompt sent to Gemini is `[instruction text] + [audio]`, so promptTokenCount
 * covers BOTH the audio and the (non-trivial: instruction + up to 100 vocab
 * terms) text. Billing the whole promptTokenCount at the audio-input rate
 * over-charges (~25%) and inflates the derived durationSeconds. Preference order:
 *   1. The explicit AUDIO modality breakdown — exact.
 *   2. promptTokenCount minus the known non-AUDIO (text) modality components, when
 *      a partial breakdown is present AND the remainder is plausible — backs the
 *      audio count out of the total. Guarded so a sparse breakdown that omits the
 *      TEXT entry (leaving the remainder ≈ the whole prompt) is NOT trusted.
 *   3. promptTokenCount (Google's documented input-token TOTAL) minus our own
 *      estimate of the text we sent. promptTokenCount is the most reliable signal
 *      when no modality breakdown is returned: a byte heuristic assumes ~64 kbps
 *      and falls FAR below Gemini's flat 32 audio-tokens/sec for low-bitrate
 *      Opus/AAC, under-billing real usage. We subtract the estimated prompt-text
 *      tokens so that text isn't billed at the audio rate.
 *   4. A byte-based duration estimate — last resort, only when there's no usage
 *      total at all (or step 3 nets ≤ 0 for a tiny clip dominated by a big vocab
 *      prompt). Conservative: undershoots rather than over-bills.
 */
export function resolveAudioInputTokens(
  usage: GeminiUsageMetadata | undefined,
  audioByteLength: number,
  promptTextTokens: number = 0,
): number {
  const details = usage?.promptTokensDetails;
  const audioDetail = details?.find((d) => d.modality === 'AUDIO');
  if (audioDetail && typeof audioDetail.tokenCount === 'number') {
    return Math.max(0, audioDetail.tokenCount);
  }

  const promptTokenCount = usage?.promptTokenCount ?? 0;
  if (details && details.length > 0) {
    // Partial breakdown without an AUDIO entry — subtract the non-AUDIO
    // (text/image/etc.) components from the prompt total to back out the audio.
    const nonAudioTokens = details
      .filter((d) => d.modality !== 'AUDIO')
      .reduce((sum, d) => sum + (d.tokenCount ?? 0), 0);
    const remainder = promptTokenCount - nonAudioTokens;
    // Only trust the remainder when it's a genuine subset of the prompt: a
    // breakdown that omits the TEXT component would leave remainder ≈ the whole
    // prompt, so we fall through to the promptTokenCount tier (which backs out
    // our OWN text estimate) rather than trusting the bogus breakdown.
    if (remainder > 0 && remainder < promptTokenCount * 0.99) {
      return remainder;
    }
  }

  // No trustworthy modality breakdown. Prefer the documented promptTokenCount
  // (input-token TOTAL) over a byte heuristic — for low-bitrate Opus/AAC the
  // byte estimate falls far below Gemini's flat 32 audio-tokens/sec and
  // under-bills. Back out our own estimate of the text we sent so it isn't
  // billed at the audio rate; clamp and fall through if nothing remains.
  if (promptTokenCount > 0) {
    const audioTokens = promptTokenCount - Math.max(0, promptTextTokens);
    if (audioTokens > 0) {
      return audioTokens;
    }
  }

  // Last resort: no usage total at all — estimate audio seconds from payload size.
  const estimatedSeconds = (audioByteLength / BYTES_PER_MINUTE_ESTIMATE) * 60;
  return Math.round(estimatedSeconds * AUDIO_TOKENS_PER_SECOND);
}

// Rough English-text token estimate (~4 chars/token) for the instruction+vocab
// prompt we send alongside the audio, so it can be backed out of promptTokenCount
// instead of being billed at the audio rate. Approximate by design — a small
// over/under-estimate only nudges the audio count by a few percent.
const CHARS_PER_TOKEN_ESTIMATE = 4;
export function estimatePromptTextTokens(promptText: string): number {
  return Math.ceil(promptText.length / CHARS_PER_TOKEN_ESTIMATE);
}

export async function transcribeWithGemini(
  audio: ArrayBuffer,
  contentType: string,
  language?: string,
  initialPrompt?: string,
  context: ProviderRequestContext = {},
): Promise<TranscriptionResult> {
  const startedAt = performance.now();
  const provider = 'gemini';
  const model = context.model || DEFAULT_MODEL;

  const apiKey = process.env.GEMINI_API_KEY || process.env.GOOGLE_GEMINI_API_KEY;
  if (!apiKey) {
    throw new Error('GEMINI_API_KEY not configured');
  }

  if (audio.byteLength > GEMINI_INLINE_MAX_BYTES) {
    throw new AudioTooLargeError('Gemini', audio.byteLength, GEMINI_INLINE_MAX_BYTES);
  }

  const promptText = buildPrompt(language, initialPrompt);
  const body = {
    contents: [{
      role: 'user',
      parts: [
        { text: promptText },
        { inline_data: { mime_type: geminiMimeType(contentType), data: arrayBufferToBase64(audio) } },
      ],
    }],
    generationConfig: {
      temperature: 0,
      thinkingConfig: thinkingConfig(model),
    },
  };

  logProviderEvent(provider, 'prepare', {
    model,
    audioBytes: audio.byteLength,
    contentType,
    language: language || 'auto',
    hasPrompt: Boolean(initialPrompt),
  }, context);

  const url = `${GEMINI_BASE}/${encodeURIComponent(model)}:generateContent`;
  const response = await fetchWithTimeout(provider, url, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'x-goog-api-key': apiKey },
    body: JSON.stringify(body),
  }, context);

  if (!response.ok) {
    const errorText = await readErrorBodyPreview(response);
    const elapsedMs = Math.round(performance.now() - startedAt);
    const kind = response.status >= 500 ? 'upstream_5xx' : response.status === 429 ? 'rate_limit' : 'http_error';

    logProviderEvent(provider, 'http_error', {
      model, elapsedMs, status: response.status, kind, bodyPreview: errorText,
    }, context);

    if (response.status === 401 || response.status === 403) {
      throw new Error('Gemini API key is invalid or unauthorized');
    }
    if (response.status === 429) {
      throw new ProviderUnavailableError('Gemini', 'rate limit exceeded');
    }
    if (response.status === 402) {
      throw new ProviderUnavailableError('Gemini', 'insufficient funds');
    }
    if (response.status >= 500) {
      throw new ProviderUnavailableError('Gemini', `upstream 5xx: ${response.status}`);
    }
    throw new ProviderInputError('Gemini', response.status, errorText || `HTTP ${response.status}`);
  }

  let data: {
    candidates?: Array<{ content?: { parts?: Array<{ text?: string }> } }>;
    usageMetadata?: GeminiUsageMetadata;
  };
  try {
    data = await response.json();
  } catch {
    throw new ProviderUnavailableError('Gemini', 'malformed 200 response body');
  }

  const transcript = (data.candidates?.[0]?.content?.parts ?? [])
    .map((p) => p.text)
    .filter((t): t is string => typeof t === 'string')
    .join('')
    .trim();

  const usage = data.usageMetadata;
  const audioInputTokens = resolveAudioInputTokens(usage, audio.byteLength, estimatePromptTextTokens(promptText));
  // The non-audio remainder of the documented prompt total is the text we sent
  // (instruction + vocab). Bill it at the text-input rate — not the audio rate,
  // and not $0. audio + text always sums to promptTokenCount, so there's no
  // double-count and no unbilled remainder. Zero when no usage total exists.
  const promptTokenCount = usage?.promptTokenCount ?? 0;
  const textInputTokens = promptTokenCount > 0 ? Math.max(0, promptTokenCount - audioInputTokens) : 0;
  // Gemini bills output INCLUDING thinking tokens, so both must be billed —
  // only candidatesTokenCount is the visible transcript; thoughtsTokenCount is
  // the (often non-zero on Pro/3.x) thinking output that's charged the same.
  const outputTokens = (usage?.candidatesTokenCount ?? 0) + (usage?.thoughtsTokenCount ?? 0);
  const durationSeconds = audioInputTokens / AUDIO_TOKENS_PER_SECOND;

  if (!transcript || transcript.length === 0) {
    logProviderEvent(provider, 'no_speech', {
      model, elapsedMs: Math.round(performance.now() - startedAt),
    }, context);
    return { text: '', language, durationSeconds: 0, costUsd: 0, source: 'no_speech' };
  }

  const costUsd = computeGeminiTranscriptionCost(model, {
    audioInputTokens,
    textInputTokens,
    outputTokens,
    fallbackDurationSeconds: (audio.byteLength / BYTES_PER_MINUTE_ESTIMATE) * 60,
  });

  logProviderEvent(provider, 'success', {
    model,
    elapsedMs: Math.round(performance.now() - startedAt),
    transcriptChars: transcript.length,
    audioInputTokens,
    outputTokens,
    candidatesTokenCount: usage?.candidatesTokenCount ?? 0,
    thoughtsTokenCount: usage?.thoughtsTokenCount ?? 0,
    durationSeconds,
  }, context);

  return {
    text: transcript,
    language,
    durationSeconds,
    costUsd,
    source: 'gemini',
  };
}
