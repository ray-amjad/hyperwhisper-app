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
  // Nobody transcribed anything, so attribute no-speech to the requested
  // provider only when it was attempted. A geo-degraded chain instead names
  // the provider and model that actually produced the result.
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

  // A no-speech result is free unless the adapter reports an upstream cost.
  // This keeps token-billed silence chargeable without provider-specific rules.
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
