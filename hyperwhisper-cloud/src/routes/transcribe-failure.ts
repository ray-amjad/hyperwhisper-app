import { logEvent } from '../lib/logging';
import { errorResponse } from '../lib/responses';
import {
  isSelfOnly,
  servedNameFor,
  type SttProviderId,
} from '../lib/stt-models';
import type { ProviderAuthError, ProviderInputError } from '../providers/types';

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
  lastAuthError?: ProviderAuthError;
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
  lastAuthError,
  sawUnavailable,
}: ProviderChainFailureInput): Response {
  // One provider rejected OUR key and no sibling could serve the request. The
  // key is the fault to surface: a 400 would blame the user's input and a 429
  // would invite retries against a key that will keep failing. Same 500 and
  // message a bad key gave before the chain tried siblings for it.
  if (lastAuthError) {
    logEvent(requestId, startTime, 'transcribe.request_fail', {
      kind: 'non_retryable',
      provider,
      fallbackCount,
      attemptFailures,
      status: lastAuthError.status,
      message: lastAuthError.message,
    });
    return errorResponse(500, 'Transcription failed', lastAuthError.message, { requestId });
  }

  // Every provider rejected the input with a non-auth 4xx and none was merely
  // unavailable. The input itself is the problem, so a retry will not help.
  //
  // A rejection from a provider gets a fixed message: the upstream's wording
  // never reaches the client (#2131). A rejection the server wrote itself
  // (`fromProvider` false: a WebSocket-only model, a missing explicit
  // language, audio over a cap) is our own hint and is sent as before.
  // `message` is content-free either way (#2163), so the log keeps it.
  if (lastInputError && !sawUnavailable) {
    logEvent(requestId, startTime, 'transcribe.request_fail', {
      kind: 'all_providers_rejected_input',
      provider,
      fallbackCount,
      status: lastInputError.status,
      message: lastInputError.message,
    });
    return errorResponse(400, 'Transcription input rejected',
      lastInputError.fromProvider
        ? 'No transcription provider accepted this audio or its settings (for example the language).'
        : `No transcription provider accepted this request: ${lastInputError.message}`,
      { requestId, provider },
    );
  }

  // A self-only chain has no sibling that can absorb the failure. A 429 would
  // incorrectly tell the client that a fallback retry is available. The error
  // is a network or parse failure the user cannot act on: log it, never echo
  // it (#2131).
  if (isSelfOnly(provider)) {
    logEvent(requestId, startTime, 'transcribe.request_fail', {
      kind: 'self_only_chain_failed',
      provider,
      fallbackCount,
      attemptFailures,
      message: lastError?.message,
    });
    return errorResponse(502, `${servedNameFor(provider)} unavailable`,
      `${servedNameFor(provider)} is currently unavailable. Please try again shortly.`,
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
