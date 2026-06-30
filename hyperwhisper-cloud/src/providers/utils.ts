import { ProviderUnavailableError } from './types';
import type { ProviderRequestContext } from './types';
import { BYTES_PER_MINUTE_ESTIMATE } from '../lib/constants';

const DEFAULT_PROVIDER_TIMEOUT_MS = 15_000;
const ERROR_BODY_PREVIEW_LIMIT = 500;

// Audio-upload timeout budget, scaled with payload size. The default 15s is fine
// for the small create/poll/transcript calls, but an upload that re-sends the
// whole recording (AssemblyAI /v2/upload, Soniox /v1/files, Azure MAI multipart)
// can far exceed 15s for the large files these async/large-cap providers accept —
// aborting mid-upload would surface as a spurious 502. Floor 30s + 1s per 100 KB.
// Mirrors gcs-storage's GCS upload budget.
const UPLOAD_TIMEOUT_FLOOR_MS = 30_000;
const UPLOAD_TIMEOUT_PER_100KB_MS = 1_000;
export function computeUploadTimeoutMs(byteLength: number): number {
  return Math.max(UPLOAD_TIMEOUT_FLOOR_MS, Math.ceil(byteLength / 100_000) * UPLOAD_TIMEOUT_PER_100KB_MS);
}

/**
 * Conservative audio-duration estimate (seconds) from the encoded byte length,
 * for fail-closed billing when an upstream returns a successful transcript but
 * omits the duration/usage we'd normally bill on. Mirrors the route's preflight
 * size→seconds heuristic (64 kbps encoded).
 */
export function estimateSecondsFromBytes(byteLength: number): number {
  return (byteLength / BYTES_PER_MINUTE_ESTIMATE) * 60;
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

export function logProviderEvent(
  provider: string,
  event: string,
  details: Record<string, unknown>,
  context: ProviderRequestContext = {},
) {
  console.log(`provider.${event}`, {
    provider,
    requestId: context.requestId,
    attempt: context.attempt,
    ...details,
  });
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

export async function readErrorBodyPreview(response: Response): Promise<string> {
  try {
    const body = await response.text();
    if (body.length <= ERROR_BODY_PREVIEW_LIMIT) {
      return body;
    }

    return `${body.slice(0, ERROR_BODY_PREVIEW_LIMIT)}...`;
  } catch {
    return '<unreadable>';
  }
}
