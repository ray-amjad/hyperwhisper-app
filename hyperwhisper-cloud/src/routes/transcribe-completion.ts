import type { Context } from 'hono';
import { creditsForCost, formatUsd } from '../lib/cost-calculator';
import { logEvent, machineUptimeMs } from '../lib/logging';
import { deductCredits, logDeductionFailure } from '../middleware/credits';
import type { TranscriptionResult } from '../providers/types';
import { formatProviderName, type SttProviderId } from '../lib/stt-models';
import type { PreparedTranscriptionRequest } from './transcribe-preparation';
import type { AttemptFailure } from './transcribe-attempts';

interface TranscriptionSuccessInput {
  result: TranscriptionResult;
  requestId: string;
  requestedProvider: SttProviderId;
  requestedModel: string;
  usedModel: string;
  servedBy: SttProviderId | undefined;
  chosenProviderAttempted: boolean;
  fallbackFrom: SttProviderId | undefined;
}

/**
 * The public success projection of the resolved provider result: who is named
 * as the provider, what is billed, and the response body.
 */
export function buildTranscriptionSuccess({
  result,
  requestId,
  requestedProvider,
  requestedModel,
  usedModel,
  servedBy,
  chosenProviderAttempted,
  fallbackFrom,
}: TranscriptionSuccessInput) {
  const resultSource = result.source;
  const noSpeech = resultSource === 'no_speech';
  // Nobody transcribed anything, so there is nothing to attribute to whichever
  // provider happened to report it last. The route answers `no_speech` with the
  // CHOSEN provider — naming a sibling would tell the client a provider it never
  // picked found nothing — and the model has to follow the provider or the pair
  // does not exist: a sibling's model under the chosen provider's name produced
  // strings like
  //   `Deepgram/whisper-large-v3-turbo (fallback from Deepgram/nova-3-general)`
  // on the client, in `metadata.stt_provider`, in the credit-metering row and in
  // `request_done`'s `finalProvider`. The fallback note goes for the same reason:
  // nothing fell back FROM anything when no transcript was produced. Which
  // providers were tried, and why each declined, is on `attemptFailures` in the
  // `request_done` log line.
  //
  // Both halves are conditioned on the chosen provider having been ATTEMPTED,
  // not on a sibling having answered. When this region dropped the chosen
  // provider out of the chain it was never called at all, and naming it here
  // reported `X-STT-Provider: elevenlabs/scribe_v2` + `no_speech_detected` for a
  // request elevenlabs never received — which Windows stamps into
  // `TranscriptionProviderDiagnostics` on exactly this path, so a Sentry report
  // named a provider that was never contacted. In that case the answer is filed
  // under the provider that actually produced it, with ITS model (`usedModel`,
  // which is also the field an adapter uses to report a model it silently
  // substituted, e.g. AssemblyAI universal-3-5-pro → universal-2).
  // (review r2)
  const resultProvider: SttProviderId = noSpeech
    ? (chosenProviderAttempted ? requestedProvider : servedBy ?? requestedProvider)
    : resultSource;
  const reportedModel = noSpeech && chosenProviderAttempted ? requestedModel : usedModel;
  const actualProvider = formatProviderName(resultProvider, reportedModel);
  const providerName = fallbackFrom && !noSpeech
    ? `${actualProvider} (fallback from ${formatProviderName(fallbackFrom, requestedModel)})`
    : actualProvider;

  // A no-speech result is free — EXCEPT where the upstream still billed us for
  // the audio and the adapter says so by returning a cost with it. Every
  // duration-billed provider here returns `costUsd: 0` for no_speech and is
  // unaffected; `gemini-transcribe` is token-billed on its audio input whether
  // or not a word comes back, and charging $0 there turns silent audio into an
  // unmetered channel to a paid upstream. The adapter's cost is the gate, so
  // this route never needs a per-provider table.
  const billable = !noSpeech || result.costUsd > 0;
  const creditsUsed = billable ? creditsForCost(result.costUsd) : 0;

  const response = {
    text: result.text,
    language: result.language,
    duration: result.durationSeconds,
    cost: {
      usd: result.costUsd,
      credits: creditsUsed,
    },
    metadata: {
      request_id: requestId,
      stt_provider: providerName,
      stt_model: reportedModel || undefined,
    },
    ...(noSpeech ? { no_speech_detected: true } : {}),
  };

  return {
    noSpeech,
    providerName,
    reportedModel,
    billable,
    creditsUsed,
    response,
  };
}

interface TranscriptionCompletionInput {
  c: Context;
  preparation: PreparedTranscriptionRequest;
  result: TranscriptionResult;
  usedModel: string;
  servedBy: SttProviderId | undefined;
  chosenProviderAttempted: boolean;
  fallbackFrom: SttProviderId | undefined;
  fallbackCount: number;
  attemptFailures: AttemptFailure[];
}

/**
 * Complete a successful transcription after the provider chain resolves:
 * projection, billing, headers and the `request_done` outcome log.
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
      clientIP,
      { onFailure: logDeductionFailure(requestId, startTime, '/transcribe') }
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
