import type { Context } from 'hono';
import type { AuthContext } from '../middleware/auth';
import { validateCredits, estimateAudioSecondsFromSize } from '../middleware/credits';
import { creditsForCost, estimatePromptInputReservationUsd } from '../lib/cost-calculator';
import { logEvent } from '../lib/logging';
import { errorResponse } from '../lib/responses';
import type { SttProviderId } from '../lib/stt-models';
import { parseMetaWav } from '../providers/meta';
import { maxReservationUsdPerMinute } from '../providers/reservation';
import { ProviderInputError, UnsupportedAudioFormatError } from '../providers/types';

/**
 * Preflight credit reservation: turn a declared Content-Length into the credits
 * to hold before the body is read.
 *
 * Which providers the request could reach, what each of them would charge, and
 * which of them has a routing tier priced above its own catalog are all
 * `maxReservationUsdPerMinute`'s to answer. What is left here is the
 * byte→seconds→credits arithmetic, plus the prompt-token allowance, which is
 * still `lib/cost-calculator.ts`'s and is applied to the primary provider only
 * — see the note in providers/reservation.ts for when that has to move.
 * `model`/`medical` are optional to keep the historical 2-arg signature working.
 */
export function estimateCreditsForProviderFallbacks(
  sizeBytes: number,
  provider: SttProviderId,
  model?: string,
  medical: boolean = false,
  initialPrompt?: string,
  language?: string,
  exactAudioSeconds?: number,
): number {
  // Muse requests are canonical mono PCM16 WAV at 16 or 24 kHz. The route
  // supplies the parsed duration because Content-Length cannot identify which
  // accepted byte rate produced the file. Keep the 16 kHz size fallback for
  // direct historical callers of this estimator; the live route never uses it
  // for a valid Muse WAV.
  const estimatedSeconds = exactAudioSeconds ?? (provider === 'meta'
    ? Math.max(10, Math.max(0, sizeBytes - 44) / (16_000 * 2))
    : estimateAudioSecondsFromSize(sizeBytes));
  const usdPerMinute = maxReservationUsdPerMinute({
    provider,
    model,
    medical,
    hasInitialPrompt: Boolean(initialPrompt),
    language,
    estimatedSeconds,
  });
  // Token-billed providers (Gemini, OpenAI gpt-4o*) charge the prompt text as
  // input tokens on top of the audio. Reserve that flat cost for the primary
  // provider (these are self-only chains) so a large vocabulary prompt on a
  // short clip can't be deducted beyond what was reserved.
  const promptReservationUsd = estimatePromptInputReservationUsd(provider, model, initialPrompt);
  const estimatedCostUsd = (estimatedSeconds / 60) * usdPerMinute + promptReservationUsd;
  return Math.max(0.1, creditsForCost(estimatedCostUsd));
}

interface PrepareTranscriptionAudioInput {
  c: Context;
  requestId: string;
  startTime: number;
  flyRequestId?: string;
  provider: SttProviderId;
  contentType: string;
  contentLength: number;
  model: string;
  medical: boolean;
  initialPrompt?: string;
  language?: string;
  auth: AuthContext;
  clientIP: string;
}

type PrepareTranscriptionAudioResult =
  | { ok: true; audioBuffer: ArrayBuffer }
  | { ok: false; response: Response };

export async function prepareTranscriptionAudio({
  c,
  requestId,
  startTime,
  flyRequestId,
  provider,
  contentType,
  contentLength,
  model,
  medical,
  initialPrompt,
  language,
  auth,
  clientIP,
}: PrepareTranscriptionAudioInput): Promise<PrepareTranscriptionAudioResult> {
  const readAudioBuffer = async (): Promise<ArrayBuffer> => {
    const uploadStart = performance.now();
    const body = await c.req.arrayBuffer();
    const uploadMs = Math.round(performance.now() - uploadStart);
    const uploadBytesPerSec = uploadMs > 0
      ? Math.round((body.byteLength / uploadMs) * 1000)
      : undefined;
    logEvent(requestId, startTime, 'transcribe.buffer_read_done', {
      audioBytes: body.byteLength,
      uploadMs,
      uploadBytesPerSec,
    });
    return body;
  };

  // Meta needs the buffered WAV to calculate an exact reservation. Before that
  // allocation, reserve the lowest possible cost for this byte count: accepted
  // 24 kHz mono PCM16 has the highest byte rate, so any canonical Muse WAV of
  // this size is at least this long. The exact duration check below still owns
  // the final amount and increases it for 16 kHz audio.
  if (provider === 'meta') {
    const minimumAudioSeconds = Math.max(0, contentLength - 44) / (24_000 * 2);
    const minimumEstimatedCredits = estimateCreditsForProviderFallbacks(
      contentLength, provider, model, medical, initialPrompt, language, minimumAudioSeconds,
    );
    const minimumCreditCheck = await validateCredits(auth, minimumEstimatedCredits, clientIP);
    if (!minimumCreditCheck.ok) {
      logEvent(requestId, startTime, 'transcribe.request_rejected', {
        reason: 'credits_failed_before_buffer',
        flyRequestId,
        status: minimumCreditCheck.response.status,
        estimatedCredits: minimumEstimatedCredits,
      });
      return minimumCreditCheck;
    }
    logEvent(requestId, startTime, 'transcribe.credits_minimum_done', {
      estimatedCredits: minimumEstimatedCredits,
    });
  }

  // Meta billing is duration-based while its two accepted PCM sample rates
  // have different byte rates. Read this finite-capped body and parse the WAV
  // before reservation; Content-Length cannot distinguish a 60-second 24 kHz
  // clip from a 90-second 16 kHz clip. Invalid/noncanonical audio deliberately
  // skips reservation and continues to the adapter, which returns the 415/400
  // that tells a native client whether to normalize and retry.
  let audioBuffer: ArrayBuffer | undefined;
  let exactAudioSeconds: number | undefined;
  let skipCreditValidationForLocalInputError = false;
  if (provider === 'meta') {
    audioBuffer = await readAudioBuffer();
    try {
      exactAudioSeconds = parseMetaWav(audioBuffer, contentType).durationSeconds;
    } catch (error) {
      if (error instanceof UnsupportedAudioFormatError || error instanceof ProviderInputError) {
        skipCreditValidationForLocalInputError = true;
      } else {
        throw error;
      }
    }
  }

  // The raw request values go in as they arrived — the initial_prompt, the
  // domain and the language are all things the reservation prices for itself.
  // See providers/reservation.ts for which of them cost what, and why.
  const estimatedCredits = estimateCreditsForProviderFallbacks(
    contentLength, provider, model, medical, initialPrompt, language, exactAudioSeconds,
  );
  if (!skipCreditValidationForLocalInputError) {
    const creditCheck = await validateCredits(auth, estimatedCredits, clientIP);
    if (!creditCheck.ok) {
      logEvent(requestId, startTime, 'transcribe.request_rejected', {
        reason: 'credits_failed',
        flyRequestId,
        status: creditCheck.response.status,
        estimatedCredits,
      });
      return creditCheck;
    }
    logEvent(requestId, startTime, 'transcribe.credits_done', { estimatedCredits });
  } else {
    logEvent(requestId, startTime, 'transcribe.credits_skipped_invalid_audio', { provider });
  }

  audioBuffer ??= await readAudioBuffer();

  // The credit check above trusted the declared Content-Length. Reject bodies
  // that arrive larger than declared so a client can't under-declare to pass
  // validateCredits cheaply and then stream a bigger payload we'd pay the
  // provider for (issue ray-amjad/hyperwhisper#263). Honest clients always
  // send a body that matches Content-Length exactly.
  if (audioBuffer.byteLength > contentLength) {
    logEvent(requestId, startTime, 'transcribe.request_rejected', {
      reason: 'content_length_mismatch',
      flyRequestId,
      declaredBytes: contentLength,
      actualBytes: audioBuffer.byteLength,
    });
    return {
      ok: false,
      response: errorResponse(400, 'Content-Length mismatch',
        `Request body (${audioBuffer.byteLength} bytes) exceeds the declared Content-Length (${contentLength} bytes)`,
        { requestId, declared_bytes: contentLength, actual_bytes: audioBuffer.byteLength },
      ),
    };
  }

  return { ok: true, audioBuffer };
}
