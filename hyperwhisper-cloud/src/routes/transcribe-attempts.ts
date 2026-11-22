import type { LatencyFailureKind } from '../lib/latency-report';
import { logEvent } from '../lib/logging';
import { errorResponse, sizeLimitFields, sizeLimitWords } from '../lib/responses';
import { isSelfOnly, servedNameFor, type SttProviderId } from '../lib/stt-models';
import {
  AudioTooLargeError,
  EmptyTranscriptError,
  ProviderAuthError,
  ProviderInputError,
  ProviderUnavailableError,
  UnsupportedAudioFormatError,
  type TranscriptionResult,
} from '../providers/types';
import type { createTranscriptionLatencyRecorder } from './transcribe-latency';

// The one message a client sees for a non-retryable failure. A failure's own
// text can carry an upstream provider's error body or a server env-var name
// (#2106), so it goes to the `transcribe.request_fail` log only; the client
// keeps the requestId to find that log line. Same shape as /post-process (#2066).
const TRANSCRIPTION_FAILURE_MESSAGE = 'The transcription request failed. Please try again.';

/**
 * One failed attempt as it appears on the outcome log line (`request_done` /
 * `request_fail`): per-attempt breadcrumbs, so one line explains a
 * degraded/failed request (which provider failed, why, how long it hung)
 * without correlating separate provider-level log events. Log-only:
 * `/transcribe`'s response body is a client contract
 * (`hyperwhisper-cloud/CLAUDE.md`, "must land in clients in the same PR
 * cycle") and this is operator data.
 */
export interface AttemptFailure {
  provider: SttProviderId;
  kind: string;
  status?: number;
  attemptMs?: number;
  /**
   * An empty-transcript refusal, not a provider fault. Inside the shared
   * `bad_response` kind nothing else separates it from a geo-block or a
   * truncated body.
   */
  emptyTranscript?: true;
}

interface OutcomeBase {
  provider: SttProviderId;
  /** The model the attempt was made with. */
  model: string | undefined;
  /** 0-based chain position. */
  index: number;
  /** Wall time of this provider call alone, measured by the loop. */
  attemptMs: number;
  /**
   * Whether the attempt's first request left the process. Adapter-side gates
   * (missing API key, size cap, content-type check) throw before it and are not
   * measurements. See ProviderAttemptNetwork in providers/provider-attempt.ts.
   */
  reachedWire: boolean;
  status?: number;
}

/**
 * Every way an attempt can fail. A `retryable` outcome hands the request to the
 * next provider in the chain; any other one ends the request with its own
 * response, unless an empty-transcript refusal already banked a `no_speech`.
 */
export type AttemptOutcome = OutcomeBase & (
  | { kind: 'empty_transcript'; retryable: true; error: EmptyTranscriptError }
  | { kind: 'unavailable'; retryable: true; error: ProviderUnavailableError }
  | { kind: 'input_rejected'; retryable: true; error: ProviderInputError }
  | { kind: 'auth_rejected'; retryable: true; error: ProviderAuthError }
  | { kind: 'audio_too_large'; retryable: false; error: AudioTooLargeError }
  | { kind: 'unsupported_audio_format'; retryable: false; error: UnsupportedAudioFormatError }
  | { kind: 'non_retryable'; retryable: false; error: Error }
);

interface AttemptContext {
  provider: SttProviderId;
  model: string | undefined;
  index: number;
  attemptStart: number;
  reachedWire: boolean;
  /** Whether a later chain member exists to take over. */
  hasNext: boolean;
}

/** Classify what one attempt threw, once. */
export function classifyAttemptError(error: unknown, ctx: AttemptContext): AttemptOutcome {
  const base = {
    provider: ctx.provider,
    model: ctx.model,
    index: ctx.index,
    attemptMs: performance.now() - ctx.attemptStart,
    reachedWire: ctx.reachedWire,
  };
  // Before ProviderUnavailableError, which it extends: the request already has
  // a valid answer and is only asking a sibling whether it can do better.
  if (error instanceof EmptyTranscriptError) {
    return { ...base, kind: 'empty_transcript', retryable: true, error, status: error.status };
  }
  if (error instanceof ProviderUnavailableError) {
    return { ...base, kind: 'unavailable', retryable: true, error, status: error.status };
  }
  // A provider rejected this specific input (e.g. ElevenLabs 400 on a language
  // code it doesn't accept); a sibling may accept it. (ray-amjad/hyperwhisper#333)
  if (error instanceof ProviderInputError) {
    return { ...base, kind: 'input_rejected', retryable: true, error, status: error.status };
  }
  // Our key for THIS provider was rejected. Each sibling has its own key, so
  // the next one can still serve the request; only the last provider's
  // rejected key ends the request (the 500 in terminalResponse).
  if (error instanceof ProviderAuthError && ctx.hasNext) {
    return { ...base, kind: 'auth_rejected', retryable: true, error, status: error.status };
  }
  if (error instanceof AudioTooLargeError) {
    return { ...base, kind: 'audio_too_large', retryable: false, error };
  }
  if (error instanceof UnsupportedAudioFormatError) {
    return { ...base, kind: 'unsupported_audio_format', retryable: false, error };
  }
  return {
    ...base,
    kind: 'non_retryable',
    retryable: false,
    error: error instanceof Error ? error : new Error(String(error)),
  };
}

/** The public /latency page's failure taxonomy. */
function latencyFailureKind(outcome: AttemptOutcome): LatencyFailureKind {
  if (outcome.kind === 'empty_transcript' || outcome.kind === 'unavailable') return outcome.error.kind;
  if (outcome.kind === 'input_rejected') return 'input_rejected';
  // A revoked key or a bug in an adapter. Without a sample the page would
  // report a 0% error rate for a provider that fails every call.
  return 'unknown';
}

/**
 * The 500 for a non-retryable failure. A rejected key keeps its curated text
 * (`<Provider> API key is invalid`, our own words), whether it was the last
 * provider's or one rejected earlier in the chain. Any other error's text stays
 * in the `request_fail` log line. (#2106)
 */
function transcriptionFailedResponse(requestId: string, error: Error): Response {
  return errorResponse(500, 'Transcription failed',
    error instanceof ProviderAuthError ? error.message : TRANSCRIPTION_FAILURE_MESSAGE,
    { requestId },
  );
}

/** The response, and `request_fail` line, of a non-retryable outcome. */
function terminalResponse(outcome: AttemptOutcome, requestId: string, startTime: number): Response {
  const { provider, index } = outcome;
  if (outcome.kind === 'audio_too_large') {
    const { error } = outcome;
    logEvent(requestId, startTime, 'transcribe.request_fail', {
      provider,
      attempt: index + 1,
      kind: 'audio_too_large',
      message: error.message,
      actualBytes: error.actualBytes,
      maxBytes: error.maxBytes,
    });
    const words = sizeLimitWords(error.actualBytes, error.maxBytes);
    return errorResponse(413, 'Audio too large for provider',
      `${servedNameFor(provider)} accepts at most ${words.max} inline. Your audio is ${words.actual}.`,
      { requestId, provider, ...sizeLimitFields(error.actualBytes, error.maxBytes) },
    );
  }
  if (outcome.kind === 'unsupported_audio_format') {
    const { error } = outcome;
    logEvent(requestId, startTime, 'transcribe.request_fail', {
      provider,
      attempt: index + 1,
      kind: 'unsupported_audio_format',
      message: error.message,
      receivedContentType: error.contentType,
      acceptedFormats: error.acceptedFormats,
    });
    return errorResponse(415, 'Unsupported audio format for provider',
      `${servedNameFor(provider)} accepts only ${error.acceptedFormats.join(', ')}. Received Content-Type: ${error.contentType}.`,
      {
        requestId,
        provider,
        received_content_type: error.contentType,
        accepted_formats: error.acceptedFormats,
      },
    );
  }
  logEvent(requestId, startTime, 'transcribe.request_fail', {
    provider,
    attempt: index + 1,
    kind: 'non_retryable',
    message: outcome.error.message,
  });
  return transcriptionFailedResponse(requestId, outcome.error);
}

/** The `provider_attempt_fail` line and the `attemptFailures` row of a recorded outcome. */
function failureRecord(outcome: AttemptOutcome, nextProvider: SttProviderId | undefined) {
  const { provider, index, error } = outcome;
  const attempt = index + 1;
  const attemptMs = Math.round(outcome.attemptMs);
  switch (outcome.kind) {
    case 'empty_transcript': {
      const e = outcome.error;
      return {
        log: {
          provider,
          attempt,
          kind: 'provider_unavailable',
          unavailableKind: e.kind,
          attemptMs: e.elapsedMs,
          message: e.message,
          // `provider_attempt_done` carries the upstream's id on every other
          // path; this one never produces a result to read it off, and #381 was
          // filed about precisely the call an operator now has to report to the
          // vendor. The adapter's `provider.no_speech` event carries it too.
          upstreamRequestId: e.noSpeechResult.requestId,
          upstreamDurationSeconds: e.upstreamDurationSeconds,
          // Set only when the refusal rested on the request's own length
          // because the upstream reported none (elevenlabs, issue #720).
          ...(e.requestAudioSeconds !== null ? { requestAudioSeconds: e.requestAudioSeconds } : {}),
          nextProvider,
        },
        failure: { provider, kind: e.kind, attemptMs: e.elapsedMs, emptyTranscript: true } as AttemptFailure,
      };
    }
    case 'unavailable': {
      // `unavailableKind`: `timeout` (we gave up; upstream may have been fine) vs
      // `upstream_5xx` / `rate_limit` vs `bad_response` (geo-block HTML / empty body).
      const e = outcome.error;
      return {
        log: {
          provider,
          attempt,
          kind: 'provider_unavailable',
          unavailableKind: e.kind,
          upstreamStatus: e.status,
          attemptMs: e.elapsedMs,
          message: e.message,
          nextProvider,
        },
        failure: { provider, kind: e.kind, status: e.status, attemptMs: e.elapsedMs },
      };
    }
    case 'input_rejected':
    case 'auth_rejected':
      return {
        log: {
          provider,
          attempt,
          kind: outcome.kind === 'input_rejected' ? 'provider_input_rejected' : 'provider_auth_rejected',
          status: outcome.status,
          message: error.message,
          nextProvider,
        },
        failure: { provider, kind: outcome.kind, status: outcome.status, attemptMs },
      };
    default:
      return {
        log: {
          provider,
          attempt,
          kind: outcome.kind,
          message: error.message,
          attemptMs,
          // The request is NOT ending here; without this an operator would
          // expect the matching `request_fail` that never comes.
          afterEmptyTranscriptRefusal: true,
          nextProvider,
        },
        failure: { provider, kind: outcome.kind, attemptMs },
      };
  }
}

interface AttemptAnswer {
  result: TranscriptionResult;
  /**
   * The provider whose attempt produced `result`. `result.source` names it on a
   * transcript, but a `no_speech` result's source is the literal `'no_speech'`,
   * so the only record of who answered is this. (review r2)
   */
  servedBy: SttProviderId;
  /** The model that produced `result`; absent for a banked `no_speech`. */
  usedModel?: string;
}

/**
 * The attempt loop's state, reduced over one outcome per attempt.
 */
export class AttemptLedger {
  fallbackCount = 0;
  readonly attemptFailures: AttemptFailure[] = [];
  private readonly outcomes: AttemptOutcome[] = [];
  /** A success, or the `no_speech` floor an empty-transcript refusal banked. */
  answer: AttemptAnswer | undefined;
  /**
   * Set once an attempt refused an empty transcript (issue #381). Its
   * `no_speech` is the floor of the request: a refusal must never turn what
   * would have been a 200 `no_speech` into an error, so every later failure (a
   * sibling that is rate-limited, one with no API key configured, one that
   * rejects the audio) settles on that floor instead of a 429/500/502. And no
   * later attempt may refuse.
   */
  private refused = false;
  /**
   * Upstream calls spent after the refusal. The spec budgets one; only an
   * attempt that reached the wire spends it, so a sibling with no API key
   * configured does not close the door on one that could answer.
   *
   * Deliberately NOT a chain-position rule (`index > refusalIndex + 1`). A
   * position is not a call: `DEEPGRAM_API_KEY` unset makes the adapter throw
   * before any fetch, and the positional rule charged that non-event to the
   * budget and then stopped — returning a `no_speech` for 22 s of real speech
   * while groq and elevenlabs, both configured and both able to transcribe,
   * were never asked. (review r2)
   */
  private recoveryCallsSpent = 0;

  constructor(
    private readonly requestId: string,
    private readonly startTime: number,
    private readonly chain: readonly SttProviderId[],
    private readonly latency: ReturnType<typeof createTranscriptionLatencyRecorder>,
  ) {}

  get hasRefused(): boolean {
    return this.refused;
  }

  /** The one extra call after a refusal is spent; the banked `no_speech` answers. */
  get recoveryBudgetSpent(): boolean {
    return this.refused && this.recoveryCallsSpent > 0;
  }

  recordSuccess(sample: { provider: SttProviderId; model: string | undefined; index: number; attemptStart: number; result: TranscriptionResult }) {
    const { provider, model, index, result } = sample;
    const attemptMs = performance.now() - sample.attemptStart;
    // Prefer the model the adapter reports it ACTUALLY ran (e.g. AssemblyAI's
    // universal-3-5-pro -> universal-2 for unsupported languages), so the
    // X-STT-Model header and deduction metadata match what was billed.
    const usedModel = result.model || model;
    this.answer = { result, servedBy: provider, usedModel };
    logEvent(this.requestId, this.startTime, 'transcribe.provider_attempt_done', {
      provider,
      model: model || 'default',
      attempt: index + 1,
      upstreamRequestId: result.requestId,
      transcriptChars: result.text.length,
      resultSource: result.source,
      attemptMs: Math.round(attemptMs),
    });
    this.latency.record({ provider, model: usedModel, index, latencyMs: attemptMs });
  }

  /**
   * Record a failed attempt. Returns the response that ends the request, or
   * undefined when the chain moves on.
   */
  recordFailure(outcome: AttemptOutcome): Response | undefined {
    // ONE latency row per attempt, recorded here before any branching, so no
    // path below can forget a sample or file a second one.
    //
    // The single condition is whether the attempt ever reached the wire.
    // Everything an adapter rejects on its own — a missing API key, a size
    // cap, a content-type it can't take — throws in microseconds without
    // calling anyone, and a row for it would publish that provider
    // "answering" in 1 ms and failing a call it never received. Every real
    // provider failure (timeout, 5xx, rate limit, an unusable 2xx, an
    // upstream 4xx) happens strictly after the request went out, so it is
    // still recorded — that direction is the bug this must not reintroduce.
    if (outcome.reachedWire) {
      this.latency.record({
        provider: outcome.provider,
        model: outcome.model,
        index: outcome.index,
        latencyMs: outcome.attemptMs,
        failureKind: latencyFailureKind(outcome),
      });
      // The same signal, read a second way: an attempt made AFTER a refusal
      // that reached the wire is the one extra upstream call the spec budgets,
      // spent. (issue #381, review r2)
      if (this.refused) this.recoveryCallsSpent += 1;
    }

    // A non-retryable outcome ENDS the request with an error, but never once a
    // refusal has banked a benign `no_speech`: the user asked a question that
    // already has a valid, free answer, and a sibling's 413, 415, missing API
    // key or 500 is our problem, not theirs. One guard, above every terminal
    // response, so a new terminal kind cannot quietly reintroduce the
    // regression. (issue #381)
    //
    // After a refusal it records and CONTINUES rather than ending the loop,
    // for two reasons. The budget is counted in wire calls above, so a sibling
    // that failed before the wire left it unspent and the next sibling is
    // still owed the request (a bare `break` answered #381's literal incident
    // — 22 s of real speech — with a `no_speech`, having called nobody but the
    // provider that refused). And a bare `break` also bypassed the failure log
    // entirely, so the final `no_speech` gave an operator no indication that a
    // sibling had been tried at all, let alone why it failed. (review r2)
    if (!outcome.retryable && !this.refused) {
      return terminalResponse(outcome, this.requestId, this.startTime);
    }

    this.outcomes.push(outcome);
    this.fallbackCount += 1;
    const { log, failure } = failureRecord(outcome, this.chain[outcome.index + 1]);
    logEvent(this.requestId, this.startTime, 'transcribe.provider_attempt_fail', log);
    this.attemptFailures.push(failure);

    if (outcome.kind === 'empty_transcript') {
      this.refused = true;
      // The floor. If nothing after this produces text, THIS is the response:
      // the same 200, the same 0 credits and the same `no_speech_detected`
      // the user would have got before the failover existed. Overwritten
      // wholesale by any later attempt that succeeds.
      this.answer = { result: outcome.error.noSpeechResult, servedBy: outcome.provider };
    }
    return undefined;
  }

  /** The response once every usable provider in the chain has failed. */
  chainFailureResponse(provider: SttProviderId): Response {
    let lastInputError: ProviderInputError | undefined;
    let lastAuthError: ProviderAuthError | undefined;
    let sawUnavailable = false;
    for (const outcome of this.outcomes) {
      if (outcome.kind === 'input_rejected') lastInputError = outcome.error;
      if (outcome.kind === 'auth_rejected') lastAuthError = outcome.error;
      if (outcome.kind === 'unavailable' || outcome.kind === 'empty_transcript') sawUnavailable = true;
    }
    return providerChainFailureResponse({
      requestId: this.requestId,
      startTime: this.startTime,
      provider,
      fallbackCount: this.fallbackCount,
      attemptFailures: this.attemptFailures,
      lastError: this.outcomes.at(-1)?.error,
      lastInputError,
      lastAuthError,
      sawUnavailable,
    });
  }
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
  // A provider rejected OUR key and no sibling could serve the request. The
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
    return transcriptionFailedResponse(requestId, lastAuthError);
  }

  // Every provider rejected the input and none was merely unavailable, so a
  // retry will not help. A rejection from a provider gets a fixed message: the
  // upstream's wording never reaches the client (#2131). One the server wrote
  // itself (`fromProvider` false: a WebSocket-only model, a missing explicit
  // language, audio over a cap) is our own hint and is sent. `message` is
  // content-free either way (#2163), so the log keeps it.
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

  // A self-only chain has no sibling that can absorb the failure, so a 429
  // would wrongly promise a fallback retry. The error is a network or parse
  // failure the user cannot act on: log it, never echo it (#2131).
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
