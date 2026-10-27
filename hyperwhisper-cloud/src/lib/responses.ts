// RESPONSE HELPERS
// Standardized JSON responses with CORS headers

import { CREDITS_PER_MINUTE } from './constants';

export const CORS_HEADERS: Record<string, string> = {
  'Access-Control-Allow-Origin': '*',
  'Access-Control-Allow-Methods': 'GET, POST, OPTIONS',
  'Access-Control-Allow-Headers': 'Content-Type',
  'Access-Control-Max-Age': '86400',
};

export function errorResponse(
  status: number,
  error: string,
  message: string,
  extra?: Record<string, unknown>
): Response {
  return new Response(
    JSON.stringify({ error, message, ...extra }),
    {
      status,
      headers: { ...CORS_HEADERS, 'content-type': 'application/json' },
    }
  );
}

export function jsonResponse<T>(data: T, headers?: Record<string, string>): Response {
  return new Response(JSON.stringify(data), {
    status: 200,
    headers: { ...CORS_HEADERS, 'content-type': 'application/json', ...headers },
  });
}

// Common error responses
export function licenseRequiredResponse(): Response {
  return errorResponse(401, 'License required', 'HyperWhisper Cloud requires a license key. Add credits to get a key, or use your own API key for free.');
}

export function invalidLicenseResponse(): Response {
  return errorResponse(401, 'Invalid license', 'The provided license key is invalid or expired');
}

export function insufficientCreditsResponse(balance: number, estimated: number): Response {
  const minutesRemaining = Math.floor(balance / CREDITS_PER_MINUTE);
  const minutesRequired = Math.ceil(estimated / CREDITS_PER_MINUTE);

  return errorResponse(402, 'Insufficient credits',
    `You have ${balance.toFixed(1)} credits remaining. This transcription requires approximately ${estimated.toFixed(1)} credits.`,
    {
      credits_remaining: balance,
      minutes_remaining: minutesRemaining,
      minutes_required: minutesRequired,
      credits_per_minute: CREDITS_PER_MINUTE,
    }
  );
}

export function invalidContentTypeResponse(expected: string, received: string): Response {
  return errorResponse(400, 'Invalid Content-Type', `Content-Type must be ${expected}`, { received });
}

export function missingContentLengthResponse(): Response {
  return errorResponse(400, 'Missing Content-Length', 'Content-Length header is required for streaming transcription');
}

const MIB = 1024 * 1024;

/**
 * The size words of a 413 message, in the unit the cap is written in (#2607).
 * Most caps are whole MiB (`128 * 1024 * 1024`), but Google Chirp's inline cap
 * is 9_500_000 bytes, the "9.5 MB" every client and doc names. Dividing that by
 * MiB and rounding said "9 MB", and called a 9.6 MB file 9.16 MB. A cap that is
 * not a whole number of MiB is spoken in decimal MB, and the file size in the
 * same unit, so the two numbers in one sentence compare.
 *
 * Only the human message uses this. The numeric `max_size_mb` /
 * `actual_size_mb` fields stay in MiB with a whole-number limit: macOS and
 * Windows parse `max_size_mb` as an integer and multiply both by 1_048_576.
 */
export function sizeLimitWords(actualBytes: number, maxBytes: number): { max: string; actual: string } {
  const unit = maxBytes % MIB === 0 ? MIB : 1_000_000;
  return {
    max: `${parseFloat((maxBytes / unit).toFixed(2))} MB`,
    actual: `${(actualBytes / unit).toFixed(2)} MB`,
  };
}

/** The wire fields the native clients read; see `sizeLimitWords`. */
export function sizeLimitFields(actualBytes: number, maxBytes: number): { max_size_mb: number; actual_size_mb: number } {
  return {
    max_size_mb: Math.round(maxBytes / MIB),
    actual_size_mb: parseFloat((actualBytes / MIB).toFixed(2)),
  };
}

export function fileTooLargeResponse(actualBytes: number, maxBytes: number): Response {
  const words = sizeLimitWords(actualBytes, maxBytes);

  return errorResponse(413, 'File too large',
    `Audio file must be ${words.max} or smaller. Your file is ${words.actual}.`,
    sizeLimitFields(actualBytes, maxBytes),
  );
}
