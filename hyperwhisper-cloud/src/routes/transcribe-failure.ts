import { logEvent } from '../lib/logging';
import { errorResponse } from '../lib/responses';
import {
  isSelfOnly,
  servedNameFor,
  type SttProviderId,
} from '../lib/stt-models';
import type { ProviderInputError } from '../providers/types';

interface AttemptFailure {
  provider: SttProviderId;
  kind: string;
  status?: number;
  attemptMs?: number;
  emptyTranscript?: true;
}

interface ProviderChainFailureInput {
  requestId: string;
  startTime: number;
  provider: SttProviderId;
  fallbackCount: number;
  attemptFailures: AttemptFailure[];
  lastError: Error | undefined;
  lastInputError: ProviderInputError | undefined;
  sawUnavailable: boolean;
}

/**
 * Build the response after every usable provider in the chain has failed.
 */
export function providerChainFailureResponse({
  requestId,
  startTime,
  provider,
  fallbackCount,
  attemptFailures,
  lastError,
  lastInputError,
  sawUnavailable,
}: ProviderChainFailureInput): Response {
  // Every provider rejected the input with a non-auth 4xx and none was merely
  // unavailable. The input itself is the problem, so a retry will not help.
  if (lastInputError && !sawUnavailable) {
    logEvent(requestId, startTime, 'transcribe.request_fail', {
      kind: 'all_providers_rejected_input',
      provider,
      fallbackCount,
      status: lastInputError.status,
      message: lastInputError.message,
    });
    return errorResponse(400, 'Transcription input rejected',
      `No transcription provider accepted this request: ${lastInputError.message}`,
      { requestId, provider },
    );
  }

  // A self-only chain has no sibling that can absorb the failure. A 429 would
  // incorrectly tell the client that a fallback retry is available.
  if (isSelfOnly(provider)) {
    logEvent(requestId, startTime, 'transcribe.request_fail', {
      kind: 'self_only_chain_failed',
      provider,
      fallbackCount,
      attemptFailures,
      message: lastError?.message,
    });
    return errorResponse(502, `${servedNameFor(provider)} unavailable`,
      lastError?.message ?? `${servedNameFor(provider)} is currently unavailable. Please try again shortly.`,
      { requestId, provider },
    );
  }

  logEvent(requestId, startTime, 'transcribe.request_fail', {
    kind: 'all_providers_unavailable',
    fallbackCount,
    attemptFailures,
    message: lastError?.message,
  });
  return errorResponse(429, 'All providers unavailable', 'All transcription providers are currently rate-limited. Please try again shortly.', { requestId });
}
