import type { Context } from 'hono';
import { formatUsd } from '../lib/cost-calculator';
import { logEvent, machineUptimeMs } from '../lib/logging';
import { deductCredits } from '../middleware/credits';
import type { TranscriptionResult } from '../providers/types';
import type { SttProviderId } from '../lib/stt-models';
import type { PreparedTranscriptionRequest } from './transcribe-preparation';
import { buildTranscriptionSuccess } from './transcribe-success';

interface TranscriptionCompletionInput {
  c: Context;
  preparation: PreparedTranscriptionRequest;
  result: TranscriptionResult;
  usedModel: string;
  servedBy: SttProviderId | undefined;
  chosenProviderAttempted: boolean;
  fallbackFrom: SttProviderId | undefined;
  fallbackCount: number;
  attemptFailures: Array<{
    provider: SttProviderId;
    kind: string;
    status?: number;
    attemptMs?: number;
    emptyTranscript?: true;
  }>;
}

/**
 * Complete a successful transcription after the provider chain resolves.
 * This keeps response projection, billing, headers, and the final outcome log
 * together and leaves the route responsible for request and provider control flow.
 */
export function completeTranscription({
  c,
  preparation,
  result,
  usedModel,
  servedBy,
  chosenProviderAttempted,
  fallbackFrom,
  fallbackCount,
  attemptFailures,
}: TranscriptionCompletionInput) {
  const {
    requestId,
    startTime,
    clientIP,
    provider,
    model,
    mode,
    clientPlatform,
    clientVersion,
    auth,
    language,
    latencyOptOut,
    latencyReportable,
  } = preparation;

  logEvent(requestId, startTime, 'transcribe.stt_done', {
    provider: result.source,
    upstreamRequestId: result.requestId,
  });

  const {
    noSpeech,
    providerName,
    reportedModel,
    billable,
    creditsUsed,
    response,
  } = buildTranscriptionSuccess({
    result,
    requestId,
    requestedProvider: provider,
    requestedModel: model,
    usedModel,
    servedBy,
    chosenProviderAttempted,
    fallbackFrom,
  });

  if (billable) {
    deductCredits(
      auth,
      result.costUsd,
      {
        audio_duration_seconds: result.durationSeconds,
        transcription_cost_usd: result.costUsd,
        language: result.language ?? language ?? 'auto',
        mode,
        endpoint: '/transcribe',
        stt_provider: providerName,
        stt_model: reportedModel || undefined,
      },
      clientIP
    ).catch(console.error);
  }

  c.header('X-Request-ID', requestId);
  c.header('X-STT-Provider', providerName);
  if (reportedModel) {
    c.header('X-STT-Model', reportedModel);
  }
  c.header('X-Total-Cost-Usd', formatUsd(result.costUsd));
  c.header('X-Credits-Used', creditsUsed.toFixed(1));

  const memUsageMb = Math.round(process.memoryUsage().rss / 1024 / 1024);
  logEvent(requestId, startTime, 'transcribe.request_done', {
    clientPlatform,
    clientVersion,
    finalProvider: providerName,
    fallbackCount,
    // On a degraded success (fallbackCount > 0) this names which provider(s)
    // failed and why, so a slow-but-successful transcription is diagnosable
    // from the single outcome line.
    ...(attemptFailures.length ? { attemptFailures } : {}),
    noSpeech,
    creditsUsed,
    flyMachineId: process.env.FLY_MACHINE_ID,
    // Region on the outcome line makes the Axiom dataset queryable by region on
    // its own, without joining against the machine id.
    flyRegion: process.env.FLY_REGION || 'local',
    // Only present when this request contributed no timing, so the field's
    // absence is the normal case. Without it a thin /latency dataset looks
    // like a bug; with it, "how much of the installed base is still too old to
    // be measured?" is one Axiom query.
    ...(latencyReportable
      ? {}
      : { latencySkipped: latencyOptOut ? 'opted_out' : 'client_too_old' }),
    machineUptimeMs: machineUptimeMs(),
    rssMb: memUsageMb,
  });
  return c.json(response);
}
