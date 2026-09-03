// TRANSCRIPTION ROUTE
// POST /transcribe - Main transcription endpoint
// Supports multiple STT providers with automatic fallback

import type { Context } from 'hono';
import type { TranscriptionResult } from '../providers/types';
import { AudioTooLargeError, EmptyTranscriptError, ProviderInputError, ProviderUnavailableError, UnsupportedAudioFormatError } from '../providers/types';
// The providers layer's own answer to "which adapter runs this provider id", so
// the route never imports an adapter or keeps a dispatch table of its own. See
// providers/dispatch.ts.
import { transcribeWithProvider } from '../providers/dispatch';
import { parseMetaWav } from '../providers/meta';
import { creditsForCost, estimatePromptInputReservationUsd, formatUsd } from '../lib/cost-calculator';
import {
  fallbackChainFor,
  getProviderDef,
  isSelfOnly,
  resolveModel,
  servedNameFor,
  MEDICAL_DOMAIN,
  type SttProviderId,
} from '../lib/stt-models';
import { readClientInfo } from '../lib/client-info';
import { clientOffersLatencyOptOut } from '../lib/latency-eligibility';
import { generateRequestId, getClientIP, getFlyRequestId } from '../lib/request-id';
import {
  reportLatencySamples,
  type LatencyFailureKind,
  type LatencySample,
} from '../lib/latency-report';
// Content-type aware, unlike the billing estimators: a failed attempt still has
// to land in the right clip-length bucket on the public /latency page.
// runProviderAttempt is how the same page learns whether an attempt ever reached
// the provider at all.
import { estimateAudioSeconds, runProviderAttempt, type ProviderAttemptNetwork } from '../providers/utils';
// The providers layer's own answer to "can this Fly region reach this provider",
// so the route never needs a provider's blocked-region list, its replay region,
// or its id in a filter. See providers/geo-availability.ts.
import { planGeoRouting, reachableFromRegion } from '../providers/geo-availability';
// The providers layer's own answer to "what is the worst USD/min this request
// could be billed at", so the route never needs a provider's add-on eligibility
// or an adapter's internal routing gate. See providers/reservation.ts.
import { maxReservationUsdPerMinute } from '../providers/reservation';
import { rawQuery } from '../lib/query';
import { isIPBlocked } from '../lib/redis';
import { errorResponse } from '../lib/responses';
import { authDiagnosticsForLog, validateAuth } from '../middleware/auth';
import { deductCredits, estimateAudioSecondsFromSize, validateCredits } from '../middleware/credits';
import { flyProxyOverheadMs, logEvent, machineUptimeMs } from '../lib/logging';
import {
  extractDomain,
  extractModel,
  extractProvider,
  isLatencyOptOut,
  validateStreamingHeaders,
} from './transcribe-request';
import { buildTranscriptionSuccess } from './transcribe-success';

// Supported providers (mirror the server-side registry in lib/stt-models.ts).
export type Provider = SttProviderId;

/**
 * Preflight credit reservation: turn a declared Content-Length into the credits
 * to hold before the body is read.
 *
 * Which providers the request could reach, what each of them would charge, and
 * which of them has a routing tier priced above its own catalog are all
 * `maxReservationUsdPerMinute`'s to answer. What is left here is the
 * byte→seconds→credits arithmetic, plus the prompt-token allowance, which is
 * still `lib/cost-calculator.ts`'s and is applied to the primary provider only
 * — see the note in providers/reservation.ts for when that has to move.
 * `model`/`medical` are optional to keep the historical 2-arg signature working.
 */
export function estimateCreditsForProviderFallbacks(
  sizeBytes: number,
  provider: Provider,
  model?: string,
  medical: boolean = false,
  initialPrompt?: string,
  language?: string,
  exactAudioSeconds?: number,
): number {
  // Muse requests are canonical mono PCM16 WAV at 16 or 24 kHz. The route
  // supplies the parsed duration because Content-Length cannot identify which
  // accepted byte rate produced the file. Keep the 16 kHz size fallback for
  // direct historical callers of this estimator; the live route never uses it
  // for a valid Muse WAV.
  const estimatedSeconds = exactAudioSeconds ?? (provider === 'meta'
    ? Math.max(10, Math.max(0, sizeBytes - 44) / (16_000 * 2))
    : estimateAudioSecondsFromSize(sizeBytes));
  const usdPerMinute = maxReservationUsdPerMinute({
    provider,
    model,
    medical,
    hasInitialPrompt: Boolean(initialPrompt),
    language,
    estimatedSeconds,
  });
  // Token-billed providers (Gemini, OpenAI gpt-4o*) charge the prompt text as
  // input tokens on top of the audio. Reserve that flat cost for the primary
  // provider (these are self-only chains) so a large vocabulary prompt on a
  // short clip can't be deducted beyond what was reserved.
  const promptReservationUsd = estimatePromptInputReservationUsd(provider, model, initialPrompt);
  const estimatedCostUsd = (estimatedSeconds / 60) * usdPerMinute + promptReservationUsd;
  return Math.max(0.1, creditsForCost(estimatedCostUsd));
}

export { isLatencyOptOut };

/**
 * The public page's failure taxonomy for whatever the attempt threw. Keeps the
 * mapping in one place so the catch arms below stay pure control flow.
 */
function failureKindFor(error: unknown): LatencyFailureKind {
  if (error instanceof ProviderUnavailableError) return error.kind;
  if (error instanceof ProviderInputError) return 'input_rejected';
  // A revoked key or a bug in an adapter lands here. Without a sample the page
  // would report a 0% error rate for a provider that fails every call.
  return 'unknown';
}

/**
 * How long a failed attempt cost the user, from the route's own clock.
 *
 * Deliberately NOT ProviderUnavailableError.elapsedMs: for the async providers
 * (AssemblyAI, Soniox, Google Chirp) that field times only the single
 * fetchWithTimeout that failed, while the upload, the job creation and every
 * earlier poll are already spent — a 90-second wait reported as the 8 seconds
 * of its last poll. The adapter's own number stays in the structured log, where
 * "which call failed" is the question; the page answers "how long did this
 * take", which is this one.
 */
function elapsedFor(attemptStart: number): number {
  return performance.now() - attemptStart;
}

export async function transcribeRoute(c: Context) {
  const requestId = generateRequestId();
  const startTime = performance.now();
  const clientIP = getClientIP(c);
  const flyRequestId = getFlyRequestId(c);

  // IP block check
  if (await isIPBlocked(clientIP)) {
    logEvent(requestId, startTime, 'transcribe.request_rejected', {
      reason: 'ip_blocked',
      flyRequestId,
    });
    return errorResponse(403, 'Access denied', 'Your IP has been temporarily blocked due to abuse');
  }
  logEvent(requestId, startTime, 'transcribe.ip_check_done', { flyRequestId });

  const providerSelection = extractProvider(c);
  if (!providerSelection.ok) {
    logEvent(requestId, startTime, 'transcribe.request_rejected', {
      reason: 'invalid_provider',
      flyRequestId,
      provided: providerSelection.provided,
    });
    return errorResponse(400, 'Invalid STT provider',
      `Unknown X-STT-Provider "${providerSelection.provided}".`,
      { requestId, provided: providerSelection.provided },
    );
  }
  const provider = providerSelection.provider;

  // Resolve + validate the requested model against the server-side registry.
  // An unknown model for the provider is rejected (fail-closed) rather than
  // silently routed to the provider default at a possibly different price.
  const requestedModel = extractModel(c);
  const modelResolution = resolveModel(provider, requestedModel);
  if (!modelResolution.ok) {
    logEvent(requestId, startTime, 'transcribe.request_rejected', {
      reason: 'invalid_model',
      flyRequestId,
      provider,
      requestedModel,
    });
    return errorResponse(400, 'Invalid STT model', modelResolution.reason, {
      requestId,
      provider,
      requested_model: requestedModel,
      valid_models: modelResolution.validModels,
    });
  }
  const model = modelResolution.model.id;

  const domain = extractDomain(c);
  // Medical add-on only applies where the provider meters it (AssemblyAI today).
  const medical = domain === MEDICAL_DOMAIN;

  const headerValidation = validateStreamingHeaders(c, provider);
  if (!headerValidation.ok) {
    logEvent(requestId, startTime, 'transcribe.request_rejected', {
      reason: 'invalid_streaming_headers',
      flyRequestId,
      provider,
      status: headerValidation.response.status,
    });
    return headerValidation.response;
  }

  const { contentType, contentLength } = headerValidation;
  // rawQuery, not c.req.query(): Hono's decoder adds an HTML-form `+` → space
  // step, corrupting values like a `C++` vocabulary term. See lib/query.ts.
  const language = rawQuery(c.req.url, 'language');
  const initialPrompt = rawQuery(c.req.url, 'initial_prompt');
  const mode = rawQuery(c.req.url, 'mode');

  // Some providers are unreachable from the region this machine runs in. The
  // providers layer owns which ones, from where, and where to send the request
  // instead — the route only carries out the plan it is handed, before doing
  // any auth/credit work. A replay adds ~50-80ms vs ~6s of certain failure.
  // See providers/geo-availability.ts.
  const geoPlan = planGeoRouting(provider, contentLength);
  if (geoPlan.action === 'replay') {
    logEvent(requestId, startTime, 'transcribe.fly_replay', {
      flyRequestId,
      provider,
      fromRegion: geoPlan.fromRegion,
      toRegion: geoPlan.toRegion,
      reason: geoPlan.reason,
    });
    c.header('fly-replay', `region=${geoPlan.toRegion}`);
    return c.body(null, 200);
  }
  // The body was too large for Fly to replay, so the request stays in this
  // region and the unreachable provider comes out of the chain below.
  if (geoPlan.action === 'drop_from_chain') {
    logEvent(requestId, startTime, 'transcribe.fly_replay_skipped_oversized', {
      flyRequestId,
      provider,
      flyRegion: geoPlan.fromRegion,
      contentLength,
      replayMaxBytes: geoPlan.replayMaxBytes,
    });
  }

  const proxyOverheadMs = flyProxyOverheadMs(c.req.header('Fly-Request-Start'));
  const { clientPlatform, clientVersion } = readClientInfo(c);
  logEvent(requestId, startTime, 'transcribe.request_start', {
    flyRequestId,
    clientPlatform,
    clientVersion,
    flyRegion: process.env.FLY_REGION || 'local',
    flyMachineId: process.env.FLY_MACHINE_ID,
    proxyOverheadMs,
    provider,
    model: model || 'default',
    domain: domain || 'none',
    contentType,
    contentLength,
    language: language || 'auto',
    hasInitialPrompt: Boolean(initialPrompt),
    mode: mode || 'default',
  });

  // Auth (query params only) — Cloud is licensed-only; a valid account key is required.
  // `account_key` is the canonical param name; `license_key` is the legacy alias
  // that installed native apps still send, so we accept either.
  const authResult = await validateAuth({
    licenseKey:
      rawQuery(c.req.url, 'account_key') ?? rawQuery(c.req.url, 'license_key'),
  });
  if (!authResult.ok) {
    logEvent(requestId, startTime, 'transcribe.request_rejected', {
      reason: 'auth_failed',
      flyRequestId,
      status: authResult.response.status,
      ...authDiagnosticsForLog(authResult.diagnostics),
    });
    return authResult.response;
  }
  logEvent(requestId, startTime, 'transcribe.auth_done', authDiagnosticsForLog(authResult.diagnostics));

  const readAudioBuffer = async (): Promise<ArrayBuffer> => {
    const uploadStart = performance.now();
    const body = await c.req.arrayBuffer();
    const uploadMs = Math.round(performance.now() - uploadStart);
    const uploadBytesPerSec = uploadMs > 0
      ? Math.round((body.byteLength / uploadMs) * 1000)
      : undefined;
    logEvent(requestId, startTime, 'transcribe.buffer_read_done', {
      audioBytes: body.byteLength,
      uploadMs,
      uploadBytesPerSec,
    });
    return body;
  };

  // Meta needs the buffered WAV to calculate an exact reservation. Before that
  // allocation, reserve the lowest possible cost for this byte count: accepted
  // 24 kHz mono PCM16 has the highest byte rate, so any canonical Muse WAV of
  // this size is at least this long. The exact duration check below still owns
  // the final amount and increases it for 16 kHz audio.
  if (provider === 'meta') {
    const minimumAudioSeconds = Math.max(0, contentLength - 44) / (24_000 * 2);
    const minimumEstimatedCredits = estimateCreditsForProviderFallbacks(
      contentLength, provider, model, medical, initialPrompt, language, minimumAudioSeconds,
    );
    const minimumCreditCheck = await validateCredits(
      authResult.value, minimumEstimatedCredits, clientIP,
    );
    if (!minimumCreditCheck.ok) {
      logEvent(requestId, startTime, 'transcribe.request_rejected', {
        reason: 'credits_failed_before_buffer',
        flyRequestId,
        status: minimumCreditCheck.response.status,
        estimatedCredits: minimumEstimatedCredits,
      });
      return minimumCreditCheck.response;
    }
    logEvent(requestId, startTime, 'transcribe.credits_minimum_done', {
      estimatedCredits: minimumEstimatedCredits,
    });
  }

  // Meta billing is duration-based while its two accepted PCM sample rates
  // have different byte rates. Read this finite-capped body and parse the WAV
  // before reservation; Content-Length cannot distinguish a 60-second 24 kHz
  // clip from a 90-second 16 kHz clip. Invalid/noncanonical audio deliberately
  // skips reservation and continues to the adapter, which returns the 415/400
  // that tells a native client whether to normalize and retry.
  let audioBuffer: ArrayBuffer | undefined;
  let exactAudioSeconds: number | undefined;
  let skipCreditValidationForLocalInputError = false;
  if (provider === 'meta') {
    audioBuffer = await readAudioBuffer();
    try {
      exactAudioSeconds = parseMetaWav(audioBuffer, contentType).durationSeconds;
    } catch (error) {
      if (error instanceof UnsupportedAudioFormatError || error instanceof ProviderInputError) {
        skipCreditValidationForLocalInputError = true;
      } else {
        throw error;
      }
    }
  }

  // The raw request values go in as they arrived — the initial_prompt, the
  // domain and the language are all things the reservation prices for itself.
  // See providers/reservation.ts for which of them cost what, and why.
  const estimatedCredits = estimateCreditsForProviderFallbacks(
    contentLength, provider, model, medical, initialPrompt, language, exactAudioSeconds,
  );
  if (!skipCreditValidationForLocalInputError) {
    const creditCheck = await validateCredits(authResult.value, estimatedCredits, clientIP);
    if (!creditCheck.ok) {
      logEvent(requestId, startTime, 'transcribe.request_rejected', {
        reason: 'credits_failed',
        flyRequestId,
        status: creditCheck.response.status,
        estimatedCredits,
      });
      return creditCheck.response;
    }
    logEvent(requestId, startTime, 'transcribe.credits_done', { estimatedCredits });
  } else {
    logEvent(requestId, startTime, 'transcribe.credits_skipped_invalid_audio', { provider });
  }

  audioBuffer ??= await readAudioBuffer();

  // The credit check above trusted the declared Content-Length. Reject bodies
  // that arrive larger than declared so a client can't under-declare to pass
  // validateCredits cheaply and then stream a bigger payload we'd pay the
  // provider for (issue ray-amjad/hyperwhisper#263). Honest clients always
  // send a body that matches Content-Length exactly.
  if (audioBuffer.byteLength > contentLength) {
    logEvent(requestId, startTime, 'transcribe.request_rejected', {
      reason: 'content_length_mismatch',
      flyRequestId,
      declaredBytes: contentLength,
      actualBytes: audioBuffer.byteLength,
    });
    return errorResponse(400, 'Content-Length mismatch',
      `Request body (${audioBuffer.byteLength} bytes) exceeds the declared Content-Length (${contentLength} bytes)`,
      { requestId, declared_bytes: contentLength, actual_bytes: audioBuffer.byteLength },
    );
  }

  let result: TranscriptionResult | undefined;
  let fallbackFrom: Provider | undefined;
  let fallbackCount = 0;
  // The model that actually produced the result. Defaults to the requested
  // model; on a cross-provider fallback it becomes that sibling's default model.
  let usedModel = model;
  // The provider whose attempt produced `result`. `result.source` names it on a
  // transcript, but a `no_speech` result's source is the literal `'no_speech'`,
  // so the only record of who answered is this. (review r2)
  let servedBy: Provider | undefined;
  // Whether the provider the caller ASKED for was ever actually attempted. It
  // is not always in the chain: an oversized upload from a region that geo-blocks
  // the chosen provider drops it, and the request is served by a sibling from
  // position 0. Attributing that request's outcome to the chosen provider would
  // name a provider we never called — which is exactly what the `no_speech`
  // attribution and the `fallback from` note below both used to do.
  // (review r2)
  let chosenProviderAttempted = false;

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
  let lastError: Error | undefined;
  let lastInputError: ProviderInputError | undefined;
  let sawUnavailable = false;
  // Chain position of the one attempt that refused an empty transcript
  // (EmptyTranscriptError), if any. It is the single piece of state that makes
  // this failure class different from every other one in the loop, and it does
  // exactly two things:
  //
  //   1. It marks that `result` already holds a benign `no_speech` — the FLOOR of
  //      this request. A refusal must never turn what would have been a 200
  //      `no_speech` into an error, so every later arm (a sibling that is
  //      rate-limited, one with no API key configured, one that rejects the
  //      audio) settles on that floor instead of a 429/500/502.
  //   2. It closes the door. No further attempt is authorised to refuse.
  //
  // (issue ray-amjad/hyperwhisper-app#381)
  let refusalIndex: number | undefined;
  // How much of the one extra UPSTREAM CALL the spec budgets has been spent.
  //
  // Deliberately NOT `index > refusalIndex + 1`. A chain POSITION is not a call:
  // `DEEPGRAM_API_KEY` unset makes the adapter throw before any fetch, and the
  // positional rule charged that non-event to the budget and then stopped —
  // returning a `no_speech` for 22 s of real speech while groq and elevenlabs,
  // both configured and both able to transcribe, were never asked. The budget is
  // spent only by an attempt that reached the wire, which the loop already knows
  // from `ProviderAttemptNetwork.reachedProvider`. (review r2)
  let recoveryCallsSpent = 0;
  // Per-attempt failure breadcrumbs, surfaced on the final outcome log so one
  // line explains a degraded/failed request (which provider failed, why, how
  // long it hung) without correlating separate provider-level log events.
  const attemptFailures: Array<{
    provider: Provider;
    kind: string;
    status?: number;
    attemptMs?: number;
    /**
     * The attempt was an empty-transcript refusal, not a provider fault. Inside
     * the shared `bad_response` kind nothing else separates it from a geo-block
     * or a truncated body. Log-only, like every other field here: `/transcribe`'s
     * response body is a client contract (`hyperwhisper-cloud/CLAUDE.md`, "must
     * land in clients in the same PR cycle") and this is operator data.
     */
    emptyTranscript?: true;
  }> = [];
  // Anonymous per-attempt timings for the public /latency page. Collected here
  // and sent once, after the response is decided, so reporting never adds wall
  // time to the latency it is measuring.
  const latencySamples: LatencySample[] = [];
  // Read once, up front: neither input can change mid-request, and the send
  // site below is the only thing that consults the result.
  //
  // Two independent reasons not to report, both resolved here. The header is
  // the user's live answer. Eligibility is whether they were ever asked: the
  // opt-out switch shipped in macOS 2.43.0 and Windows 1.10.0, and sharing is
  // on by default, so recording an older build would apply that default to
  // someone who had no way to decline it. See lib/latency-eligibility.ts.
  const latencyOptOut = isLatencyOptOut(c);
  const latencyEligibleClient = clientOffersLatencyOptOut(clientPlatform, clientVersion);
  const latencyReportable = !latencyOptOut && latencyEligibleClient;
  // The clip length every row of this request is filed under — one estimate,
  // from the bytes on the wire and the Content-Type describing them, used
  // identically on success and on failure.
  //
  // Deliberately NOT the adapter's `result.durationSeconds`. That is a BILLING
  // number, and when an upstream omits a duration the adapters fall back to
  // estimateSecondsFromBytes() — a flat 64 kbps assumption that overstates the
  // 16 kHz/16-bit mono WAV both desktop apps upload by ~4x. openai's default
  // model (gpt-4o-transcribe) reports only tokens, so it takes that fallback on
  // every call, and mistral/soniox/assemblyai take it whenever upstream omits a
  // duration: a 3-second dictation would be stored as 12 seconds and bucketed
  // 'medium'. Preferring it on success and estimating on failure also made the
  // two incomparable, and put one clip in different buckets depending on
  // whether the provider that answered happened to report a length. One
  // estimator for every row is what makes a cell a like-for-like comparison.
  const audioSeconds = estimateAudioSeconds(audioBuffer.byteLength, contentType);

  // The one place an attempt becomes a sample. Every arm out of the loop below
  // goes through it — success, retryable failure, and the failures that end the
  // request outright — so "one row per attempt" holds by construction instead
  // of by remembering to push. The loop is wrapped in a try/finally that sends
  // whatever this collected, so an early return can no longer lose the most
  // interesting rows this page has.
  const recordAttempt = (sample: {
    provider: Provider;
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
  }) => {
    latencySamples.push({
      provider: sample.provider,
      model: sample.model || undefined,
      latencyMs: sample.latencyMs,
      ok: sample.failureKind === undefined,
      failureKind: sample.failureKind,
      attempt: sample.index + 1,
      audioSeconds,
    });
  };

  try {
    for (const [index, current] of chain.entries()) {
      // The one extra call this feature is allowed, already spent. Stopping here
      // is not giving up: the refusal banked a `no_speech` in `result`, so the
      // request answers with that. Walking on would cost the third and fourth
      // upstream call the spec priced out. (issue #381)
      if (refusalIndex !== undefined && recoveryCallsSpent > 0) break;

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
      // ProviderAttemptNetwork in providers/utils.ts for why the signal is taken
      // at the wire rather than from a list of error types.
      const network: ProviderAttemptNetwork = { reachedProvider: false };

      try {
        result = await runProviderAttempt(network, () => transcribeWithProvider(current, audioBuffer, contentType, language, initialPrompt, {
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
          mayRefuseEmptyTranscript: refusalIndex === undefined
            && current === provider
            && index < chain.length - 1,
        }));
        servedBy = current;
        // Prefer the model the adapter reports it ACTUALLY ran (e.g. AssemblyAI's
        // universal-3-5-pro → universal-2 fallback for unsupported languages) so the
        // X-STT-Model header and deduction metadata match what was billed; fall
        // back to the attempted model when the adapter doesn't report one.
        usedModel = result.model || attemptModel;
        // `chosenProviderAttempted`, not just `current !== provider`: a chosen
        // provider that this region dropped out of the chain was never called, so
        // nothing fell back FROM it. Without the guard a request whose chosen
        // provider is geo-blocked reported `X-STT-Provider: elevenlabs/scribe_v2`
        // for a `no_speech` that deepgram produced and elevenlabs never saw.
        // (review r2)
        if (current !== provider && chosenProviderAttempted) {
          fallbackFrom = provider;
        }
        const attemptMs = performance.now() - attemptStart;
        logEvent(requestId, startTime, 'transcribe.provider_attempt_done', {
          provider: current,
          model: attemptModel || 'default',
          attempt: index + 1,
          upstreamRequestId: result.requestId,
          transcriptChars: result.text.length,
          resultSource: result.source,
          attemptMs: Math.round(attemptMs),
        });
        recordAttempt({
          provider: current,
          model: usedModel,
          index,
          latencyMs: attemptMs,
        });
        break;
      } catch (error) {
        // ONE row per attempt, recorded here before any branching: the arms
        // below are pure control flow (log, fall back, or return) and none of
        // them can forget a sample or file a second one.
        //
        // The single condition is whether the attempt ever reached the wire.
        // Everything an adapter rejects on its own — a missing API key, a size
        // cap, a content-type it can't take — throws in microseconds without
        // calling anyone, and a row for it would publish that provider
        // "answering" in 1 ms and failing a call it never received. Every real
        // provider failure (timeout, 5xx, rate limit, an unusable 2xx, an
        // upstream 4xx) happens strictly after the request went out, so it is
        // still recorded — that direction is the bug this must not reintroduce.
        if (network.reachedProvider) {
          recordAttempt({
            provider: current,
            model: attemptModel,
            index,
            latencyMs: elapsedFor(attemptStart),
            failureKind: failureKindFor(error),
          });
          // The same signal, read a second way: an attempt made AFTER a refusal
          // that reached the wire is the one extra upstream call the spec budgets,
          // spent. One that never reached it (no API key, a size cap, a
          // content-type gate) cost nothing and must not close the door on a
          // sibling that could still answer. (issue #381, review r2)
          if (refusalIndex !== undefined) {
            recoveryCallsSpent += 1;
          }
        }

        // A 200 with no transcript for audio the upstream says it processed.
        // Handled before the generic ProviderUnavailableError arm it extends,
        // because this is the one failure in the loop that is not a fault: the
        // request already has a valid answer in hand and is only asking a sibling
        // whether it can do better. (issue #381)
        if (error instanceof EmptyTranscriptError) {
          const next = chain[index + 1];
          fallbackCount += 1;
          refusalIndex = index;
          // The floor. If nothing after this produces text, THIS is the response:
          // the same 200, the same 0 credits and the same `no_speech_detected`
          // the user would have got before the failover existed. Overwritten
          // wholesale by any later attempt that succeeds.
          result = error.noSpeechResult;
          servedBy = current;
          logEvent(requestId, startTime, 'transcribe.provider_attempt_fail', {
            provider: current,
            attempt: index + 1,
            kind: 'provider_unavailable',
            unavailableKind: error.kind,
            attemptMs: error.elapsedMs,
            message: error.message,
            // `provider_attempt_done` carries the upstream's id on every other
            // path; this one never produces a result to read it off, and #381 was
            // filed about precisely the call an operator now has to report to the
            // vendor. The adapter's `provider.no_speech` event carries it too.
            upstreamRequestId: error.noSpeechResult.requestId,
            upstreamDurationSeconds: error.upstreamDurationSeconds,
            nextProvider: next,
          });
          attemptFailures.push({
            provider: current,
            kind: error.kind,
            attemptMs: error.elapsedMs,
            emptyTranscript: true,
          });
          lastError = error;
          sawUnavailable = true;
          continue;
        }
        if (error instanceof ProviderUnavailableError) {
          const next = chain[chain.indexOf(current) + 1];
          fallbackCount += 1;
          // `unavailableKind` distinguishes the root cause inline — `timeout`
          // (we gave up; upstream may have been fine) vs `upstream_5xx` /
          // `rate_limit` (upstream actually failed) vs `bad_response` (geo-block
          // HTML / empty body) — instead of the old catch-all `provider_unavailable`.
          logEvent(requestId, startTime, 'transcribe.provider_attempt_fail', {
            provider: current,
            attempt: index + 1,
            kind: 'provider_unavailable',
            unavailableKind: error.kind,
            upstreamStatus: error.status,
            attemptMs: error.elapsedMs,
            message: error.message,
            nextProvider: next,
          });
          attemptFailures.push({
            provider: current,
            kind: error.kind,
            status: error.status,
            attemptMs: error.elapsedMs,
          });
          lastError = error;
          sawUnavailable = true;
          continue;
        }
        if (error instanceof ProviderInputError) {
          // The provider rejected this specific input (e.g. ElevenLabs 400 on a
          // language code it doesn't accept). A sibling provider may accept the
          // same input, so continue the fallback chain instead of failing the
          // whole request. (issue ray-amjad/hyperwhisper#333)
          const next = chain[chain.indexOf(current) + 1];
          fallbackCount += 1;
          logEvent(requestId, startTime, 'transcribe.provider_attempt_fail', {
            provider: current,
            attempt: index + 1,
            kind: 'provider_input_rejected',
            status: error.status,
            message: error.message,
            nextProvider: next,
          });
          lastError = error;
          lastInputError = error;
          continue;
        }
        // Everything below this line ENDS the request with an error. None of them
        // may fire once a refusal has banked a benign `no_speech`: the user asked
        // a question that already has a valid, free answer, and a sibling's 413,
        // 415, missing API key or 500 is our problem, not theirs. One guard, above
        // every terminal arm, so a new arm cannot be added below it and quietly
        // reintroduce the regression. (issue #381)
        //
        // It records and CONTINUES rather than breaking, for two reasons. The
        // budget is counted in wire calls above, so a sibling that failed before
        // the wire left it unspent and the next sibling is still owed the request
        // (a bare `break` here answered #381's literal incident — 22 s of real
        // speech — with a `no_speech`, having called nobody but the provider that
        // refused). And a bare `break` also bypassed the failure log entirely, so
        // the final `no_speech` gave an operator no indication that a sibling had
        // been tried at all, let alone why it failed. (review r2)
        if (refusalIndex !== undefined) {
          const terminalKind = error instanceof AudioTooLargeError
            ? 'audio_too_large'
            : error instanceof UnsupportedAudioFormatError
              ? 'unsupported_audio_format'
              : 'non_retryable';
          fallbackCount += 1;
          logEvent(requestId, startTime, 'transcribe.provider_attempt_fail', {
            provider: current,
            attempt: index + 1,
            kind: terminalKind,
            message: error instanceof Error ? error.message : String(error),
            attemptMs: Math.round(elapsedFor(attemptStart)),
            // The request is NOT ending here. Without this an operator reading the
            // line would expect the matching `request_fail` that never comes.
            afterEmptyTranscriptRefusal: true,
            nextProvider: chain[index + 1],
          });
          attemptFailures.push({
            provider: current,
            kind: terminalKind,
            attemptMs: Math.round(elapsedFor(attemptStart)),
          });
          lastError = error instanceof Error ? error : new Error(String(error));
          continue;
        }
        if (error instanceof AudioTooLargeError) {
          logEvent(requestId, startTime, 'transcribe.request_fail', {
            provider: current,
            attempt: index + 1,
            kind: 'audio_too_large',
            message: error.message,
            actualBytes: error.actualBytes,
            maxBytes: error.maxBytes,
          });
          return errorResponse(413, 'Audio too large for provider',
            `${servedNameFor(current)} accepts at most ${Math.round(error.maxBytes / (1024 * 1024))} MB inline. Your audio is ${(error.actualBytes / (1024 * 1024)).toFixed(2)} MB.`,
            { requestId, provider: current, max_size_mb: Math.round(error.maxBytes / (1024 * 1024)), actual_size_mb: parseFloat((error.actualBytes / (1024 * 1024)).toFixed(2)) },
          );
        }
        if (error instanceof UnsupportedAudioFormatError) {
          logEvent(requestId, startTime, 'transcribe.request_fail', {
            provider: current,
            attempt: index + 1,
            kind: 'unsupported_audio_format',
            message: error.message,
            receivedContentType: error.contentType,
            acceptedFormats: error.acceptedFormats,
          });
          return errorResponse(415, 'Unsupported audio format for provider',
            `${servedNameFor(current)} accepts only ${error.acceptedFormats.join(', ')}. Received Content-Type: ${error.contentType}.`,
            {
              requestId,
              provider: current,
              received_content_type: error.contentType,
              accepted_formats: error.acceptedFormats,
            },
          );
        }
        // Non-retryable error (401 invalid key, etc.) — don't try fallbacks
        logEvent(requestId, startTime, 'transcribe.request_fail', {
          provider: current,
          attempt: index + 1,
          kind: 'non_retryable',
          message: error instanceof Error ? error.message : String(error),
        });
        return errorResponse(500, 'Transcription failed', error instanceof Error ? error.message : String(error), { requestId });
      }
    }
  } finally {
    // Fire-and-forget, like the credit deduction below: the whole attempt chain
    // goes in one POST, and a slow or failing website must never delay a
    // transcript. In a finally so EVERY path out of the loop reports — the
    // early returns above included — and so it happens exactly once.
    //
    // Reportability is checked here rather than at recordAttempt: a request
    // that must not be reported still collects samples, it just never sends
    // them, so they die with the request. Gating the single send is what makes
    // that impossible to leak past — there is no second way out of this loop.
    if (latencyReportable) {
      reportLatencySamples(latencySamples);
    }
  }

  // All providers in the chain failed.
  if (!result) {
    // Every provider rejected the input with a non-auth 4xx and none was merely
    // unavailable — the input itself is the problem, so a retry won't help.
    // Surface a 400 with the upstream message instead of a misleading 429/502
    // ("rate-limited"/"unavailable") that would have the client back off and
    // retry the same bad request. (issue ray-amjad/hyperwhisper#333)
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

    // Self-only chains (e.g. azure-mai, google-chirp) mean the user explicitly
    // opted into a single upstream. Surfacing a 429 implies "we'll retry
    // through siblings, just back off" — which is a lie when there are no
    // siblings. Return 502 with the upstream's actual error message so client
    // retry logic doesn't storm against a broken region.
    //
    // Ask the registry rather than measuring `chain`: that array is this
    // request's own copy and may already have had a provider filtered out of
    // it (the ElevenLabs geo-block above), so its length answers "how many did
    // we try here", not "does this provider have siblings at all".
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
      authResult.value,
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
