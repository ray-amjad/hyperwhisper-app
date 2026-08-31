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
import { computeCacheTtlSeconds, createGoogleAuth, type GoogleTokenCache } from './google-auth-core';

/** A cache that always misses, so every call reaches the minter. */
function alwaysMissCache(): GoogleTokenCache {
  return {
    read: async () => null,
    write: async () => {},
    clear: async () => {},
  };
}

interface RecordingCache extends GoogleTokenCache {
  writes: Array<{ token: string; ttlSeconds: number }>;
  clears: number;
  reads: number;
}

/**
 * A cache that records every call. `stored` starts empty (a miss) unless a
 * test seeds it. Reads and writes go through the same slot, so a test can
 * assert the write-then-read round trip.
 */
function recordingCache(stored: string | null = null): RecordingCache {
  const cache: RecordingCache = {
    writes: [],
    clears: 0,
    reads: 0,
    read: async () => {
      cache.reads += 1;
      return stored;
    },
    write: async (token, ttlSeconds) => {
      cache.writes.push({ token, ttlSeconds });
      stored = token;
    },
    clear: async () => {
      cache.clears += 1;
      stored = null;
    },
  };
  return cache;
}

/** A minter that hands back a fixed token and counts how often it was asked. */
function countingMinter(token = 'minted-token', expiryDate: number | null = null) {
  const state = { calls: 0 };
  return {
    state,
    minter: {
      authorize: async () => {
        state.calls += 1;
        return { access_token: token, expiry_date: expiryDate };
      },
    },
  };
}

describe('createGoogleAuth single-flight minting', () => {
  // Kept from #341, which met this as a red CI run reading
  // "'createGoogleAuth' is undefined" inside the single-flight test — a message
  // pointing nowhere near google-chirp.test.ts, the suite whose lossy
  // `mock.module` factory actually caused it. Importing lib/google-auth-core
  // takes this file off every mocked path, so the guard should never fire. If a
  // future suite mocks this module too, it fails as a named test that says so
  // rather than as a defect in the code under test.
  test('the module under test is the real one, not a mock.module replacement', () => {
    expect(typeof createGoogleAuth).toBe('function');
  });

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

describe('computeCacheTtlSeconds', () => {
  // Google's `expiry_date` is a wall-clock epoch in ms. The cached TTL is that
  // remainder minus a 10 min safety margin, so a token never survives in Redis
  // right up to the second it stops working — Chirp is a self-only chain, so a
  // stale token is a 502 with no fallback provider to absorb it.
  test('derives the TTL from the expiry, 10 minutes short of it', () => {
    const ttl = computeCacheTtlSeconds(Date.now() + 7_200_000); // 2 h

    // 7200 s remaining - 600 s margin. Allow 2 s for clock drift inside the call.
    expect(ttl).toBeGreaterThanOrEqual(6598);
    expect(ttl).toBeLessThanOrEqual(6600);
  });

  test('caps a pathological near-expiry token at the 60 s floor, never negative', () => {
    // Exactly the safety margin left: 600 - 600 = 0, which is under the floor.
    expect(computeCacheTtlSeconds(Date.now() + 600_000)).toBe(60);
    // Already expired — the arithmetic goes negative and must still clamp.
    expect(computeCacheTtlSeconds(Date.now() - 3_600_000)).toBe(60);
  });

  test('does not clamp once the remainder clears the floor', () => {
    const ttl = computeCacheTtlSeconds(Date.now() + 665_000); // 665 - 600 = 65

    expect(ttl).toBeGreaterThanOrEqual(64);
    expect(ttl).toBeLessThanOrEqual(65);
  });

  test('falls back to 50 minutes when the minter omits a usable expiry', () => {
    expect(computeCacheTtlSeconds(null)).toBe(3000);
    expect(computeCacheTtlSeconds(undefined)).toBe(3000);
    expect(computeCacheTtlSeconds(Number.NaN)).toBe(3000);
    expect(computeCacheTtlSeconds(Number.POSITIVE_INFINITY)).toBe(3000);
    // Google has been seen returning the epoch as a string. `typeof` rejects it
    // rather than letting `'…' - Date.now()` produce a NaN TTL.
    expect(computeCacheTtlSeconds('1800000000000' as unknown as number)).toBe(3000);
  });
});

describe('createGoogleAuth cache behaviour', () => {
  test('a cache hit is returned without minting', async () => {
    const cache = recordingCache('token-already-in-redis');
    const { state, minter } = countingMinter();

    const auth = createGoogleAuth(minter, cache);

    expect(await auth.getGoogleAccessToken()).toBe('token-already-in-redis');
    expect(state.calls).toBe(0);
    expect(cache.writes).toEqual([]);
  });

  test('an empty cached string is treated as a miss, not as a token', async () => {
    // A truncated or cleared Upstash value must not be handed to Google as a
    // bearer token — that is a 401 on every request until the TTL runs out.
    const cache = recordingCache('');
    const { state, minter } = countingMinter('freshly-minted');

    const auth = createGoogleAuth(minter, cache);

    expect(await auth.getGoogleAccessToken()).toBe('freshly-minted');
    expect(state.calls).toBe(1);
  });

  test('the minted token is written back with the expiry-derived TTL', async () => {
    const cache = recordingCache();
    const { minter } = countingMinter('minted-token', Date.now() + 7_200_000);

    const auth = createGoogleAuth(minter, cache);
    await auth.getGoogleAccessToken();

    expect(cache.writes).toHaveLength(1);
    expect(cache.writes[0]!.token).toBe('minted-token');
    expect(cache.writes[0]!.ttlSeconds).toBeGreaterThanOrEqual(6598);
    expect(cache.writes[0]!.ttlSeconds).toBeLessThanOrEqual(6600);
  });

  test('a cache read failure falls through to a mint instead of throwing', async () => {
    // Upstash being unreachable must degrade to "pay the mint cost", not to a
    // failed transcription.
    const { state, minter } = countingMinter('minted-after-read-failure');
    const auth = createGoogleAuth(minter, {
      read: async () => { throw new Error('upstash unreachable'); },
      write: async () => {},
      clear: async () => {},
    });

    expect(await auth.getGoogleAccessToken()).toBe('minted-after-read-failure');
    expect(state.calls).toBe(1);
  });

  test('a cache write failure still returns the token to the caller', async () => {
    const { state, minter } = countingMinter('minted-but-uncached');
    const auth = createGoogleAuth(minter, {
      read: async () => null,
      write: async () => { throw new Error('upstash write rejected'); },
      clear: async () => {},
    });

    expect(await auth.getGoogleAccessToken()).toBe('minted-but-uncached');
    // Nothing was cached, so the next caller mints again rather than reusing a
    // token that never landed.
    expect(await auth.getGoogleAccessToken()).toBe('minted-but-uncached');
    expect(state.calls).toBe(2);
  });
});

describe('createGoogleAuth mint failures', () => {
  test('a rejected authorize propagates and does not wedge the next caller', async () => {
    // The in-flight promise must be cleared on failure. If it is not, every
    // later request awaits the same permanently-rejected promise and the Chirp
    // chain stays down until the machine restarts.
    let calls = 0;
    const auth = createGoogleAuth(
      {
        authorize: async () => {
          calls += 1;
          if (calls === 1) throw new Error('invalid_grant');
          return { access_token: 'token-after-retry', expiry_date: null };
        },
      },
      alwaysMissCache(),
    );

    await expect(auth.getGoogleAccessToken()).rejects.toThrow('invalid_grant');
    expect(await auth.getGoogleAccessToken()).toBe('token-after-retry');
    expect(calls).toBe(2);
  });

  test('a non-Error rejection is normalised to an Error', async () => {
    const auth = createGoogleAuth(
      { authorize: async () => { throw 'gaxios string failure'; } },
      alwaysMissCache(),
    );

    const error = await auth.getGoogleAccessToken().catch((e: unknown) => e);

    expect(error).toBeInstanceOf(Error);
    expect((error as Error).message).toBe('gaxios string failure');
  });

  test('a response with no access_token is a failure, not an empty token', async () => {
    // authorize() resolving with `{}` used to hand `undefined` down the chain
    // and surface as an unauthenticated Google call rather than a clear error.
    let calls = 0;
    const auth = createGoogleAuth(
      {
        authorize: async () => {
          calls += 1;
          if (calls === 1) return { access_token: null, expiry_date: null };
          return { access_token: 'token-after-retry', expiry_date: null };
        },
      },
      alwaysMissCache(),
    );

    await expect(auth.getGoogleAccessToken()).rejects.toThrow(
      'Google service account did not return an access_token',
    );
    // Same wedge risk as a rejected authorize: the retry must reach the minter.
    expect(await auth.getGoogleAccessToken()).toBe('token-after-retry');
    expect(calls).toBe(2);
  });
});

describe('invalidateGoogleAccessToken', () => {
  test('clears the shared cache entry', async () => {
    const cache = recordingCache('stale-token');
    const { minter } = countingMinter('token-after-invalidate');
    const auth = createGoogleAuth(minter, cache);

    expect(await auth.getGoogleAccessToken()).toBe('stale-token');

    await auth.invalidateGoogleAccessToken();

    expect(cache.clears).toBe(1);
    // The cache now misses, so the next caller re-mints instead of getting the
    // stale token back — this is the 401-mid-poll recovery path.
    expect(await auth.getGoogleAccessToken()).toBe('token-after-invalidate');
  });

  test('drops an in-flight mint so the next caller does not receive it', async () => {
    let releaseMint: (credentials: { access_token: string; expiry_date: null }) => void = () => {};
    const pendingMint = new Promise<{ access_token: string; expiry_date: null }>((resolve) => {
      releaseMint = resolve;
    });
    let calls = 0;

    const auth = createGoogleAuth(
      {
        authorize: () => {
          calls += 1;
          if (calls === 1) return pendingMint;
          return Promise.resolve({ access_token: 'second-token', expiry_date: null });
        },
      },
      alwaysMissCache(),
    );

    const first = auth.getGoogleAccessToken();
    await auth.invalidateGoogleAccessToken();

    const second = auth.getGoogleAccessToken();
    releaseMint({ access_token: 'first-token', expiry_date: null });

    expect(await first).toBe('first-token');
    expect(await second).toBe('second-token');
    expect(calls).toBe(2);
  });

  test('a cache clear failure is swallowed and still drops the in-flight mint', async () => {
    let calls = 0;
    const auth = createGoogleAuth(
      {
        authorize: async () => {
          calls += 1;
          return { access_token: `token-${calls}`, expiry_date: null };
        },
      },
      {
        read: async () => null,
        write: async () => {},
        clear: async () => { throw new Error('upstash del rejected'); },
      },
    );

    expect(await auth.getGoogleAccessToken()).toBe('token-1');

    // Must resolve, not reject — the caller is already handling a 401.
    await auth.invalidateGoogleAccessToken();

    expect(await auth.getGoogleAccessToken()).toBe('token-2');
    expect(calls).toBe(2);
  });
});
