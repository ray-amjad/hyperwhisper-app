import {
  reportLatencySamples,
  type LatencyFailureKind,
  type LatencySample,
} from '../lib/latency-report';
import {
  ProviderInputError,
  ProviderUnavailableError,
} from '../providers/types';
import { estimateAudioSeconds } from '../providers/utils';
import type { SttProviderId } from '../lib/stt-models';

/**
 * The public page's failure taxonomy for whatever the attempt threw.
 */
function failureKindFor(error: unknown): LatencyFailureKind {
  if (error instanceof ProviderUnavailableError) return error.kind;
  if (error instanceof ProviderInputError) return 'input_rejected';
  // A revoked key or a bug in an adapter lands here. Without a sample the page
  // would report a 0% error rate for a provider that fails every call.
  return 'unknown';
}

interface AttemptSample {
  provider: SttProviderId;
  /**
   * On success the model that actually ran (the adapter's, when it reports
   * one); on a failure the model the attempt was made with, since none ran.
   */
  model?: string;
  /** 0-based position in the chain; stored 1-based. */
  index: number;
  latencyMs: number;
  /** Absent on success. */
  failureKind?: LatencyFailureKind;
}

/**
 * Collect one anonymous latency row per upstream attempt and send the rows once
 * after the provider chain has decided the response.
 */
export function createTranscriptionLatencyRecorder(
  audioBuffer: ArrayBuffer,
  contentType: string,
  reportable: boolean,
) {
  const samples: LatencySample[] = [];
  // Use one content-type-aware estimate for both success and failure. Adapter
  // duration values are billing estimates and can put the same clip in a
  // different public latency bucket depending on the provider response.
  const audioSeconds = estimateAudioSeconds(audioBuffer.byteLength, contentType);

  return {
    recordAttempt(sample: AttemptSample) {
      samples.push({
        provider: sample.provider,
        model: sample.model || undefined,
        latencyMs: sample.latencyMs,
        ok: sample.failureKind === undefined,
        failureKind: sample.failureKind,
        attempt: sample.index + 1,
        audioSeconds,
      });
    },

    recordFailure(
      provider: SttProviderId,
      model: string | undefined,
      index: number,
      attemptStart: number,
      error: unknown,
    ) {
      samples.push({
        provider,
        model: model || undefined,
        latencyMs: performance.now() - attemptStart,
        ok: false,
        failureKind: failureKindFor(error),
        attempt: index + 1,
        audioSeconds,
      });
    },

    report() {
      if (reportable) {
        reportLatencySamples(samples);
      }
    },
  };
}
