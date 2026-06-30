export type TranscriptionSource =
  | 'deepgram'
  | 'elevenlabs'
  | 'groq'
  | 'grok'
  | 'azure-mai'
  | 'google-chirp'
  | 'openai'
  | 'gemini'
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
   * universal-3-pro → universal-2 for unsupported languages, and `costUsd` is
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
   * 'universal-3-pro', 'nova-3-medical'). Resolved + validated against the
   * server-side registry in `lib/stt-models.ts` before the provider fn runs,
   * so adapters can trust it. Empty/undefined means "provider default" —
   * single-model providers (grok) ignore it.
   */
  model?: string;
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
