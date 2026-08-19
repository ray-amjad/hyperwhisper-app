import { describe, expect, test } from 'bun:test';

import {
  CORS_HEADERS,
  errorResponse,
  fileTooLargeResponse,
  imageTooLargeResponse,
  insufficientCreditsResponse,
  invalidContentTypeResponse,
  invalidLicenseResponse,
  jsonResponse,
  licenseRequiredResponse,
  missingContentLengthResponse,
} from './responses';
import {
  CREDITS_PER_MINUTE,
  GEMINI_INLINE_MAX_BYTES,
  GOOGLE_CHIRP_INLINE_MAX_BYTES,
  MAX_ASSISTANT_IMAGE_BYTES,
  MAX_AUDIO_SIZE_BYTES,
  OPENAI_INLINE_MAX_BYTES,
} from './constants';

// These are the entitlement and size refusals the native clients branch on.
// The status code, the `error` string and the numeric fields are a wire
// contract with macOS (`fileTooLargeContext`) and Windows
// (`RustCoreMapping.CreditContext` / `FileTooLargeContext`), so they are pinned
// here rather than left to a snapshot.

const MB = 1024 * 1024;

async function bodyOf(response: Response): Promise<Record<string, unknown>> {
  return (await response.json()) as Record<string, unknown>;
}

describe('errorResponse', () => {
  test('sets the status, the error/message pair and a JSON content type', async () => {
    const response = errorResponse(418, 'Teapot', 'Short and stout');

    expect(response.status).toBe(418);
    expect(response.headers.get('content-type')).toBe('application/json');
    expect(await bodyOf(response)).toEqual({ error: 'Teapot', message: 'Short and stout' });
  });

  test('merges extra fields alongside error and message', async () => {
    const response = errorResponse(429, 'Rate limited', 'Slow down', { retry_after: 30 });

    expect(await bodyOf(response)).toEqual({
      error: 'Rate limited',
      message: 'Slow down',
      retry_after: 30,
    });
  });

  test('carries the CORS headers', () => {
    const response = errorResponse(500, 'Boom', 'Upstream failed');

    for (const [name, value] of Object.entries(CORS_HEADERS)) {
      expect(response.headers.get(name)).toBe(value);
    }
  });
});

describe('jsonResponse', () => {
  test('answers 200 with the serialized payload and the CORS headers', async () => {
    const response = jsonResponse({ text: 'hello', duration: 1.5 });

    expect(response.status).toBe(200);
    expect(response.headers.get('content-type')).toBe('application/json');
    expect(response.headers.get('Access-Control-Allow-Origin')).toBe('*');
    expect(await bodyOf(response)).toEqual({ text: 'hello', duration: 1.5 });
  });

  test('adds caller-supplied headers, which is how the provider header is returned', () => {
    const response = jsonResponse({ text: '' }, { 'X-STT-Provider': 'deepgram' });

    expect(response.headers.get('X-STT-Provider')).toBe('deepgram');
    expect(response.headers.get('Access-Control-Allow-Origin')).toBe('*');
  });
});

describe('licence refusals', () => {
  test('a missing licence is a 401 the client can tell from an invalid one', async () => {
    const response = licenseRequiredResponse();

    expect(response.status).toBe(401);
    expect((await bodyOf(response))['error']).toBe('License required');
  });

  test('an invalid licence is a 401 with its own error string', async () => {
    const response = invalidLicenseResponse();

    expect(response.status).toBe(401);
    expect((await bodyOf(response))['error']).toBe('Invalid license');
  });

  test('the two 401s do not share an error string', async () => {
    const required = (await bodyOf(licenseRequiredResponse()))['error'];
    const invalid = (await bodyOf(invalidLicenseResponse()))['error'];

    expect(required).not.toBe(invalid);
  });
});

describe('insufficientCreditsResponse', () => {
  test('answers 402 with the raw balance and estimate, unrounded', async () => {
    const response = insufficientCreditsResponse(12.5, 30.2);

    expect(response.status).toBe(402);

    const body = await bodyOf(response);
    expect(body['error']).toBe('Insufficient credits');
    expect(body['credits_remaining']).toBe(12.5);
    expect(body['credits_per_minute']).toBe(CREDITS_PER_MINUTE);
  });

  test('floors the minutes it promises and ceils the minutes it demands', async () => {
    // Under-promise / over-demand: the client shows these to the user, so
    // neither number may flatter the balance.
    const body = await bodyOf(insufficientCreditsResponse(12.5, 30.2));

    expect(body['minutes_remaining']).toBe(Math.floor(12.5 / CREDITS_PER_MINUTE));
    expect(body['minutes_remaining']).toBe(1);
    expect(body['minutes_required']).toBe(Math.ceil(30.2 / CREDITS_PER_MINUTE));
    expect(body['minutes_required']).toBe(5);
  });

  test('reports 0 minutes remaining for a balance just short of a minute', async () => {
    const body = await bodyOf(insufficientCreditsResponse(CREDITS_PER_MINUTE - 0.1, 0.1));

    expect(body['minutes_remaining']).toBe(0);
  });

  test('never demands 0 minutes for a non-zero estimate', async () => {
    const body = await bodyOf(insufficientCreditsResponse(0, 0.1));

    expect(body['minutes_required']).toBe(1);
  });

  test('states both credit figures to one decimal place in the message', async () => {
    const body = await bodyOf(insufficientCreditsResponse(0.5, 6.3));

    expect(body['message']).toBe(
      'You have 0.5 credits remaining. This transcription requires approximately 6.3 credits.'
    );
  });
});

describe('size refusals', () => {
  test('an oversized audio file is a 413 with both size fields', async () => {
    const response = fileTooLargeResponse(3 * MB, 2 * MB);

    expect(response.status).toBe(413);

    const body = await bodyOf(response);
    expect(body['error']).toBe('File too large');
    expect(body['max_size_mb']).toBe(2);
    expect(body['actual_size_mb']).toBe(3);
  });

  test('reports the actual size to two decimals as a number, not a string', async () => {
    const body = await bodyOf(fileTooLargeResponse(1_234_567, 2 * MB));

    // Windows reads this with TryGetDouble, macOS as a Double.
    expect(typeof body['actual_size_mb']).toBe('number');
    expect(body['actual_size_mb']).toBe(1.18);
  });

  test('reports max_size_mb as a whole number, which is how the clients read it', async () => {
    // Windows uses JsonElement.TryGetInt64 and returns 0 (no limit known) if
    // the value is fractional, so a non-integer here silently drops the hint.
    for (const maxBytes of [
      MAX_AUDIO_SIZE_BYTES,
      GOOGLE_CHIRP_INLINE_MAX_BYTES,
      GEMINI_INLINE_MAX_BYTES,
      OPENAI_INLINE_MAX_BYTES,
    ]) {
      const body = await bodyOf(fileTooLargeResponse(maxBytes + 1, maxBytes));

      expect(Number.isInteger(body['max_size_mb'])).toBe(true);
    }
  });

  test('never advertises a limit larger than the one actually enforced', async () => {
    // The client turns max_size_mb back into bytes (`mv * 1_048_576`) and uses
    // it to decide what to re-send. If the rounding ever went up, the client
    // would retry a file that is guaranteed to 413 again.
    for (const maxBytes of [
      MAX_AUDIO_SIZE_BYTES,
      GOOGLE_CHIRP_INLINE_MAX_BYTES,
      GEMINI_INLINE_MAX_BYTES,
      OPENAI_INLINE_MAX_BYTES,
      MAX_ASSISTANT_IMAGE_BYTES,
    ]) {
      const body = await bodyOf(fileTooLargeResponse(maxBytes + 1, maxBytes));

      expect((body['max_size_mb'] as number) * MB).toBeLessThanOrEqual(maxBytes);
    }
  });

  test('an oversized image is a 413 the client can tell from an oversized audio file', async () => {
    const response = imageTooLargeResponse(11 * MB, MAX_ASSISTANT_IMAGE_BYTES);

    expect(response.status).toBe(413);

    const body = await bodyOf(response);
    expect(body['error']).toBe('Image too large');
    expect(body['max_size_mb']).toBe(10);
    expect(body['actual_size_mb']).toBe(11);
  });
});

describe('request-shape refusals', () => {
  test('a wrong Content-Type is a 400 that echoes what was received', async () => {
    const response = invalidContentTypeResponse('audio/*', 'application/json');

    expect(response.status).toBe(400);

    const body = await bodyOf(response);
    expect(body['error']).toBe('Invalid Content-Type');
    expect(body['received']).toBe('application/json');
    expect(body['message']).toContain('audio/*');
  });

  test('a missing Content-Length is a 400', async () => {
    const response = missingContentLengthResponse();

    expect(response.status).toBe(400);
    expect((await bodyOf(response))['error']).toBe('Missing Content-Length');
  });
});

describe('CORS_HEADERS', () => {
  test('allows the methods the service actually serves, including the preflight', () => {
    expect(CORS_HEADERS['Access-Control-Allow-Methods']).toContain('POST');
    expect(CORS_HEADERS['Access-Control-Allow-Methods']).toContain('OPTIONS');
  });

  test('rides on every refusal, so a browser caller sees the status instead of a CORS error', () => {
    const refusals = [
      licenseRequiredResponse(),
      invalidLicenseResponse(),
      insufficientCreditsResponse(1, 2),
      fileTooLargeResponse(3 * MB, 2 * MB),
      imageTooLargeResponse(11 * MB, 10 * MB),
      invalidContentTypeResponse('audio/*', 'text/plain'),
      missingContentLengthResponse(),
    ];

    for (const response of refusals) {
      expect(response.headers.get('Access-Control-Allow-Origin')).toBe('*');
    }
  });
});
