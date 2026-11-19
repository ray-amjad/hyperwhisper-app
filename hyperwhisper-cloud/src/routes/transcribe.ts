// POST /transcribe: walks the provider fallback chain the registry gives it.

import type { Context } from 'hono';
import type { TranscriptionResult } from '../providers/types';
import { transcribeWithProvider } from '../providers/dispatch';
import { fallbackChainFor, getProviderDef, type SttProviderId } from '../lib/stt-models';
import { estimateAudioSeconds, runProviderAttempt, type ProviderAttemptNetwork } from '../providers/utils';
import { reachableFromRegion } from '../providers/geo-availability';
import { logEvent } from '../lib/logging';
import { prepareTranscriptionRequest } from './transcribe-preparation';
import { completeTranscription } from './transcribe-completion';
import { AttemptLedger, classifyAttemptError } from './transcribe-attempts';
import { createTranscriptionLatencyRecorder } from './transcribe-latency';
import { withCreditHoldScope } from '../middleware/credit-holds';

export async function transcribeRoute(c: Context) {
  // Every exit below, an early refusal, a failed chain, a `no_speech`, a throw,
  // releases the credit hold the preparation took, unless the completion's
  // deduction took it over to settle (#2008).
  return withCreditHoldScope(() => handleTranscribe(c));
}

async function handleTranscribe(c: Context) {
  const preparation = await prepareTranscriptionRequest(c);
  if (preparation instanceof Response) {
    return preparation;
  }
  const {
    requestId,
    startTime,
    provider,
    model,
    domain,
    contentType,
    language,
    initialPrompt,
    audioBuffer,
    latencyReportable,
  } = preparation;

  // Filtered unconditionally: a member this region cannot reach answers a
  // geo-block (200 `text/html`) the adapter cannot tell from a real answer, so
  // `chain` is only what this request may actually call. (issue #381)
  const chain = reachableFromRegion(fallbackChainFor(provider));
  const requestAudioSeconds = estimateAudioSeconds(audioBuffer.byteLength, contentType);
  const latency = createTranscriptionLatencyRecorder(requestAudioSeconds, latencyReportable);
  const ledger = new AttemptLedger(requestId, startTime, chain, latency);
  let fallbackFrom: SttProviderId | undefined;
  // Whether the provider the caller ASKED for was ever attempted. An oversized
  // upload from a region that geo-blocks it drops it from the chain, and the
  // request is served by a sibling from position 0; its outcome must not be
  // attributed to a provider we never called. (review r2)
  let chosenProviderAttempted = false;

  try {
    for (const [index, current] of chain.entries()) {
      if (ledger.recoveryBudgetSpent) break;
      if (current === provider) {
        chosenProviderAttempted = true;
      }

      // The chosen model + domain only apply to the provider the caller picked.
      // Fallback siblings run their own default model and never inherit the
      // medical add-on.
      const attemptModel = current === provider ? model : getProviderDef(current).defaultModel;
      const attemptDomain = current === provider ? domain : undefined;

      logEvent(requestId, startTime, 'transcribe.provider_attempt_start', {
        provider: current,
        model: attemptModel || 'default',
        attempt: index + 1,
      });

      const attemptStart = performance.now();
      const network: ProviderAttemptNetwork = { reachedProvider: false };
      let attemptResult: TranscriptionResult;
      try {
        attemptResult = await runProviderAttempt(network, () => transcribeWithProvider(current, audioBuffer, contentType, language, initialPrompt, {
          requestId,
          attempt: index + 1,
          model: attemptModel,
          domain: attemptDomain,
          // Only the provider the CALLER CHOSE may refuse, and only with a
          // sibling after it: a covering sibling refusing in turn would push the
          // request onto elevenlabs, ~15x groq's price, at zero revenue since a
          // `no_speech` is never billable. (issue #381, review r2)
          mayRefuseEmptyTranscript: !ledger.hasRefused
            && current === provider
            && index < chain.length - 1,
          isEmptyTranscriptRecovery: ledger.hasRefused,
          requestAudioSeconds,
        }));
      } catch (error) {
        const outcome = classifyAttemptError(error, {
          provider: current,
          model: attemptModel,
          index,
          attemptStart,
          reachedWire: network.reachedProvider,
          hasNext: index < chain.length - 1,
        });
        const terminal = ledger.recordFailure(outcome);
        if (terminal) return terminal;
        continue;
      }
      ledger.recordSuccess({ provider: current, model: attemptModel, index, attemptStart, result: attemptResult });
      if (current !== provider && chosenProviderAttempted) {
        fallbackFrom = provider;
      }
      break;
    }
  } finally {
    // Fire-and-forget, once, on every path out of the loop: a slow or failing
    // website must never delay a transcript.
    latency.report();
  }

  const { answer } = ledger;
  if (!answer) {
    return ledger.chainFailureResponse(provider);
  }
  return completeTranscription({
    c,
    preparation,
    result: answer.result,
    usedModel: answer.usedModel ?? model,
    servedBy: answer.servedBy,
    chosenProviderAttempted,
    fallbackFrom,
    fallbackCount: ledger.fallbackCount,
    attemptFailures: ledger.attemptFailures,
  });
}
