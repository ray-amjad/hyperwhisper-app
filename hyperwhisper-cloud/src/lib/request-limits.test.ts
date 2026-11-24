import { describe, expect, test } from 'bun:test';

import { MAX_POST_PROCESS_BODY_BYTES } from './constants';
import { capWords, validateContentLength } from './request-limits';
import { MAX_INVALIDATE_BODY_BYTES } from '../routes/internal-license-cache';

async function refusal(header: string | undefined, maxBytes: number) {
  const check = validateContentLength(header, maxBytes);
  if (check.ok) throw new Error('expected a refusal');
  return { status: check.response.status, body: (await check.response.json()) as Record<string, unknown> };
}

describe('validateContentLength 413 names its cap (#2704)', () => {
  test('the internal 1 KiB cap says 1 KB, not 0 MB', async () => {
    const { status, body } = await refusal('1025', MAX_INVALIDATE_BODY_BYTES);

    expect(status).toBe(413);
    expect(body['message']).toBe('Request body must be 1 KB or smaller');
    expect(body['max_size_bytes']).toBe(1024);
    expect(body['content_length']).toBe(1025);
  });

  test('the /post-process 2 MiB cap still says 2 MB', async () => {
    const { status, body } = await refusal(String(MAX_POST_PROCESS_BODY_BYTES + 1), MAX_POST_PROCESS_BODY_BYTES);

    expect(status).toBe(413);
    expect(body['message']).toBe('Request body must be 2 MB or smaller');
  });

  test('no cap in use is ever spoken as 0', () => {
    for (const maxBytes of [MAX_INVALIDATE_BODY_BYTES, MAX_POST_PROCESS_BODY_BYTES]) {
      expect(capWords(maxBytes)).not.toMatch(/^0\b/);
    }
  });

  test('a cap that is not a whole KiB is spoken in bytes', () => {
    expect(capWords(1000)).toBe('1000 bytes');
    expect(capWords(1536)).toBe('1536 bytes');
    expect(capWords(3 * 1024)).toBe('3 KB');
  });

  test('a body at the cap passes', () => {
    expect(validateContentLength('1024', MAX_INVALIDATE_BODY_BYTES)).toEqual({ ok: true });
  });
});
