// POST /transcribe: walks the provider fallback chain the registry gives it.

import type { Context } from 'hono';
import type { TranscriptionResult } from '../providers/types';
// The providers layer's own answer to "which adapter runs this provider id", so
// the route never imports an adapter or keeps a dispatch table of its own. See
// providers/dispatch.ts.
import { transcribeWithProvider } from '../providers/dispatch';
import { fallbackChainFor, getProviderDef, type SttProviderId } from '../lib/stt-models';
// Content-type aware, unlike the billing estimators: a failed attempt still has
// to land in the right clip-length bucket on the public /latency page.
// runProviderAttempt is how the same page learns whether an attempt ever reached
// the provider at all.
import { runProviderAttempt, type ProviderAttemptNetwork } from '../providers/provider-attempt';
import { estimateAudioSeconds } from '../providers/provider-audio';
// The providers layer's own answer to "can this Fly region reach this provider",
// so the route never needs a provider's blocked-region list, its replay region,
// or its id in a filter. See providers/geo-availability.ts.
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

  // Only the chain members this region can actually reach. We fall through to
  // the next provider instead of failing the chain on a geo-block response the
  // adapter cannot tell apart from a real answer.
  //
  // Filtered UNCONDITIONALLY, not only when the CHOSEN provider is the blocked
  // one. A `deepgram` request served from `nrt`/`bom`/`maa` used to keep
  // elevenlabs on the tail of its chain, so a degraded request uploaded the
  // audio a third time to a host that answers 200 `text/html`. That call could
  // never produce a transcript, and it also made `chain.length` a count of
  // providers this request cannot use — which the empty-transcript grant below
  // reads. One filter, one meaning: `chain` is what this request may actually
  // call. (issue ray-amjad/hyperwhisper-app#381, review r2)
  const chain = reachableFromRegion(fallbackChainFor(provider));
  const requestAudioSeconds = estimateAudioSeconds(audioBuffer.byteLength, contentType);
  const latency = createTranscriptionLatencyRecorder(requestAudioSeconds, latencyReportable);
  const ledger = new AttemptLedger(requestId, startTime, chain, latency);
  let fallbackFrom: SttProviderId | undefined;
  // Whether the provider the caller ASKED for was ever attempted. An oversized
  // upload from a region that geo-blocks it drops it from the chain, and the
  // request is served by a sibling from position 0; its outcome must not be
  // attributed to a provider we never called — which is exactly what the
  // `no_speech` attribution and the `fallback from` note both used to do.
  // (review r2)
  let chosenProviderAttempted = false;

  try {
    for (const [index, current] of chain.entries()) {
      // The one extra call this feature is allowed, already spent. Stopping here
      // is not giving up: the refusal banked a `no_speech` in the ledger, so the
      // request answers with that. Walking on would cost the third and fourth
      // upstream call the spec priced out. (issue #381)
      if (ledger.recoveryBudgetSpent) break;
      if (current === provider) {
        chosenProviderAttempted = true;
      }

      // The chosen model + domain only apply to the provider the caller picked.
      // Fallback siblings run their own default model (the caller's model id is
      // meaningless to them) and never inherit the medical add-on.
      const attemptModel = current === provider ? model : getProviderDef(current).defaultModel;
      const attemptDomain = current === provider ? domain : undefined;

      logEvent(requestId, startTime, 'transcribe.provider_attempt_start', {
        provider: current,
        model: attemptModel || 'default',
        attempt: index + 1,
      });

      // logEvent's elapsedMs runs from REQUEST start, so it already includes
      // upload, auth, credits, and every earlier attempt. This clock brackets
      // this provider call alone — the number the /latency page reports.
      const attemptStart = performance.now();
      // Flipped by the fetch helpers the instant this attempt's first request
      // leaves the process. Until then the adapter has only run its own gates —
      // a missing API key, a size cap, a content-type check — and those are our
      // `if`s, not an upstream, so they are not measurements. See
      // ProviderAttemptNetwork in providers/provider-attempt.ts for why the signal is taken
      // at the wire rather than from a list of error types.
      const network: ProviderAttemptNetwork = { reachedProvider: false };
      let attemptResult: TranscriptionResult;
      try {
        attemptResult = await runProviderAttempt(network, () => transcribeWithProvider(current, audioBuffer, contentType, language, initialPrompt, {
          requestId,
          attempt: index + 1,
          model: attemptModel,
          domain: attemptDomain,
          // Granted here and nowhere else. An adapter cannot see this request's
          // chain — geo filtering can shrink it to a single provider — so it is
          // told, per attempt, whether refusing an empty transcript has anywhere
          // to go. See ProviderRequestContext.mayRefuseEmptyTranscript.
          //
          // THREE conditions, and the middle one is the spec's, not a fact about
          // the table: only the provider the CALLER CHOSE may refuse. A sibling
          // that is already covering for a rate-limited primary refusing in turn
          // would push the request onto a third provider — on the deepgram/groq/
          // grok chains that third provider is elevenlabs, ~15x groq's price and
          // documented as the last resort, called at zero revenue because a
          // `no_speech` is never billable. Granting it at every non-terminal
          // position made that the outcome of every silent clip recorded while a
          // primary was having a bad hour. (issue #381, review r2)
          mayRefuseEmptyTranscript: !ledger.hasRefused
            && current === provider
            && index < chain.length - 1,
          // The second opinion after a refusal. A Whisper sibling uses it to
          // drop its own silence hallucination ("Thank you.") instead of
          // billing it. See ProviderRequestContext.isEmptyTranscriptRecovery.
          isEmptyTranscriptRecovery: ledger.hasRefused,
          // The request's own measured length, for an adapter whose upstream
          // reports no duration on an empty transcript (elevenlabs). Read only
          // by emptyTranscriptOutcome, and only for an adapter that opts in.
          // See ProviderRequestContext.requestAudioSeconds. (issue #720)
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
      // `chosenProviderAttempted`, not just `current !== provider`: a chosen
      // provider that this region dropped out of the chain was never called, so
      // nothing fell back FROM it. Without the guard a request whose chosen
      // provider is geo-blocked reported `X-STT-Provider: elevenlabs/scribe_v2`
      // for a `no_speech` that deepgram produced and elevenlabs never saw.
      // (review r2)
      if (current !== provider && chosenProviderAttempted) {
        fallbackFrom = provider;
      }
      break;
    }
  } finally {
    // Fire-and-forget, like the credit deduction: the whole attempt chain goes
    // in one POST, and a slow or failing website must never delay a
    // transcript. In a finally so EVERY path out of the loop reports — the
    // early terminal returns included — and so it happens exactly once.
    //
    // Reportability is checked at this single send rather than when a sample is
    // recorded: a request that must not be reported still collects samples, it
    // just never sends them, so they die with the request. Gating the single
    // send is what makes that impossible to leak past — there is no second way
    // out of this loop.
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
