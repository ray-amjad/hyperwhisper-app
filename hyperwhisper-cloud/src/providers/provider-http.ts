import { ProviderAuthError, ProviderInputError, ProviderUnavailableError } from './types';
import type { ProviderUnavailableKind } from '../lib/errors';
import type { ProviderRequestContext } from './types';
import { readUpstreamError, upstreamErrorLogFields, upstreamErrorReason, type UpstreamErrorRead } from './upstream-error';
import { markProviderNetworkCall } from './provider-attempt';
import { logProviderEvent } from './provider-log';

const DEFAULT_PROVIDER_TIMEOUT_MS = 15_000;

// Audio-upload timeout budget, scaled with payload size. The default 15s is fine
// for the small create/poll/transcript calls, but an upload that re-sends the
// whole recording (AssemblyAI /v2/upload, Soniox /v1/files, Azure MAI multipart)
// can far exceed 15s for the large files these async/large-cap providers accept —
// aborting mid-upload would surface as a spurious 502. Floor 30s + 1s per 100 KB.
// gcs-storage's GCS upload (Chirp's large-file path) uses this same budget.
const UPLOAD_TIMEOUT_FLOOR_MS = 30_000;
const UPLOAD_TIMEOUT_PER_100KB_MS = 1_000;
export function computeUploadTimeoutMs(byteLength: number): number {
  return Math.max(UPLOAD_TIMEOUT_FLOOR_MS, Math.ceil(byteLength / 100_000) * UPLOAD_TIMEOUT_PER_100KB_MS);
}

function resolveProviderTimeoutMs(): number {
  const configured = Number.parseInt(process.env.STT_PROVIDER_TIMEOUT_MS || '', 10);
  if (Number.isFinite(configured) && configured > 0) {
    return configured;
  }

  return DEFAULT_PROVIDER_TIMEOUT_MS;
}

function serializeError(error: unknown): string {
  if (error instanceof Error) {
    return error.message;
  }

  return String(error);
}

function isAbortError(error: unknown): boolean {
  return error instanceof DOMException && error.name === 'AbortError';
}

/**
 * Shape facts for a 200 body that failed to parse. A speech vendor's 200 body
 * IS the transcript, so never log any part of it: only whether it looks like
 * an HTML page (geo-block/proxy), a cut-off JSON stream, or something else.
 */
export function unparsedBodyKind(raw: string): 'html' | 'json_truncated' | 'other' {
  const first = raw.trimStart()[0];
  if (first === '<') return 'html';
  if (first === '{' || first === '[') return 'json_truncated';
  return 'other';
}

/**
 * Read and parse one 200 poll body. On failure, log `parse_error` and return
 * undefined so the caller can retry. It never logs body content (#1069): a
 * poll body can hold the user's transcript. A read failure (connection reset,
 * truncated chunked transfer) logs only the error's name; a parse failure
 * logs only the body's length and `unparsedBodyKind`.
 */
export async function readPollJson<T>(
  provider: string,
  resp: Response,
  polls: number,
  context: ProviderRequestContext = {},
): Promise<T | undefined> {
  // `raw` stays undefined when the body fails to read, so the catch can tell
  // a read failure from a parse failure.
  let raw: string | undefined;
  try {
    raw = await resp.text();
    return JSON.parse(raw) as T;
  } catch (err) {
    logProviderEvent(provider, 'parse_error', {
      phase: 'poll',
      failure: raw === undefined ? 'read' : 'parse',
      polls,
      contentType: resp.headers.get('content-type') ?? 'unknown',
      contentEncoding: resp.headers.get('content-encoding') ?? 'none',
      ...(raw === undefined
        ? { errorName: err instanceof Error ? err.name : typeof err }
        : { bodyLength: raw.length, bodyKind: unparsedBodyKind(raw) }),
    }, context);
    return undefined;
  }
}

/**
 * Read a 200 body that must be a JSON object, or throw the retryable
 * `ProviderUnavailableError` (`kind: 'bad_response'`) so the route fails over
 * instead of answering a 500. A speech vendor's 200 body IS the transcript, so
 * like `readPollJson` this never logs body content or a read error's message
 * (#1069): a read failure logs only the error's name; an empty, non-JSON or
 * non-object body logs only its length and shape. (#2064, #2151)
 *
 * `null`, a bare number or string, and an array all parse as JSON and then
 * fail on the first field read, so they are refused here too.
 */
export async function readJson200<T extends object>(
  provider: string,
  label: string,
  response: Response,
  startTime: number,
  context: ProviderRequestContext = {},
): Promise<Partial<T>> {
  const elapsedMs = () => Math.round(performance.now() - startTime);
  const shape = {
    contentType: response.headers.get('content-type') ?? 'unknown',
    contentEncoding: response.headers.get('content-encoding') ?? 'none',
  };
  const badResponse = (event: string, details: Record<string, unknown>) => {
    logProviderEvent(provider, event, { elapsedMs: elapsedMs(), ...shape, ...details }, context);
    return new ProviderUnavailableError(label, 'malformed 200 response body', {
      kind: 'bad_response',
      status: response.status,
      elapsedMs: elapsedMs(),
    });
  };

  let raw: string;
  try {
    raw = await response.text();
  } catch (readError) {
    // A reset connection or a cut chunked transfer. Only the name: the
    // message can quote what was read so far.
    throw badResponse('body_read_error', {
      errorName: readError instanceof Error ? readError.name : typeof readError,
    });
  }
  if (!raw) {
    throw badResponse('empty_body', {});
  }
  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    throw badResponse('parse_error', { bodyLength: raw.length, bodyKind: unparsedBodyKind(raw) });
  }
  if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) {
    throw badResponse('parse_error', {
      bodyLength: raw.length,
      bodyKind: Array.isArray(parsed) ? 'json_array' : parsed === null ? 'json_null' : `json_${typeof parsed}`,
    });
  }
  return parsed as Partial<T>;
}

export async function fetchWithTimeout(
  provider: string,
  url: string,
  init: RequestInit,
  context: ProviderRequestContext = {},
  timeoutMsOverride?: number,
): Promise<Response> {
  const timeoutMs = typeof timeoutMsOverride === 'number' && timeoutMsOverride > 0
    ? timeoutMsOverride
    : resolveProviderTimeoutMs();
  const startedAt = performance.now();
  const controller = new AbortController();
  const timeoutHandle = setTimeout(() => controller.abort(), timeoutMs);

  // Before the fetch, not after: a timeout or a connection reset is a provider
  // attempt the user waited on, so it has to count as a measurement. See
  // ProviderAttemptNetwork in provider-attempt.ts.
  markProviderNetworkCall();
  logProviderEvent(provider, 'request_start', { timeoutMs }, context);

  try {
    const response = await fetch(url, {
      ...init,
      signal: controller.signal,
    });

    logProviderEvent(provider, 'http_response', {
      elapsedMs: Math.round(performance.now() - startedAt),
      status: response.status,
      ok: response.ok,
    }, context);

    return response;
  } catch (error) {
    const elapsedMs = Math.round(performance.now() - startedAt);

    if (isAbortError(error)) {
      logProviderEvent(provider, 'transport_error', {
        elapsedMs,
        kind: 'timeout',
        timeoutMs,
      }, context);
      throw new ProviderUnavailableError(provider, `timeout after ${timeoutMs}ms`, {
        kind: 'timeout',
        elapsedMs,
      });
    }

    logProviderEvent(provider, 'transport_error', {
      elapsedMs,
      kind: 'network_error',
      message: serializeError(error),
    }, context);
    throw new ProviderUnavailableError(provider, `network error: ${serializeError(error)}`, {
      kind: 'network_error',
      elapsedMs,
    });
  } finally {
    clearTimeout(timeoutHandle);
  }
}

export function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

/**
 * Per-provider knobs for {@link providerHttpError}. Everything NOT expressed
 * here is identical across the synchronous STT adapters and lives in the
 * helper: the `http_error` log line, the `kind` classification, the 429 /
 * 5xx failover errors, and the "other 4xx → ProviderInputError so a sibling
 * provider can try the same input" fallthrough.
 */
export type ProviderHttpErrorPolicy = ProviderHttpErrorPolicyBase & (
  | {
    /**
     * Statuses that mean OUR credentials are wrong. These throw a
     * `ProviderAuthError`: a sibling has its own key, so the route tries it when
     * the chain has one, and fails the request when it does not.
     * Defaults to `[401]`; adapters whose upstream also returns 403 for a bad or
     * unauthorized key pass `[401, 403]`.
     */
    authStatuses?: readonly number[];
    /** Message for the `authStatuses` failure. */
    authMessage: string;
  }
  | {
    /**
     * `[]`: no status raises a `ProviderAuthError`. For the adapters whose
     * bad-key statuses end the request instead, through
     * {@link ProviderHttpErrorPolicyBase.terminalStatuses}.
     */
    authStatuses: readonly [];
    authMessage?: undefined;
  }
);

interface ProviderHttpErrorPolicyBase {
  /** Name used in the thrown error messages ('OpenAI', 'Grok', 'Deepgram', …). */
  label: string;
  /**
   * Statuses that END the request with a plain `Error(message)`: no failover,
   * no sibling, and the client gets the generic 500 text. Checked FIRST, before
   * the out-of-credits test and every other branch, so a status listed here
   * always gives exactly this error.
   */
  terminalStatuses?: Readonly<Partial<Record<number, string>>>;
  /**
   * The error for a 4xx no other branch claimed. Unset (default): a
   * `ProviderInputError`, so the chain tries a sibling with the same input.
   */
  otherStatusError?: (status: number) => Error;
  /**
   * When true, 402 fails over as `insufficient funds` — billing exhaustion on
   * THIS provider only, siblings may still have budget. When false (default)
   * a 402 falls through to the generic `ProviderInputError` branch, which is
   * what the adapters whose upstream never returns 402 already did.
   */
  failoverOn402?: boolean;
  /** Extra fields merged into the `http_error` log line (e.g. `{ model }`). */
  logDetails?: Record<string, unknown>;
  /**
   * Attach `{ kind, status, elapsedMs }` to the thrown
   * `ProviderUnavailableError`, so the route can log the root cause inline
   * instead of correlating a separate provider log line by requestId.
   */
  attachUnavailableDetails?: boolean;
}

/**
 * Error bodies that mean OUR account with the upstream is out of credits or
 * over its plan quota. Matched on the body text, not parsed, because the
 * preview may be truncated. Each marker is specific enough that no
 * bad-key or bad-input body carries it.
 */
const QUOTA_EXHAUSTED_MARKERS: ReadonlyArray<{ pattern: RegExp; reason: string }> = [
  // ElevenLabs sends 401 (not 402) when the plan runs out of credits:
  // `{"detail":{"code":"quota_exceeded","status":"quota_exceeded",…}}`.
  // 641 requests 500'd on it, 2026-10-02 to 10-05. (issue #1228)
  { pattern: /\bquota_exceeded\b/, reason: 'quota_exceeded' },
  // OpenAI: 429 with `"code": "insufficient_quota"` once the account's credit is spent.
  { pattern: /\binsufficient_quota\b/, reason: 'insufficient_quota' },
  // xAI: 403 "Your team … has either used all available credits or reached its
  // monthly spending limit" / "… doesn't have any credits yet".
  { pattern: /used all available credits|(?:doesn't|does not) have any credits|monthly spending limit/i, reason: 'credits_exhausted' },
];

/** Statuses an upstream has been seen to carry an out-of-credits body on. */
const QUOTA_EXHAUSTED_STATUSES: ReadonlySet<number> = new Set([401, 402, 403, 429]);

/**
 * The greppable reason when `body` says the upstream account is out of
 * credits, or `null` when it does not.
 */
export function quotaExhaustedReason(status: number, body: string): string | null {
  if (!QUOTA_EXHAUSTED_STATUSES.has(status)) return null;
  return QUOTA_EXHAUSTED_MARKERS.find(({ pattern }) => pattern.test(body))?.reason ?? null;
}

/**
 * Read, log and classify a non-2xx response from a synchronous STT upstream,
 * returning the error the caller should throw:
 *
 * ```ts
 * if (!response.ok) {
 *   throw await providerHttpError(provider, response, startedAt, context, { … });
 * }
 * ```
 *
 * Every adapter had grown its own copy of this block — same body preview, same
 * `kind` ternary, same `http_error` log, same 401 / 429 / 5xx / other-4xx
 * ladder — differing only in the knobs on {@link ProviderHttpErrorPolicy}. It
 * returns the error rather than throwing so the `throw` stays visible at the
 * call site and TypeScript keeps narrowing the code after it.
 *
 * `startedAt` is the adapter's `performance.now()` mark; `elapsedMs` is measured
 * AFTER the body preview is read, matching what the inline blocks logged.
 */
export async function providerHttpError(
  provider: string,
  response: Response,
  startedAt: number,
  context: ProviderRequestContext,
  policy: ProviderHttpErrorPolicy,
): Promise<Error> {
  return classifyProviderHttpError(provider, await readUpstreamError(response), startedAt, context, policy);
}

/**
 * {@link providerHttpError} for a caller that has already read the error body
 * (a Response body reads once), e.g. Chirp's adaptation-retry test.
 */
export function classifyProviderHttpError(
  provider: string,
  upstreamError: UpstreamErrorRead,
  startedAt: number,
  context: ProviderRequestContext,
  policy: ProviderHttpErrorPolicy,
): Error {
  const { label } = policy;
  // `classifierText` is for the quota test only. The body can quote the
  // user's input, so the log line carries content-free fields (#2163).
  const { fields, classifierText: errorText } = upstreamError;
  const { status } = fields;
  const elapsedMs = Math.round(performance.now() - startedAt);
  const kind = status >= 500 ? 'upstream_5xx' : status === 429 ? 'rate_limit' : 'http_error';
  const quotaReason = quotaExhaustedReason(status, errorText);

  logProviderEvent(provider, 'http_error', {
    ...policy.logDetails,
    elapsedMs,
    ...upstreamErrorLogFields(fields),
    kind,
    ...(quotaReason ? { quotaExhausted: quotaReason } : {}),
  }, context);

  // Only the failover branches below carry details, and each knows its own
  // kind — `http_error` is a log label, not a ProviderUnavailableKind.
  const details = (unavailableKind: ProviderUnavailableKind) =>
    policy.attachUnavailableDetails ? { kind: unavailableKind, status, elapsedMs } : undefined;

  const terminalMessage = policy.terminalStatuses?.[status];
  if (terminalMessage !== undefined) {
    return new Error(terminalMessage);
  }
  // Checked BEFORE the auth branch: an out-of-credits account is billing
  // exhaustion on THIS provider's account, like a 402 — a sibling with its own
  // account can still serve the request — even when the upstream sends it as a
  // 401 or 403. (issue #1228)
  if (quotaReason) {
    return new ProviderUnavailableError(label, `insufficient funds (${quotaReason})`, details(status === 429 ? 'rate_limit' : 'unknown'));
  }
  if (policy.authMessage !== undefined && (policy.authStatuses ?? [401]).includes(status)) {
    return new ProviderAuthError(policy.authMessage, status);
  }
  if (status === 429) {
    return new ProviderUnavailableError(label, 'rate limit exceeded', details('rate_limit'));
  }
  if (status === 402 && policy.failoverOn402) {
    return new ProviderUnavailableError(label, 'insufficient funds', details('unknown'));
  }
  if (status >= 500) {
    return new ProviderUnavailableError(label, `upstream 5xx: ${status}`, details('upstream_5xx'));
  }

  // Other 4xx (e.g. 400 on an unaccepted language code/format) — a sibling
  // provider may accept this input, so let the chain fall through. The
  // message (logged) names only the code; the body preview is kept in
  // clientMessage, which no route sends (#2131): the client's 400 is fixed.
  if (policy.otherStatusError) {
    return policy.otherStatusError(status);
  }
  return new ProviderInputError(label, status, upstreamErrorReason(fields), errorText || `HTTP ${status}`);
}

/**
 * Read a required string field from a successful upstream JSON response.
 *
 * Async job providers use the same two failure modes: unreadable JSON is a
 * malformed response, while valid JSON without the required field is a
 * provider-specific missing-field response. Keep those messages at the call
 * site while sharing the response parsing and field check.
 */
export async function readRequiredJsonString(
  response: Response,
  options: {
    provider: string;
    field: string;
    malformedMessage: string;
    missingMessage: string;
  },
): Promise<string> {
  let value: string;
  try {
    value = ((await response.json()) as Record<string, string | undefined>)[options.field] || '';
  } catch {
    throw new ProviderUnavailableError(options.provider, options.malformedMessage);
  }

  if (!value) {
    throw new ProviderUnavailableError(options.provider, options.missingMessage);
  }

  return value;
}
