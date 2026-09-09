import type { Context } from 'hono';
import type { AuthContext } from '../middleware/auth';
import { validateCredits } from '../middleware/credits';
import { creditsForCost, estimatePromptInputReservationUsd } from '../lib/cost-calculator';
import { logEvent } from '../lib/logging';
import { errorResponse } from '../lib/responses';
import type { SttProviderId } from '../lib/stt-models';
import { providerAudioReservation } from '../providers/audio-reservation';
import { maxReservationUsdPerMinute } from '../providers/reservation';

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
  const estimatedSeconds = exactAudioSeconds
    ?? providerAudioReservation(provider, sizeBytes).estimatedAudioSeconds;
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

  const audioReservation = providerAudioReservation(provider, contentLength);

  // Some providers need the buffered audio to calculate an exact reservation.
  // Before that allocation, check the provider boundary's safe duration floor.
  if (audioReservation.requiresBufferedBody) {
    const minimumEstimatedCredits = estimateCreditsForProviderFallbacks(
      contentLength, provider, model, medical, initialPrompt, language,
      audioReservation.preBufferAudioSeconds,
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

  // Read before reservation only when the provider boundary needs the body.
  // A local input error deliberately skips the final credit check and continues
  // to the adapter, which returns the existing client-facing response.
  let audioBuffer: ArrayBuffer | undefined;
  let exactAudioSeconds: number | undefined;
  let skipCreditValidationForLocalInputError = false;
  if (audioReservation.requiresBufferedBody) {
    audioBuffer = await readAudioBuffer();
    const resolvedReservation = audioReservation.resolveBufferedAudio(audioBuffer, contentType);
    if (resolvedReservation.kind === 'duration') {
      exactAudioSeconds = resolvedReservation.audioSeconds;
    } else {
      skipCreditValidationForLocalInputError = true;
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
