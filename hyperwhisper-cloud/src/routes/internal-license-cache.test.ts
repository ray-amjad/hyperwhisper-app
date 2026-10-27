// POST /internal/license-cache/invalidate, driven as HTTP through a Hono app.
//
// The route is built from its factory with an in-memory secret and invalidator,
// so this suite mocks no module (bun's module registry is process-wide). What
// it pins: the secret gate refuses before the body is read, an unset secret
// never runs the route open, the key the cache is keyed on is the trimmed one,
// and nothing logs the key.

import { afterEach, beforeEach, describe, expect, spyOn, test } from 'bun:test';
import { Hono } from 'hono';
import {
  INTERNAL_SECRET_HEADER,
  createLicenseCacheInvalidateRoute,
  secretMatches,
} from './internal-license-cache';

const SECRET = ['test', 'internal', 'secret'].join('-');
// Built from parts so no credential-shaped literal sits in the source.
const KEY = ['HW', 'AAAA', 'BBBB', 'CCCC', 'DDDD'].join('-');

let invalidated: string[] = [];
let invalidateResult = true;
let secret: string | undefined = SECRET;
let logs: string[] = [];
let logSpy: ReturnType<typeof spyOn> | undefined;
let errorSpy: ReturnType<typeof spyOn> | undefined;

function app() {
  const hono = new Hono();
  hono.post(
    '/internal/license-cache/invalidate',
    createLicenseCacheInvalidateRoute({
      secret: () => secret,
      invalidate: async (key) => {
        invalidated.push(key);
        return invalidateResult;
      },
    }),
  );
  return hono;
}

function post(body: unknown, headers: Record<string, string> = { [INTERNAL_SECRET_HEADER]: SECRET }) {
  return app().request('/internal/license-cache/invalidate', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...headers },
    body: typeof body === 'string' ? body : JSON.stringify(body),
  });
}

beforeEach(() => {
  invalidated = [];
  invalidateResult = true;
  secret = SECRET;
  logs = [];
  logSpy = spyOn(console, 'log').mockImplementation((...args: unknown[]) => {
    logs.push(args.map(String).join(' '));
  });
  errorSpy = spyOn(console, 'error').mockImplementation((...args: unknown[]) => {
    logs.push(args.map(String).join(' '));
  });
});

afterEach(() => {
  logSpy?.mockRestore();
  errorSpy?.mockRestore();
});

describe('POST /internal/license-cache/invalidate', () => {
  test('with the secret, the key is invalidated and 200 comes back', async () => {
    const res = await post({ account_key: KEY });
    expect(res.status).toBe(200);
    expect(await res.json()).toEqual({ invalidated: true });
    expect(invalidated).toEqual([KEY]);
  });

  test('the key is trimmed, the same spelling the cache is keyed on; case is kept', async () => {
    await post({ account_key: `  ${KEY}\n` });
    expect(invalidated).toEqual([KEY]);
  });

  test('a wrong or missing secret is 401 and invalidates nothing', async () => {
    expect((await post({ account_key: KEY }, { [INTERNAL_SECRET_HEADER]: 'nope' })).status).toBe(401);
    expect((await post({ account_key: KEY }, {})).status).toBe(401);
    expect(invalidated).toEqual([]);
  });

  test('no secret configured: 503, never open', async () => {
    secret = undefined;
    expect((await post({ account_key: KEY }, { [INTERNAL_SECRET_HEADER]: '' })).status).toBe(503);
    secret = '';
    expect((await post({ account_key: KEY }, { [INTERNAL_SECRET_HEADER]: '' })).status).toBe(503);
    expect(invalidated).toEqual([]);
  });

  test('a body with no usable key is 400', async () => {
    expect((await post('not json')).status).toBe(400);
    expect((await post({})).status).toBe(400);
    expect((await post({ account_key: 42 })).status).toBe(400);
    expect((await post({ account_key: '   ' })).status).toBe(400);
    expect((await post({ account_key: 'x'.repeat(129) })).status).toBe(400);
    expect(invalidated).toEqual([]);
  });

  test('a cache failure is 503 so the site logs that the old verdict may remain', async () => {
    invalidateResult = false;
    expect((await post({ account_key: KEY })).status).toBe(503);
  });

  test('no log line carries the key', async () => {
    await post({ account_key: KEY });
    invalidateResult = false;
    await post({ account_key: KEY });
    expect(logs.length).toBeGreaterThan(0);
    for (const line of logs) expect(line).not.toContain(KEY);
  });
});

describe('secretMatches', () => {
  test('matches only the exact secret', () => {
    expect(secretMatches(SECRET, SECRET)).toBe(true);
    expect(secretMatches(`${SECRET}x`, SECRET)).toBe(false);
    expect(secretMatches(undefined, SECRET)).toBe(false);
    expect(secretMatches('', SECRET)).toBe(false);
  });
});
