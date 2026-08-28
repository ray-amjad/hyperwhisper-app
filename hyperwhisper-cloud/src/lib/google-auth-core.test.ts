// Before `createGoogleAuth` took a `GoogleTokenMinter`, nothing in this code
// was reachable from a test: minting went straight through a `JWT` client
// built from `GOOGLE_SERVICE_ACCOUNT_JSON`, so exercising it needed a real RSA
// private key and a live call to Google's OAuth endpoint. The in-flight state
// was module-level too, so even a mocked run would have leaked one test's
// pending mint into the next.
//
// This suite imports lib/google-auth-core, not lib/google-auth, and mocks
// nothing at all. bun's `mock.module` is process-wide: google-chirp.test.ts
// replaces `../lib/google-auth` and seven suites replace `../lib/redis` for
// the whole run, so importing either from here would hand this test another
// suite's stub. Both I/O edges are injected instead.

import { describe, expect, test } from 'bun:test';
import { createGoogleAuth, type GoogleTokenCache } from './google-auth-core';

/** A cache that always misses, so every call reaches the minter. */
function alwaysMissCache(): GoogleTokenCache {
  return {
    read: async () => null,
    write: async () => {},
    clear: async () => {},
  };
}

describe('createGoogleAuth single-flight minting', () => {
  test('a cold-cache burst mints exactly one token and shares it', async () => {
    let authorizeCalls = 0;
    let releaseMint: (credentials: { access_token: string; expiry_date: number }) => void = () => {};
    const pendingMint = new Promise<{ access_token: string; expiry_date: number }>((resolve) => {
      releaseMint = resolve;
    });

    const auth = createGoogleAuth(
      {
        authorize: () => {
          authorizeCalls += 1;
          return pendingMint;
        },
      },
      alwaysMissCache(),
    );

    // Five concurrent cold-cache callers — a machine cold-start after deploy.
    const burst = Promise.all(Array.from({ length: 5 }, () => auth.getGoogleAccessToken()));

    releaseMint({ access_token: 'token-from-one-mint', expiry_date: Date.now() + 3_600_000 });

    const tokens = await burst;

    expect(authorizeCalls).toBe(1);
    expect(tokens).toEqual(Array.from({ length: 5 }, () => 'token-from-one-mint'));
  });
});
