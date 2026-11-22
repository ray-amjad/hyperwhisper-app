import {
  reportLatencySamples,
  type LatencyFailureKind,
  type LatencySample,
} from '../lib/latency-report';
import type { SttProviderId } from '../lib/stt-models';

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
 *
 * `audioSeconds` is the request's content-type-aware estimate, used for success
 * and failure alike: adapter durations are billing estimates and would put the
 * same clip in a different public latency bucket depending on the provider.
 */
export function createTranscriptionLatencyRecorder(audioSeconds: number, reportable: boolean) {
  const samples: LatencySample[] = [];

  return {
    record(sample: AttemptSample) {
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

    /** A request that must not be reported still collects samples; they die with it. */
    report() {
      if (reportable) {
        reportLatencySamples(samples);
      }
    },
  };
}
