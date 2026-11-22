import { AsyncLocalStorage } from 'node:async_hooks';
import { EmptyTranscriptError } from './types';
import type { ProviderRequestContext, TranscriptionResult } from './types';
import { logProviderEvent } from './provider-log';
import { upstreamDurationOrNull } from './provider-audio';

/**
 * Whether one provider attempt ever opened a socket to its upstream.
 *
 * The /latency page may only publish an attempt the provider actually received.
 * Every adapter runs its own gates first — a missing API key, a byte-length cap,
 * a content-type check — and each of those throws in single-digit microseconds,
 * which the page would otherwise render as that provider answering in 1 ms.
 * Enumerating those throw sites is what failed twice (a whitelist of error types
 * has to be extended by whoever adds the next gate, and nothing makes them).
 *
 * So the signal is taken from the one place that cannot be bypassed without
 * noticing: the request actually leaving this process. `markProviderNetworkCall()`
 * is called by fetchWithTimeout() in provider-http.ts — every adapter's only route to an
 * upstream — and by the GCS uploader on Chirp's large-file path, BEFORE the
 * fetch, so a timeout or a connection reset still counts as a measurement. A new
 * gate added above any of those calls is excluded automatically; a new gate
 * added below one is, correctly, a real attempt.
 *
 * AsyncLocalStorage rather than a module-level flag: attempts from concurrent
 * requests interleave on one event loop, and a shared flag would let one
 * request's fetch mark another request's rejection as measured.
 *
 * One deliberate omission: lib/google-auth mints its Google token through
 * google-auth-library's own HTTP client, so a Chirp attempt that dies there is
 * not reported. That is the right answer — a bad or missing service-account
 * credential is our configuration failing, not Google answering slowly.
 */
export type ProviderAttemptNetwork = { reachedProvider: boolean };

const attemptNetworkStorage = new AsyncLocalStorage<ProviderAttemptNetwork>();

/** Runs one provider attempt, recording into `state` whether it reached the wire. */
export function runProviderAttempt<T>(
  state: ProviderAttemptNetwork,
  fn: () => Promise<T>,
): Promise<T> {
  return attemptNetworkStorage.run(state, fn);
}

/** Called immediately before an upstream request leaves this process. */
export function markProviderNetworkCall(): void {
  const state = attemptNetworkStorage.getStore();
  if (state) {
    state.reachedProvider = true;
  }
}

/**
 * The empty-transcript outcome, for the adapters the failover covers (grok,
 * deepgram, groq, and elevenlabs since #720): log the `no_speech`, then either
 * refuse it — when the
 * ROUTE granted this attempt the refusal — or return it.
 *
 * ```ts
 * if (!transcript.trim()) {
 *   return emptyTranscriptOutcome(provider, {
 *     label: 'Deepgram', startedAt, context,
 *     upstreamDuration: data.metadata?.duration,
 *     upstreamRequestId: data.metadata?.request_id,
 *     logDetails: { detectedLanguage: channel?.detected_language },
 *     noSpeechResult,
 *   });
 * }
 * ```
 *
 * One block, not three. `deepgram.ts`, `groq.ts` and `xai-stt.ts` carried
 * 41/36/41-line copies differing in three tokens, and they had ALREADY drifted:
 * two carried `upstreamRequestId` on the `no_speech` event and groq carried
 * neither the field nor the comment claiming it, so #381's own telemetry was
 * dead for one of the three providers it was written for. Same seam, same remedy
 * as `providerHttpError` in provider-http.ts.
 *
 * It returns on one path and throws on the other because the route's grant is
 * the only thing that decides which — see
 * `ProviderRequestContext.mayRefuseEmptyTranscript`. An adapter never re-derives
 * it. (issue ray-amjad/hyperwhisper-app#381)
 */
export function emptyTranscriptOutcome(
  provider: string,
  opts: {
    /** Name used in the thrown error message ('Grok', 'Deepgram', 'Groq'). */
    label: string;
    /** The adapter's `performance.now()` mark for this attempt. */
    startedAt: number;
    context: ProviderRequestContext;
    /** The upstream's own reported audio length, unvalidated. */
    upstreamDuration: unknown;
    /**
     * The upstream's own id for the call that returned nothing — what an operator
     * hands vendor support. On the refusal path there is no result for the route
     * to read it off, so it has to be on this event.
     */
    upstreamRequestId?: string;
    /** Extra fields merged into the `no_speech` log line (language, …). */
    logDetails?: Record<string, unknown>;
    /** The `no_speech` result the adapter would have returned. */
    noSpeechResult: TranscriptionResult;
    /**
     * Opt-in, per adapter: when the upstream reports no usable duration, gate the
     * refusal on the request's own measured length
     * (`context.requestAudioSeconds`) instead. Only for an upstream that
     * STRUCTURALLY reports no duration on an empty transcript yet has parsed and
     * processed the audio (ElevenLabs: duration is the last word's end time).
     * Never set it for an adapter whose missing duration can mean "did not
     * process the audio" — that gate is what stops a second paid upload.
     * (issue ray-amjad/hyperwhisper-app#720)
     */
    requestDurationFallback?: boolean;
  },
): TranscriptionResult {
  // `upstreamDurationSeconds` keeps its meaning on the log line: the upstream's
  // own number or null, never the request's estimate.
  const upstreamDurationSeconds = upstreamDurationOrNull(opts.upstreamDuration);
  const requestAudioSeconds = opts.requestDurationFallback === true && upstreamDurationSeconds === null
    ? upstreamDurationOrNull(opts.context.requestAudioSeconds)
    : null;
  const gateDurationSeconds = upstreamDurationSeconds ?? requestAudioSeconds;
  const refusing = gateDurationSeconds !== null && opts.context.mayRefuseEmptyTranscript === true;

  // Logged on BOTH paths, refusal included. This event is the only per-provider
  // count of "the upstream returned nothing", and suppressing it on the refusal
  // path would zero it for exactly the providers and exactly the case the
  // feature exists for. `refused` separates the two outcomes in one query.
  logProviderEvent(provider, 'no_speech', {
    elapsedMs: Math.round(performance.now() - opts.startedAt),
    ...opts.logDetails,
    upstreamDurationSeconds,
    upstreamRequestId: opts.upstreamRequestId,
    // Only on an opted-in adapter, and only when the upstream said nothing:
    // which number the gate actually read, so a refusal reads honestly.
    ...(opts.requestDurationFallback === true
      ? { requestAudioSeconds, durationSource: upstreamDurationSeconds !== null ? 'upstream' : requestAudioSeconds !== null ? 'request' : 'none' }
      : {}),
    refused: refusing,
  }, opts.context);

  if (refusing) {
    throw new EmptyTranscriptError(opts.label, {
      upstreamDurationSeconds,
      requestAudioSeconds,
      elapsedMs: Math.round(performance.now() - opts.startedAt),
      // The route keeps this as the request's floor, so a sibling that never
      // answers cannot turn a benign no_speech into an error.
      noSpeechResult: opts.noSpeechResult,
    });
  }
  return opts.noSpeechResult;
}
