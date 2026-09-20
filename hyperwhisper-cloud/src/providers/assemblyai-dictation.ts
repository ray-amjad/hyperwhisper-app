// AssemblyAI Dictation is a separate service with built-in cleanup, never an async STT fallback.
// Contract: https://www.assemblyai.com/docs/dictation
import { computeAssemblyAITranscriptionCost } from '../lib/cost-calculator';
import { ProviderInputError, ProviderUnavailableError, UnsupportedAudioFormatError } from './types';
import type { ProviderRequestContext, TranscriptionResult } from './types';
import { fetchWithTimeout, logProviderEvent, providerHttpError } from './utils';

export const DICTATION_LANGUAGES = ['en', 'es', 'de', 'fr', 'it', 'pt', 'tr', 'nl', 'sv', 'no', 'da', 'fi', 'hi', 'vi', 'he', 'ur', 'ko', 'ca', 'gl', 'ru', 'ro', 'et', 'fa', 'yue', 'af', 'mr', 'zu', 'xh', 'nn', 'ar', 'ja', 'zh'] as const;

export function validateDictationOptions(language?: string, domain?: string): string {
  if (domain?.trim()) {
    throw new ProviderInputError('AssemblyAI Dictation', 400, 'Dictation does not support Medical Mode or other domains. Select Dictation without a domain.');
  }
  const primary = (language || '').trim().toLowerCase().split(/[-_]/)[0];
  const code = primary === 'nb' ? 'no' : primary;
  if (!(DICTATION_LANGUAGES as readonly string[]).includes(code)) {
    throw new ProviderInputError('AssemblyAI Dictation', 400, 'Select an explicit supported language instead of Auto for Dictation.');
  }
  return code;
}

function ascii(view: DataView, offset: number, length: number): string {
  let value = '';
  for (let index = 0; index < length; index++) {
    value += String.fromCharCode(view.getUint8(offset + index));
  }
  return value;
}

function unsupported(contentType: string): UnsupportedAudioFormatError {
  return new UnsupportedAudioFormatError('AssemblyAI Dictation', contentType, ['PCM16 WAV (convert this recording to WAV)']);
}

/** Inspect the actual RIFF chunks, never a MIME label or a byte-size estimate. */
export function parseDictationWav(audio: ArrayBuffer, contentType: string): { durationSeconds: number } {
  if (audio.byteLength < 12) throw unsupported(contentType);

  const view = new DataView(audio);
  if (ascii(view, 0, 4) !== 'RIFF' || ascii(view, 8, 4) !== 'WAVE') {
    throw unsupported(contentType);
  }

  const riffEnd = view.getUint32(4, true) + 8;
  if (riffEnd !== audio.byteLength) throw unsupported(contentType);

  let offset = 12;
  let format: {
    audioFormat: number;
    channels: number;
    sampleRate: number;
    byteRate: number;
    blockAlign: number;
    bitsPerSample: number;
  } | undefined;
  let dataBytes: number | undefined;

  while (offset < riffEnd) {
    if (offset + 8 > riffEnd) throw unsupported(contentType);
    const chunkId = ascii(view, offset, 4);
    const chunkSize = view.getUint32(offset + 4, true);
    const chunkDataStart = offset + 8;
    const chunkDataEnd = chunkDataStart + chunkSize;
    const paddedChunkEnd = chunkDataEnd + (chunkSize & 1);
    if (chunkDataEnd > riffEnd || paddedChunkEnd > riffEnd) throw unsupported(contentType);

    if (chunkId === 'fmt ') {
      if (format || chunkSize < 16) throw unsupported(contentType);
      format = {
        audioFormat: view.getUint16(chunkDataStart, true),
        channels: view.getUint16(chunkDataStart + 2, true),
        sampleRate: view.getUint32(chunkDataStart + 4, true),
        byteRate: view.getUint32(chunkDataStart + 8, true),
        blockAlign: view.getUint16(chunkDataStart + 12, true),
        bitsPerSample: view.getUint16(chunkDataStart + 14, true),
      };
    } else if (chunkId === 'data') {
      if (dataBytes !== undefined) throw unsupported(contentType);
      dataBytes = chunkSize;
    }

    offset = paddedChunkEnd;
  }

  if (!format || dataBytes === undefined || dataBytes === 0) throw unsupported(contentType);
  const expectedBlockAlign = format.channels * (format.bitsPerSample / 8);
  const expectedByteRate = format.sampleRate * expectedBlockAlign;
  if (
    format.audioFormat !== 1
    || format.channels < 1
    || format.bitsPerSample !== 16
    || format.sampleRate < 1
    || format.blockAlign !== expectedBlockAlign
    || format.byteRate !== expectedByteRate
    || dataBytes % format.blockAlign !== 0
  ) {
    throw unsupported(contentType);
  }

  const durationSeconds = dataBytes / format.byteRate;
  if (durationSeconds > 120) {
    throw new ProviderInputError('AssemblyAI Dictation', 400, 'audio exceeds 120 seconds. Use a shorter recording or another model.');
  }

  return {
    durationSeconds,
  };
}

/** Match the native vocabulary sanitizer without the async six-word limit. */
export function dictationKeyterms(prompt: string): string[] {
  const terms: string[] = [];
  const seen = new Set<string>();
  let chars = 0;
  for (const raw of prompt.split(/[,\n;]+/)) {
    const sanitized = raw.trim().replace(/^[-*]\s*/, '').replace(/[<>]/g, '').replace(/\s+/g, ' ').trim();
    const term = Array.from(sanitized).slice(0, 80).join('');
    const length = Array.from(term).length;
    if (!length || seen.has(term.toLowerCase())) continue;
    if (terms.length >= 100 || chars + length > 8000) break;
    terms.push(term);
    seen.add(term.toLowerCase());
    chars += length;
  }
  return terms;
}

export async function transcribeWithAssemblyAIDictation(
  audio: ArrayBuffer, contentType: string, language: string | undefined,
  initialPrompt: string | undefined, context: ProviderRequestContext,
): Promise<TranscriptionResult> {
  const code = validateDictationOptions(language, context.domain);
  const { durationSeconds } = parseDictationWav(audio, contentType);
  const apiKey = process.env.ASSEMBLYAI_API_KEY;
  if (!apiKey) throw new Error('ASSEMBLYAI_API_KEY not configured');
  const config: Record<string, unknown> = { language_codes: [code] };
  const keyterms = dictationKeyterms(initialPrompt || '');
  if (keyterms.length) config.keyterms_prompt = keyterms;
  // initial_prompt is vocabulary in Cloud, not rewrite instructions or STT context.
  // Omit llm_instruction to keep vendor cleanup; explicit /post-process remains separate.
  const form = new FormData();
  form.append('config', new Blob([new TextEncoder().encode(JSON.stringify(config))], { type: 'application/json' }), 'config.json');
  form.append('audio', new Blob([audio], { type: 'audio/wav' }), 'audio.wav');
  const startedAt = performance.now();
  const response = await fetchWithTimeout('assemblyai', 'https://dictation.assemblyai.com/v1/transcribe/live', {
    method: 'POST', headers: { Authorization: apiKey }, body: form,
  }, context, 90_000);
  if (!response.ok) throw await providerHttpError('assemblyai', response, startedAt, context, {
    label: 'AssemblyAI Dictation', authStatuses: [401, 403],
    authMessage: 'AssemblyAI API key is invalid or unauthorized', failoverOn402: true,
    attachUnavailableDetails: true, logDetails: { model: 'dictation' },
  });
  let job: unknown;
  try { job = await response.json(); } catch {
    throw new ProviderUnavailableError('AssemblyAI Dictation', 'malformed response', { kind: 'bad_response' });
  }
  if (!job || typeof job !== 'object' || Array.isArray(job)) {
    throw new ProviderUnavailableError('AssemblyAI Dictation', 'malformed response', { kind: 'bad_response' });
  }
  const result = job as Record<string, unknown>;
  const cleaned = typeof result.llm_response === 'string' ? result.llm_response : '';
  const raw = typeof result.text === 'string' ? result.text : '';
  if (typeof result.text !== 'string' && !cleaned.trim()) {
    throw new ProviderUnavailableError('AssemblyAI Dictation', 'missing transcript', { kind: 'bad_response' });
  }
  const text = cleaned.trim() ? cleaned : raw;
  if (!text.trim()) return { text: '', language: code, durationSeconds: 0, costUsd: 0, source: 'no_speech', model: 'dictation' };
  // The validated WAV is authoritative. Never trust an unbounded upstream duration
  // over the exact duration used for the credit reservation.
  logProviderEvent('assemblyai', 'success', {
    model: 'dictation', durationSeconds, transcriptChars: text.length,
    elapsedMs: Math.round(performance.now() - startedAt),
  }, context);
  return { text, language: code, durationSeconds,
    costUsd: computeAssemblyAITranscriptionCost(durationSeconds, 'dictation'),
    source: 'assemblyai', model: 'dictation',
    requestId: typeof result.session_id === 'string' ? result.session_id : undefined,
  };
}
