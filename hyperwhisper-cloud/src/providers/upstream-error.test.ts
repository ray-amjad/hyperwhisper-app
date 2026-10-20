import { describe, expect, test } from 'bun:test';
import {
  readUpstreamError,
  safeErrorToken,
  summarizeUpstreamError,
  upstreamErrorLogFields,
  upstreamErrorReason,
} from './upstream-error';

const MARKER = 'SECRET_TRANSCRIPT_MARKER_2163';

describe('safeErrorToken', () => {
  test('keeps short identifier-like codes and integer codes', () => {
    expect(safeErrorToken('invalid_request_error')).toBe('invalid_request_error');
    expect(safeErrorToken('INVALID_ARGUMENT')).toBe('INVALID_ARGUMENT');
    expect(safeErrorToken('quota.exceeded:v2-x')).toBe('quota.exceeded:v2-x');
    expect(safeErrorToken(400)).toBe('400');
  });

  test('drops prose, empty strings, over-long tokens and non-strings', () => {
    expect(safeErrorToken('Bad Request')).toBeUndefined();
    expect(safeErrorToken(`could not parse ${MARKER}`)).toBeUndefined();
    expect(safeErrorToken('')).toBeUndefined();
    expect(safeErrorToken('a'.repeat(65))).toBeUndefined();
    expect(safeErrorToken('a'.repeat(64))).toBe('a'.repeat(64));
    expect(safeErrorToken('{"x":1}')).toBeUndefined();
    expect(safeErrorToken(1.5)).toBeUndefined();
    expect(safeErrorToken(null)).toBeUndefined();
    expect(safeErrorToken({ code: 'x' })).toBeUndefined();
  });
});

describe('summarizeUpstreamError', () => {
  const cases: Array<[string, string, Record<string, unknown>]> = [
    ['OpenAI-compatible',
      JSON.stringify({ error: { message: MARKER, type: 'invalid_request_error', code: 'context_length_exceeded' } }),
      { errorCode: 'context_length_exceeded', errorType: 'invalid_request_error' }],
    ['Anthropic',
      JSON.stringify({ type: 'error', error: { type: 'invalid_request_error', message: MARKER } }),
      { errorType: 'invalid_request_error' }],
    ['Google',
      JSON.stringify({ error: { code: 400, message: MARKER, status: 'INVALID_ARGUMENT' } }),
      { errorCode: '400', errorType: 'INVALID_ARGUMENT' }],
    ['Deepgram',
      JSON.stringify({ err_code: 'Bad Request', err_msg: MARKER, request_id: 'dg-123' }),
      { upstreamRequestId: 'dg-123' }],
    ['ElevenLabs',
      JSON.stringify({ detail: { status: 'invalid_language', message: MARKER } }),
      { errorType: 'invalid_language' }],
    ['Soniox',
      JSON.stringify({ status_code: 400, error_type: 'invalid_request', message: MARKER, request_id: 'sx-1' }),
      { errorType: 'invalid_request', upstreamRequestId: 'sx-1' }],
    ['AssemblyAI (free-text error)',
      JSON.stringify({ error: `language ${MARKER} is not supported` }),
      {}],
  ];

  test.each(cases)('%s: keeps identifiers, never the message', (_label, body, expected) => {
    const fields = summarizeUpstreamError(400, undefined, body);
    expect(fields).toEqual({ status: 400, bodyLength: body.length, ...expected });
    expect(JSON.stringify(fields)).not.toContain(MARKER);
  });

  test('a non-JSON body gives only status and length', () => {
    const body = `<html>${MARKER}</html>`;
    expect(summarizeUpstreamError(502, undefined, body)).toEqual({ status: 502, bodyLength: body.length });
  });

  test('an unreadable body has length -1', () => {
    expect(summarizeUpstreamError(500, undefined, undefined)).toEqual({ status: 500, bodyLength: -1 });
  });

  test('takes the request id from the response headers, and validates it', () => {
    expect(summarizeUpstreamError(400, new Headers({ 'request-id': 'req_abc' }), '').upstreamRequestId).toBe('req_abc');
    expect(summarizeUpstreamError(400, new Headers({ 'x-request-id': `has space ${MARKER}` }), '').upstreamRequestId).toBeUndefined();
    // A header id wins over a body id.
    expect(summarizeUpstreamError(
      400,
      new Headers({ 'apim-request-id': 'hdr-1' }),
      JSON.stringify({ request_id: 'body-1' }),
    ).upstreamRequestId).toBe('hdr-1');
  });

  test('a marker smuggled into a code field as prose is dropped', () => {
    const fields = summarizeUpstreamError(400, undefined, JSON.stringify({ error: { code: `bad: ${MARKER} here`, type: MARKER + ' x' } }));
    expect(fields.errorCode).toBeUndefined();
    expect(fields.errorType).toBeUndefined();
  });
});

describe('readUpstreamError', () => {
  test('returns content-free fields and the 500-character classifier text', async () => {
    const body = JSON.stringify({ error: { code: 'quota_exceeded', message: MARKER } }) + 'x'.repeat(600);
    const { fields, classifierText } = await readUpstreamError(new Response(body, { status: 429 }));
    expect(fields).toEqual({ status: 429, bodyLength: body.length });
    expect(classifierText).toBe(`${body.slice(0, 500)}...`);
  });

  test('parses a short JSON body', async () => {
    const body = JSON.stringify({ error: { code: 'quota_exceeded', message: MARKER } });
    const { fields, classifierText } = await readUpstreamError(new Response(body, { status: 429, headers: { 'x-request-id': 'r1' } }));
    expect(fields).toEqual({ status: 429, bodyLength: body.length, errorCode: 'quota_exceeded', upstreamRequestId: 'r1' });
    expect(classifierText).toBe(body);
  });

  test('never throws on an unreadable body', async () => {
    const unreadable = { status: 503, headers: new Headers(), text: async () => { throw new Error('consumed'); } } as unknown as Response;
    const { fields, classifierText } = await readUpstreamError(unreadable);
    expect(fields).toEqual({ status: 503, bodyLength: -1 });
    expect(classifierText).toBe('<unreadable>');
  });
});

describe('upstreamErrorReason / upstreamErrorLogFields', () => {
  test('reason prefers the code, then the type, then HTTP <status>', () => {
    expect(upstreamErrorReason({ status: 400, bodyLength: 1, errorCode: 'c', errorType: 't' })).toBe('c');
    expect(upstreamErrorReason({ status: 400, bodyLength: 1, errorType: 't' })).toBe('t');
    expect(upstreamErrorReason({ status: 400, bodyLength: 1 })).toBe('HTTP 400');
  });

  test('log fields omit absent members', () => {
    expect(upstreamErrorLogFields({ status: 400, bodyLength: 3 })).toEqual({ status: 400, bodyLength: 3 });
    expect(upstreamErrorLogFields({ status: 400, bodyLength: 3, errorCode: 'c', upstreamRequestId: 'r' }))
      .toEqual({ status: 400, bodyLength: 3, errorCode: 'c', upstreamRequestId: 'r' });
  });
});

// A static guard, kept narrow so it cannot go brittle: the property name and
// the helper that carried raw error bodies into provider logs are gone from
// the provider adapters and the routes, and must not come back (#2163).
describe('no raw error-body preview in providers or routes', () => {
  test('no non-test file under src/providers or src/routes names bodyPreview / readErrorBodyPreview', async () => {
    const offenders: string[] = [];
    for (const dir of ['providers', 'routes']) {
      const glob = new Bun.Glob('*.ts');
      for await (const name of glob.scan({ cwd: `${import.meta.dir}/../${dir}` })) {
        if (name.endsWith('.test.ts')) continue;
        const text = await Bun.file(`${import.meta.dir}/../${dir}/${name}`).text();
        if (/\bbodyPreview\b|\breadErrorBodyPreview\b/.test(text)) offenders.push(`${dir}/${name}`);
      }
    }
    expect(offenders).toEqual([]);
  });
});
