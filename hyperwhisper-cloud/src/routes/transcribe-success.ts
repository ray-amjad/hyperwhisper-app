import { creditsForCost } from '../lib/cost-calculator';
import { formatProviderName, type SttProviderId } from '../lib/stt-models';
import type { TranscriptionResult } from '../providers/types';

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
 * Build the public success projection from the resolved provider result.
 * Billing and response side effects stay in the route.
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
  // Read `source` once so TypeScript narrows away the 'no_speech' member
  // itself, instead of needing a cast to re-assert what the ternary proved.
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
  // A fallback label describes a transcript. No-speech never fell back from
  // a provider because no provider produced a transcript.
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
