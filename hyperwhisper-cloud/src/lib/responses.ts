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

export function corsPreflightResponse(): Response {
  return new Response(null, { status: 204, headers: CORS_HEADERS });
}

// Common error responses
export function ipBlockedResponse(): Response {
  return errorResponse(403, 'Access denied', 'Your IP has been temporarily blocked due to abuse');
}

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

export function fileTooLargeResponse(actualBytes: number, maxBytes: number): Response {
  const actualMB = actualBytes / (1024 * 1024);
  const maxMB = maxBytes / (1024 * 1024);

  return errorResponse(413, 'File too large',
    `Audio file must be ${maxMB.toFixed(0)} MB or smaller. Your file is ${actualMB.toFixed(2)} MB.`,
    {
      max_size_mb: Math.round(maxMB),
      actual_size_mb: parseFloat(actualMB.toFixed(2)),
    }
  );
}

export function imageTooLargeResponse(actualBytes: number, maxBytes: number): Response {
  const actualMB = actualBytes / (1024 * 1024);
  const maxMB = maxBytes / (1024 * 1024);

  return errorResponse(413, 'Image too large',
    `Image must be ${maxMB.toFixed(0)} MB or smaller. Your image is ${actualMB.toFixed(2)} MB.`,
    {
      max_size_mb: Math.round(maxMB),
      actual_size_mb: parseFloat(actualMB.toFixed(2)),
    }
  );
}
