// ANONYMOUS LATENCY REPORTING
//
// Ships per-provider-attempt timings to the website, which stores them and
// aggregates them on the public /latency page. One row per attempt: a request
// that falls back from ElevenLabs to Deepgram reports two samples.
//
// Nothing identifying leaves this process — no license key, no request id, no
// IP, no transcript. See nextjs/src/db/schema/stt-latency-samples.ts for the
// stored shape and what is deliberately absent.

import { BYTES_PER_MINUTE_ESTIMATE, DEFAULT_API_BASE_URL } from './constants';

/** How long the whole batch POST may take before we abandon it. */
const REPORT_TIMEOUT_MS = 5_000;

/** Matches the ingest endpoint's ceiling; one chain never comes close. */
const MAX_SAMPLES_PER_BATCH = 20;

export type LatencyFailureKind =
  | 'timeout'
  | 'rate_limit'
  | 'upstream_5xx'
  | 'bad_response'
  | 'network_error'
  | 'input_rejected'
  | 'unknown';

export interface LatencySample {
  /** Backend provider id, e.g. 'deepgram'. */
  provider: string;
  /** Model attempted, if the provider takes one. */
  model?: string;
  /** The provider call alone — not upload, auth, or any earlier attempt. */
  latencyMs: number;
  ok: boolean;
  failureKind?: LatencyFailureKind;
  /** 1-based position in the fallback chain. */
  attempt: number;
  /** Rounded clip length. Measured on success, estimated on failure. */
  audioSeconds: number;
}

/**
 * Clip length estimated from encoded size, for attempts that FAILED — a failed
 * provider call returns no duration, but the sample still has to land in the
 * right clip-length bucket to be compared fairly.
 *
 * Deliberately NOT estimateAudioSecondsFromSize() from middleware/credits.ts:
 * that one clamps to a 10-second floor so billing never under-charges, which
 * here would push every failed short clip into the 'medium' bucket and
 * systematically bias the failure numbers. Billing wants a floor; bucketing
 * wants the truth.
 */
export function estimateSecondsFromBytes(byteLength: number): number {
  return (byteLength / BYTES_PER_MINUTE_ESTIMATE) * 60;
}

// In-flight tracking for graceful shutdown. Reporting is fired without
// awaiting so it never adds wall time to the very latency it measures — which
// means a Fly machine recycle (SIGTERM on deploy/scale-down) between the
// response flush and the POST would silently drop the samples. Every batch
// registers here so the SIGTERM handler can drain before exit. Same pattern as
// inFlightDeductions in middleware/credits.ts.
const inFlightReports = new Set<Promise<void>>();

export async function drainPendingLatencyReports(timeoutMs: number): Promise<number> {
  const pendingCount = inFlightReports.size;
  if (pendingCount === 0) {
    return 0;
  }

  const allSettled = Promise.allSettled([...inFlightReports]);
  const timeout = new Promise<void>((resolve) => setTimeout(resolve, timeoutMs));
  await Promise.race([allSettled, timeout]);
  return pendingCount;
}

/**
 * Sends one transcription's whole attempt chain. Fire without awaiting, after
 * the response is already on its way to the user.
 *
 * A failure here is never allowed to affect a transcription: everything is
 * caught and logged at most once per batch.
 */
export function reportLatencySamples(samples: LatencySample[]): void {
  if (samples.length === 0) return;

  const secret = process.env.HYPERWHISPER_INTERNAL_SECRET;
  if (!secret) {
    // Unset on a machine that has not been synced yet. Staying silent here
    // would hide a misconfiguration, so say it once per batch and move on.
    console.warn('latency_report.skipped_no_secret');
    return;
  }

  const report = sendBatch(samples, secret);
  inFlightReports.add(report);
  report
    .catch(() => {}) // already logged inside sendBatch
    .finally(() => inFlightReports.delete(report));
}

async function sendBatch(samples: LatencySample[], secret: string): Promise<void> {
  const apiBase = (process.env.NEXTJS_LICENSE_API_URL || DEFAULT_API_BASE_URL).replace(/\/+$/, '');
  const flyRegion = process.env.FLY_REGION || 'local';

  const payload = {
    samples: samples.slice(0, MAX_SAMPLES_PER_BATCH).map((sample) => ({
      provider: sample.provider,
      model: sample.model || undefined,
      flyRegion,
      latencyMs: Math.max(1, Math.round(sample.latencyMs)),
      ok: sample.ok,
      failureKind: sample.ok ? undefined : (sample.failureKind || 'unknown'),
      attempt: sample.attempt,
      audioSeconds: Math.max(0, Math.round(sample.audioSeconds)),
    })),
  };

  try {
    const response = await fetch(`${apiBase}/api/internal/latency`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'x-internal-secret': secret,
      },
      body: JSON.stringify(payload),
      signal: AbortSignal.timeout(REPORT_TIMEOUT_MS),
    });

    if (!response.ok) {
      console.warn(JSON.stringify({
        event: 'latency_report.rejected',
        status: response.status,
        count: payload.samples.length,
      }));
    }
  } catch (error) {
    console.warn(JSON.stringify({
      event: 'latency_report.failed',
      count: payload.samples.length,
      message: error instanceof Error ? error.message : String(error),
    }));
  }
}