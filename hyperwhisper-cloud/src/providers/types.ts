export type TranscriptionSource =
  | 'deepgram'
  | 'elevenlabs'
  | 'groq'
  | 'grok'
  | 'azure-mai'
  | 'google-chirp'
  | 'openai'
  | 'gemini'
  | 'gemini-transcribe'
  | 'assemblyai'
  | 'mistral'
  | 'soniox'
  | 'no_speech';

export interface TranscriptionResult {
  text: string;
  language?: string;
  durationSeconds: number;
  costUsd: number;
  source: TranscriptionSource;
  requestId?: string;
  /**
   * The upstream model that ACTUALLY ran, when it can differ from the requested
   * model. AssemblyAI's `speech_models` priority list silently falls back
   * universal-3-5-pro → universal-2 for unsupported languages, and `costUsd` is
   * billed at the model that ran — so the adapter reports it here and the route
   * propagates it to `X-STT-Model` / deduction metadata instead of mislabeling
   * the transcript as the requested model. Undefined → use the requested model.
   */
  model?: string;
}

export interface ProviderRequestContext {
  requestId?: string;
  attempt?: number;
  /**
   * Upstream model id the caller selected (e.g. 'gpt-4o-transcribe',
   * 'universal-3-5-pro', 'nova-3-medical'). Resolved + validated against the
   * server-side registry in `lib/stt-models.ts` before the provider fn runs,
   * so adapters can trust it. Empty/undefined means "provider default" —
   * single-model providers (grok) ignore it.
   */
  model?: string;
  /**
   * A capability the transcribe route GRANTS for this one attempt, never
   * something an adapter works out for itself: "if you get a 200 with no
   * transcript for audio the upstream says it processed, you may refuse it —
   * this request can still end as the benign 200 `no_speech` it would have been
   * before the failover existed."
   *
   * The route sets it only when all three of these hold:
   *
   *   1. This attempt is the provider the CALLER CHOSE. A sibling already
   *      covering for a failed primary does not get to refuse in turn and push
   *      the request onto a third provider.
   *   2. This request's chain — after geo filtering, which is applied to every
   *      request and not only to a blocked chosen provider — still has a provider
   *      after this one that this region can actually reach.
   *   3. No refusal has been spent yet on this request.
   *
   * The bound on the recovery is enforced separately and in the currency the spec
   * priced: ONE extra attempt that reaches the wire. A sibling that throws before
   * any fetch (no API key, a size cap, a content-type gate) costs nothing and does
   * not consume it.
   *
   * Adapters must NOT re-derive this from `attempt`. `attempt === 1` means
   * "first entry of this request's chain", which is not the same claim: a
   * geo-degraded chain can filter down to a single provider, and its one and
   * only attempt is still attempt 1. The chain is authored in exactly one place
   * (`lib/stt-models.ts`) and read in exactly one place (the route) — see
   * `hyperwhisper-cloud/CLAUDE.md`, "Never re-derive either in a caller".
   * (issue ray-amjad/hyperwhisper-app#381)
   */
  mayRefuseEmptyTranscript?: boolean;
  /**
   * Optional transcription domain add-on. Currently only 'medical', which
   * AssemblyAI layers on a base model via `domain: "medical-v1"` (a metered
   * add-on, not a separate model). Providers that don't support it ignore it.
   */
  domain?: string;
}

/**
 * Why a provider attempt was deemed unavailable. Lets the route and dashboards
 * distinguish "the upstream was slow and we gave up" (`timeout` — the request
 * might have succeeded with more budget) from "the upstream actually failed"
 * (`upstream_5xx` / `rate_limit`) and from "we got a 2xx we couldn't use"
 * (`bad_response` — e.g. ElevenLabs' geo-block HTML or an empty gzip body),
 * WITHOUT having to correlate a separate provider-level log line by requestId.
 */
export type ProviderUnavailableKind =
  | 'timeout'        // our per-request budget elapsed; upstream may have succeeded given more time
  | 'network_error'  // connection failed/reset before any response
  | 'rate_limit'     // upstream 429
  | 'upstream_5xx'   // upstream 5xx server error
  | 'bad_response'   // 2xx with an unusable body (geo-block HTML, empty, non-JSON)
  | 'unknown';

/**
 * Thrown when a provider is temporarily unavailable (429, 403 edge block, etc.)
 * Signals the fallback chain to try the next provider. `kind` carries the root
 * cause and `status`/`elapsedMs` the upstream HTTP status and attempt latency
 * when known, so the route can log the reason inline instead of dropping it.
 */
export class ProviderUnavailableError extends Error {
  readonly kind: ProviderUnavailableKind;
  readonly status?: number;
  readonly elapsedMs?: number;

  constructor(
    provider: string,
    reason: string,
    opts: { kind?: ProviderUnavailableKind; status?: number; elapsedMs?: number } = {},
  ) {
    super(`${provider} unavailable: ${reason}`);
    this.name = 'ProviderUnavailableError';
    this.kind = opts.kind ?? 'unknown';
    this.status = opts.status;
    this.elapsedMs = opts.elapsedMs;
  }
}

/**
 * A covered adapter got a 200 with no transcript, while the upstream's own
 * response said it had processed audio. Worth one sibling call — but it is NOT
 * a provider fault, and that distinction is the whole point of the class.
 *
 * Before the failover existed the same request was a benign 200 `no_speech`:
 * 0 credits, `no_speech_detected: true`. It must still be able to end that way,
 * so the refusal carries the exact result the adapter WOULD have returned. The
 * route holds it as the request's floor: if no sibling produces text — because
 * one is rate-limited, or has no API key configured, or rejects the audio — the
 * response is this result, not a 429/500/502 the user never used to get.
 *
 * It extends ProviderUnavailableError so every existing route behaviour is
 * untouched by construction: the chain continues, `attemptFailures` records it,
 * and the /latency row stays `ok: false` / `bad_response` (the spec's decided
 * boundary). Only the route may authorise it, via
 * `ProviderRequestContext.mayRefuseEmptyTranscript`.
 * (issue ray-amjad/hyperwhisper-app#381)
 */
export class EmptyTranscriptError extends ProviderUnavailableError {
  /** The `no_speech` result the adapter would have returned — the request's floor. */
  readonly noSpeechResult: TranscriptionResult;
  /** The upstream's own reported audio length, for the log and the operator. */
  readonly upstreamDurationSeconds: number;

  constructor(
    provider: string,
    opts: {
      upstreamDurationSeconds: number;
      elapsedMs: number;
      noSpeechResult: TranscriptionResult;
    },
  ) {
    super(provider, `empty transcript for ${opts.upstreamDurationSeconds}s of audio`, {
      kind: 'bad_response',
      elapsedMs: opts.elapsedMs,
    });
    this.name = 'EmptyTranscriptError';
    this.noSpeechResult = opts.noSpeechResult;
    this.upstreamDurationSeconds = opts.upstreamDurationSeconds;
  }
}

/**
 * Thrown when an upstream provider rejects the request input with a non-auth
 * 4xx (e.g. ElevenLabs 400 on a language code it doesn't accept, or a format
 * it can't decode). A sibling provider may well accept the same input, so the
 * transcribe route treats this like ProviderUnavailableError and continues
 * the fallback chain rather than failing the whole request. Distinct from
 * AudioTooLargeError / UnsupportedAudioFormatError, which are deterministic
 * across providers and map to a fixed client error. `status` is the upstream
 * HTTP status that triggered it.
 */
export class ProviderInputError extends Error {
  readonly status: number;

  constructor(provider: string, status: number, reason: string) {
    super(`${provider} rejected input (${status}): ${reason}`);
    this.name = 'ProviderInputError';
    this.status = status;
  }
}

/**
 * Thrown when the audio payload exceeds an upstream provider's inline-content
 * cap and a long-file path (e.g. GCS upload) isn't available in v1. The
 * transcribe route turns this into a 413 to the client instead of retrying
 * through the fallback chain.
 */
export class AudioTooLargeError extends Error {
  readonly actualBytes: number;
  readonly maxBytes: number;

  constructor(provider: string, actualBytes: number, maxBytes: number) {
    super(`${provider} audio too large: ${actualBytes} bytes (max ${maxBytes})`);
    this.name = 'AudioTooLargeError';
    this.actualBytes = actualBytes;
    this.maxBytes = maxBytes;
  }
}

/**
 * Thrown when the upstream provider does not accept the supplied audio
 * format and we can't transparently convert it. The transcribe route maps
 * this to HTTP 415 so the client can re-encode and retry, rather than
 * falling through the fallback chain.
 */
export class UnsupportedAudioFormatError extends Error {
  readonly contentType: string;
  readonly acceptedFormats: readonly string[];

  constructor(provider: string, contentType: string, acceptedFormats: readonly string[]) {
    super(`${provider} does not accept ${contentType}; accepts ${acceptedFormats.join(', ')}`);
    this.name = 'UnsupportedAudioFormatError';
    this.contentType = contentType;
    this.acceptedFormats = acceptedFormats;
  }
}
