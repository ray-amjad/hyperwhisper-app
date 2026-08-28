// Before `createGoogleAuth` took a `GoogleTokenMinter`, nothing in this file
// was reachable from a test: minting went straight through a `JWT` client
// built from `GOOGLE_SERVICE_ACCOUNT_JSON`, so exercising it needed a real RSA
// private key and a live call to Google's OAuth endpoint. The in-flight state
// was module-level too, so even a mocked run would have leaked one test's
// pending mint into the next.
//
// Redis is left real on purpose. `@upstash/redis` speaks HTTP, so a fake
// global `fetch` serves its `/pipeline` protocol — no `mock.module`, which
// leaks process-wide across suites in this repo.

import { afterEach, beforeEach, describe, expect, test } from 'bun:test';

process.env.UPSTASH_REDIS_URL ??= 'https://google-auth-test.upstash.io';
process.env.UPSTASH_REDIS_TOKEN ??= 'google-auth-test-token';

const { createGoogleAuth } = await import('./google-auth');

const originalFetch = globalThis.fetch;

/**
 * Answers every Upstash command with a cache MISS, so each call mints.
 * The client auto-pipelines, so one request can carry N commands and the
 * response must carry exactly N results.
 */
function upstashCacheMissFetch(): typeof fetch {
  return (async (_input: unknown, init?: { body?: unknown }) => {
    const commands: unknown = JSON.parse(String(init?.body ?? '[]'));
    const count = Array.isArray(commands) ? commands.length : 1;
    return Response.json(Array.from({ length: count }, () => ({ result: null })));
  }) as unknown as typeof fetch;
}

beforeEach(() => {
  globalThis.fetch = upstashCacheMissFetch();
});

afterEach(() => {
  globalThis.fetch = originalFetch;
});

describe('createGoogleAuth single-flight minting', () => {
  // A guard, not a behaviour test, and the reason it exists is worth the lines.
  // `mock.module` is process-wide in bun: a factory in an earlier suite that
  // replaces `lib/google-auth` without forwarding every export leaves this
  // whole file calling `undefined`. `google-chirp.test.ts` did exactly that,
  // and because `bun test` does not fix the file order, the run was red only
  // when it happened to load first — google-chirp at position 4, this file at
  // 33. It read as a race in the single-flight code under test, and the real
  // message ("'createGoogleAuth' is undefined") pointed nowhere near the file
  // that caused it. This turns any recurrence into a named test that says so.
  test('the module under test is the real one, not a mock.module replacement', () => {
    expect(typeof createGoogleAuth).toBe('function');
  });

  test('a cold-cache burst mints exactly one token and shares it', async () => {
    let authorizeCalls = 0;
    let releaseMint: (credentials: { access_token: string; expiry_date: number }) => void = () => {};
    const pendingMint = new Promise<{ access_token: string; expiry_date: number }>((resolve) => {
      releaseMint = resolve;
    });

    const auth = createGoogleAuth({
      authorize: () => {
        authorizeCalls += 1;
        return pendingMint;
      },
    });

    // Five concurrent cold-cache callers — a machine cold-start after deploy.
    const burst = Promise.all(Array.from({ length: 5 }, () => auth.getGoogleAccessToken()));

    releaseMint({ access_token: 'token-from-one-mint', expiry_date: Date.now() + 3_600_000 });

    const tokens = await burst;

    expect(authorizeCalls).toBe(1);
    expect(tokens).toEqual(Array.from({ length: 5 }, () => 'token-from-one-mint'));
  });
});
