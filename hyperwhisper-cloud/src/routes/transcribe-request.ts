import type { Context } from 'hono';
import { MAX_AUDIO_SIZE_BYTES } from '../lib/constants';
import { rawQuery } from '../lib/query';
import {
  errorResponse,
  fileTooLargeResponse,
  invalidContentTypeResponse,
  missingContentLengthResponse,
} from '../lib/responses';
import { isValidProviderId, type SttProviderId } from '../lib/stt-models';
// The providers layer's own answer to "how big a payload may this provider be
// handed", so the route never needs a provider's byte caps or the environment
// that lifts them. See providers/audio-limits.ts.
import { preBufferMaxBytes } from '../providers/audio-limits';

type ProviderSelection =
  | { ok: true; provider: SttProviderId }
  | { ok: false; provided: string };

export function extractProvider(c: Context): ProviderSelection {
  const header = c.req.header('X-STT-Provider')?.toLowerCase().trim();
  // No header → historical default (many clients send only a provider, some
  // none). An explicitly-supplied but unknown provider is REJECTED (fail-closed)
  // rather than silently billed against a default upstream.
  if (!header) {
    return { ok: true, provider: 'deepgram' };
  }
  if (isValidProviderId(header)) {
    return { ok: true, provider: header };
  }
  return { ok: false, provided: header };
}

export function extractModel(c: Context): string | undefined {
  return c.req.header('X-STT-Model')?.trim() || rawQuery(c.req.url, 'model')?.trim() || undefined;
}

export function extractDomain(c: Context): string | undefined {
  const domain = c.req.header('X-STT-Domain')?.toLowerCase().trim();
  return domain || undefined;
}

/**
 * Values that mean "the header is present but the client is NOT opting out".
 * Everything else counts as an opt-out, including values we never documented —
 * a client that bothers to send this header at all means to be excluded, so an
 * unrecognised value fails toward privacy rather than toward more data.
 */
const LATENCY_OPT_IN_VALUES = new Set(['', '0', 'false', 'no', 'off']);

/**
 * True when the caller asked to be left out of the public latency statistics
 * (`X-Latency-Opt-Out: 1`). The macOS and Windows apps send this when the user
 * turns off "Share anonymous speed data" in settings.
 *
 * Opting out costs the user nothing and changes nothing else about the
 * request — it only stops the anonymous timing row from being written. See
 * lib/latency-report.ts for what that row holds.
 */
export function isLatencyOptOut(c: Context): boolean {
  const header = c.req.header('X-Latency-Opt-Out');
  if (header === undefined) return false;
  return !LATENCY_OPT_IN_VALUES.has(header.toLowerCase().trim());
}

export function validateStreamingHeaders(c: Context, provider: SttProviderId):
  | { ok: true; contentType: string; contentLength: number }
  | { ok: false; response: Response } {
  const contentType = c.req.header('Content-Type') || '';
  if (!contentType.startsWith('audio/')) {
    return { ok: false, response: invalidContentTypeResponse('audio/*', contentType) };
  }

  const contentLengthHeader = c.req.header('Content-Length');
  if (!contentLengthHeader) {
    return { ok: false, response: missingContentLengthResponse() };
  }

  const contentLength = Number.parseInt(contentLengthHeader, 10);
  if (!Number.isFinite(contentLength) || contentLength <= 0) {
    return { ok: false, response: errorResponse(400, 'Invalid Content-Length', 'Content-Length must be a positive integer') };
  }

  if (contentLength > MAX_AUDIO_SIZE_BYTES) {
    return { ok: false, response: fileTooLargeResponse(contentLength, MAX_AUDIO_SIZE_BYTES) };
  }

  // Some providers reject a payload this large no matter what we do with it —
  // Chirp's inline `recognize` cap, Gemini's base64 inline cap, OpenAI's hard
  // 25 MB limit. Ask the providers layer for the cap and 413 on the way past,
  // so we never allocate a buffer of up to MAX_AUDIO_SIZE_BYTES only to throw
  // it away. `null` means this provider has no cap of its own.
  const providerMaxBytes = preBufferMaxBytes(provider);
  if (providerMaxBytes !== null && contentLength > providerMaxBytes) {
    return {
      ok: false,
      response: fileTooLargeResponse(contentLength, providerMaxBytes),
    };
  }

  return { ok: true, contentType, contentLength };
}
