import { describe, expect, test } from 'bun:test';
import {
  CHARGE_BODY_MAX_BYTES,
  MAX_METADATA_STRING_BYTES,
  buildChargeBody,
  truncateForJson,
  utf8ByteLength,
} from './charge-body';

/** The licence API's own limit (`MAX_BODY_BYTES` in the site's credits route). */
const LICENCE_API_MAX_BODY_BYTES = 16 * 1024;

// A key-shaped stand-in, built at run time so no key literal sits in the file.
const KEY = ['account', 'key', 'for', 'tests'].join('-');

/** What `/transcribe` charges today, with the two caller-supplied labels swapped in. */
function transcribeMetadata(language: unknown, mode: unknown): Record<string, unknown> {
  return {
    audio_duration_seconds: 12.5,
    transcription_cost_usd: 0.00123,
    language,
    mode,
    endpoint: '/transcribe',
    stt_provider: 'Deepgram',
    stt_model: undefined,
  };
}

function parse(body: string): { license_key: unknown; amount: unknown; metadata: Record<string, unknown> } {
  return JSON.parse(body) as { license_key: unknown; amount: unknown; metadata: Record<string, unknown> };
}

describe('buildChargeBody', () => {
  test('sends ordinary metadata exactly as JSON.stringify did before', () => {
    const metadata = transcribeMetadata('en', '00000000-0000-0000-0000-000000000001');
    const charge = buildChargeBody(KEY, 3.4, metadata);

    expect(charge.truncated).toBe(false);
    expect(charge.body).toBe(JSON.stringify({ license_key: KEY, amount: 3.4, metadata }));
  });

  // The CTF #1a/b body was 17,016 bytes, from a long `mode` / `language`.
  const HUGE_INPUTS: Array<[string, string]> = [
    ['ASCII', 'a'.repeat(20_000)],
    ['3-byte characters', '語'.repeat(20_000)],
    ['4-byte characters', '😀'.repeat(20_000)],
    ['control characters (6-byte JSON escapes)', '\u0001'.repeat(20_000)],
    ['quotes and backslashes', '"\\'.repeat(20_000)],
    ['lone surrogates', '\ud800'.repeat(20_000)],
  ];

  test.each(HUGE_INPUTS)('stays under the bound with a huge mode and language of %s, amount unchanged', (_label, huge) => {
    const charge = buildChargeBody(KEY, 7.3, transcribeMetadata(huge, huge));

    expect(utf8ByteLength(charge.body)).toBeLessThanOrEqual(CHARGE_BODY_MAX_BYTES);
    expect(CHARGE_BODY_MAX_BYTES).toBeLessThan(LICENCE_API_MAX_BODY_BYTES / 2);
    expect(charge.truncated).toBe(true);
    const sent = parse(charge.body);
    expect(sent.amount).toBe(7.3);
    expect(sent.license_key).toBe(KEY);
    expect(sent.metadata.endpoint).toBe('/transcribe');
    expect(sent.metadata.metadata_truncated).toBe(true);
    // Each label is a prefix of what was sent, never a split character.
    expect(huge.startsWith(sent.metadata.language as string)).toBe(true);
    expect(utf8ByteLength(JSON.stringify(sent.metadata.language))).toBeLessThanOrEqual(MAX_METADATA_STRING_BYTES + 2);
  });

  test('stays under the bound when the metadata has thousands of fields', () => {
    const metadata: Record<string, unknown> = { endpoint: '/transcribe' };
    for (let i = 0; i < 5_000; i += 1) metadata[`field_${i}`] = 'x'.repeat(100);

    const charge = buildChargeBody(KEY, 1.1, metadata);

    expect(utf8ByteLength(charge.body)).toBeLessThanOrEqual(CHARGE_BODY_MAX_BYTES);
    const sent = parse(charge.body);
    expect(sent.amount).toBe(1.1);
    // Dropped from the end, so the fields first in line survive.
    expect(sent.metadata.endpoint).toBe('/transcribe');
    expect(sent.metadata.metadata_truncated).toBe(true);
  });

  test('drops values that are not plain JSON scalars', () => {
    const charge = buildChargeBody(KEY, 2, { endpoint: '/transcribe', nested: { big: 'x'.repeat(50_000) }, list: [1, 2] });

    const sent = parse(charge.body);
    expect(sent.metadata).toEqual({ endpoint: '/transcribe', metadata_truncated: true });
    expect(sent.amount).toBe(2);
  });

  test('never changes the amount, for any amount', () => {
    for (const amount of [0.1, 1, 2.8, 123.4, 99999.9]) {
      const sent = parse(buildChargeBody(KEY, amount, transcribeMetadata('x'.repeat(30_000), 'y'.repeat(30_000))).body);
      expect(sent.amount).toBe(amount);
    }
  });
});

describe('truncateForJson', () => {
  test('returns a short value unchanged', () => {
    expect(truncateForJson('en-US', MAX_METADATA_STRING_BYTES)).toBe('en-US');
  });

  test('never splits a multi-byte character', () => {
    const cut = truncateForJson('é'.repeat(100), 9);
    expect(cut).toBe('éééé');
  });

  test('never splits a surrogate pair', () => {
    const cut = truncateForJson('😀'.repeat(100), 10);
    expect(cut).toBe('😀😀');
  });
});
