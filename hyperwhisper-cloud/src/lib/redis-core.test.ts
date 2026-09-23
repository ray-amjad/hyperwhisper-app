// UPSTASH REDIS CORE — IP BLOCK LOOKUP AND LICENSE CACHE
//
// None of this was reachable from a test before `redis-core` took a store
// factory. The three functions each called a module-level `getRedis()` that
// built a real `@upstash/redis` client out of `UPSTASH_REDIS_CLOUD_URL` /
// `UPSTASH_REDIS_CLOUD_TOKEN`, memoised it for the life of the process, and talked to
// Upstash over the network. So `lib/redis.ts` had no test file at all, and the
// ten suites that touch it all replace it with `mock.module` — which asserts
// the STUB, never this code.
//
// This suite mocks nothing. It passes a fake store instead, which is also why
// it is safe from bun's process-wide module registry: nothing replaces
// `./redis-core`, so a plain import always resolves to the real thing whatever
// order bun walks the tree in.

import { describe, expect, spyOn, test } from 'bun:test';
import { LICENSE_CACHE_TTL_SECONDS } from './constants';
import {
  cacheLicense,
  getCachedLicense,
  isIPBlocked,
  type CachedLicense,
  type RedisStore,
  type RedisStoreFactory,
} from './redis-core';

interface RecordingStore extends RedisStore {
  /** Every key read, in order. */
  readonly gets: string[];
  /** Every write, with the TTL option it carried. */
  readonly sets: Array<{ key: string; value: unknown; opts: { ex: number } }>;
}

/** A store that answers every read with `stored` and records every call. */
function recordingStore(stored: unknown = null): RecordingStore {
  const gets: string[] = [];
  const sets: Array<{ key: string; value: unknown; opts: { ex: number } }> = [];
  return {
    gets,
    sets,
    async get<TData = unknown>(key: string): Promise<TData | null> {
      gets.push(key);
      return stored as TData | null;
    },
    async set(key: string, value: unknown, opts: { ex: number }): Promise<unknown> {
      sets.push({ key, value, opts });
      return 'OK';
    },
  };
}

/**
 * The shape of the real factory when the Upstash env vars are missing: it
 * throws before any command runs. Every function is meant to fail open on it.
 */
const unconfiguredStore: RedisStoreFactory = () => {
  throw new Error('UPSTASH_REDIS_CLOUD_URL and UPSTASH_REDIS_CLOUD_TOKEN are required');
};

/** A store that connects but fails the command itself. */
function failingStore(): RedisStore {
  return {
    async get(): Promise<never> {
      throw new Error('upstash read failed');
    },
    async set(): Promise<never> {
      throw new Error('upstash write failed');
    },
  };
}

/** A store whose read throws exactly `thrown`, whatever shape that is. */
function throwingStore(thrown: unknown): RedisStore {
  return {
    async get(): Promise<never> {
      throw thrown;
    },
    async set(): Promise<never> {
      throw thrown;
    },
  };
}

/**
 * The error `@upstash/redis` really throws on an HTTP failure. It builds the
 * message as `${body.error}, command was: ${JSON.stringify(req.body)}`, and
 * `req.body` is the command — so the ip_blocked: key, and therefore the client
 * IP, is INSIDE the message. Reproduced here because that is the only thing
 * that can carry the IP into the log, and `lib/redis.ts` wires the real client
 * straight into `core.isIPBlocked`.
 */
function upstashError(message: string): Error {
  const error = new Error(message);
  error.name = 'UpstashError';
  return error;
}

const validLicense: CachedLicense = { isValid: true, credits: 1000, cachedAt: '2026-09-01T00:00:00Z' };

describe('isIPBlocked', () => {
  test('reads the ip_blocked: key for the address it was given', async () => {
    const store = recordingStore('true');

    expect(await isIPBlocked(() => store, '203.0.113.7')).toBe(true);
    expect(store.gets).toEqual(['ip_blocked:203.0.113.7']);
  });

  test('blocks only on the exact string "true"', async () => {
    // The block flag is compared as a string, so every other truthy value a
    // stale or hand-written entry could hold must NOT block a paying user.
    expect(await isIPBlocked(() => recordingStore(true), '203.0.113.7')).toBe(false);
    expect(await isIPBlocked(() => recordingStore(1), '203.0.113.7')).toBe(false);
    expect(await isIPBlocked(() => recordingStore('TRUE'), '203.0.113.7')).toBe(false);
    expect(await isIPBlocked(() => recordingStore('false'), '203.0.113.7')).toBe(false);
  });

  test('fails open when no address is stored', async () => {
    expect(await isIPBlocked(() => recordingStore(null), '203.0.113.7')).toBe(false);
  });

  test('fails open when Redis is not configured, rather than throwing', async () => {
    expect(await isIPBlocked(unconfiguredStore, '203.0.113.7')).toBe(false);
  });

  test('fails open when the read itself fails', async () => {
    expect(await isIPBlocked(() => failingStore(), '203.0.113.7')).toBe(false);
  });

  test('RECORDS each fail-open on console.error, without the IP address', async () => {
    // The fail-open is deliberate, so the return value is not the thing at
    // risk — the silence was. Every failure shape has to leave a line, or a
    // disabled abuse gate reads as an hour with no blocked IPs. The IP must
    // NOT appear: the client IP is a privacy finding on this service (#714),
    // and the operation name plus the redacted error is enough to spot the
    // outage.
    //
    // The spy is created and restored inside this test, rather than in an
    // afterEach, so its whole lifetime sits in one block and only the tests
    // that install one pay for it. An afterEach would have been SAFE for the
    // getCachedLicense / cacheLicense suites below, contrary to what this
    // comment used to say: bun's lifecycle hooks are describe-scoped, so a
    // hook declared here never runs for a sibling describe. Measured on bun
    // 1.4.2, not assumed.
    const spy = spyOn(console, 'error').mockImplementation(() => {});

    try {
      expect(await isIPBlocked(unconfiguredStore, '203.0.113.7')).toBe(false);
      expect(spy).toHaveBeenCalledTimes(1);

      expect(await isIPBlocked(() => failingStore(), '203.0.113.7')).toBe(false);
      expect(spy).toHaveBeenCalledTimes(2);

      // The real leak shape: an HTTP failure from @upstash/redis carries the
      // command — and so the ip_blocked: key — inside its message.
      const httpFailure = upstashError(
        'WRONGPASS invalid password, command was: ["get","ip_blocked:203.0.113.7"]'
      );
      expect(await isIPBlocked(() => throwingStore(httpFailure), '203.0.113.7')).toBe(false);
      expect(spy).toHaveBeenCalledTimes(3);

      for (const call of spy.mock.calls) {
        // Pin the ARITY. `console.error(MSG, redacted, { ip })` would satisfy
        // every assertion below while putting the address back on the line.
        expect(call).toHaveLength(2);
        expect(call[0]).toBe('IP block check failed — failing open:');
        // A string, not the Error: a raw Error prints a multi-line stack that
        // a line-oriented shipper splits into several records.
        expect(typeof call[1]).toBe('string');
        // Asserted on the argument console.error ACTUALLY received. Never
        // `JSON.stringify(call)`: an Error's message, stack and cause are all
        // non-enumerable, so `JSON.stringify(['x', new Error(ip)])` is
        // `["x",{}]` and that assertion passes over a live leak.
        expect(call[1]).not.toContain('203.0.113.7');
      }

      // And the exact line for the leak case, so the redaction is pinned to a
      // readable value rather than only to the absence of the address.
      expect(spy.mock.calls[2]).toEqual([
        'IP block check failed — failing open:',
        'UpstashError: WRONGPASS invalid password, command was: ["get","ip_blocked:<redacted>"]',
      ]);
    } finally {
      spy.mockRestore();
    }
  });

  test('redacts the client IP from the error however the message carries it', async () => {
    // Redaction is by VALUE — the `ip` argument is the string the key was
    // built from — so it does not depend on Upstash keeping the
    // `, command was:` wording, and it still covers an error that names the
    // address without the key at all.
    const cases: Array<{ thrown: unknown; logged: string }> = [
      {
        thrown: upstashError(
          'max requests limit exceeded, command was: ["get","ip_blocked:203.0.113.7"]'
        ),
        logged:
          'UpstashError: max requests limit exceeded, command was: ["get","ip_blocked:<redacted>"]',
      },
      // No ip_blocked: key in this one: a shape-only redaction would leak it.
      {
        thrown: new Error('connect ETIMEDOUT 203.0.113.7:443'),
        logged: 'Error: connect ETIMEDOUT <redacted ip>:443',
      },
      // Not every throw is an Error.
      {
        thrown: 'raw string failure for 203.0.113.7',
        logged: 'raw string failure for <redacted ip>',
      },
    ];

    const spy = spyOn(console, 'error').mockImplementation(() => {});

    try {
      for (const { thrown, logged } of cases) {
        spy.mockClear();

        expect(await isIPBlocked(() => throwingStore(thrown), '203.0.113.7')).toBe(false);

        expect(spy.mock.calls).toEqual([['IP block check failed — failing open:', logged]]);
      }
    } finally {
      spy.mockRestore();
    }
  });

  test('stays silent on the paths that are not a failure', async () => {
    // A hit and a miss are the normal case. Logging them would bury the
    // fail-open line the test above pins, on every single request.
    const spy = spyOn(console, 'error').mockImplementation(() => {});

    try {
      expect(await isIPBlocked(() => recordingStore('true'), '203.0.113.7')).toBe(true);
      expect(await isIPBlocked(() => recordingStore(null), '203.0.113.7')).toBe(false);
      expect(spy).not.toHaveBeenCalled();
    } finally {
      spy.mockRestore();
    }
  });
});

describe('getCachedLicense', () => {
  test('reads the license: key and returns a well-formed entry unchanged', async () => {
    const store = recordingStore(validLicense);

    expect(await getCachedLicense(() => store, 'KEY-123')).toEqual(validLicense);
    expect(store.gets).toEqual(['license:KEY-123']);
  });

  test('parses an entry stored as a JSON string', async () => {
    const store = recordingStore(JSON.stringify(validLicense));

    expect(await getCachedLicense(() => store, 'KEY-123')).toEqual(validLicense);
  });

  test('treats an entry missing isValid as a MISS, not as an invalid license', async () => {
    // This is the paying-user lockout guard. An entry written by an older
    // schema comes back with `isValid` undefined; middleware/auth.ts reads that
    // as "license invalid" and locks the account out for the full 1 hour TTL.
    // `null` sends the next request to the license API instead.
    const olderSchema = { credits: 1000, cachedAt: '2026-09-01T00:00:00Z' };

    expect(await getCachedLicense(() => recordingStore(olderSchema), 'KEY-123')).toBeNull();
  });

  test('treats every other unrecognised shape as a MISS', async () => {
    const cases: unknown[] = [
      { isValid: 'true', credits: 1000, cachedAt: 'x' }, // isValid not a boolean
      { isValid: true, credits: '1000', cachedAt: 'x' }, // credits not a number
      { isValid: true, credits: 1000 }, // cachedAt absent
      'not json at all',
      42,
      [],
    ];

    for (const stored of cases) {
      expect(await getCachedLicense(() => recordingStore(stored), 'KEY-123')).toBeNull();
    }
  });

  test('returns a MISS for an empty entry', async () => {
    expect(await getCachedLicense(() => recordingStore(null), 'KEY-123')).toBeNull();
  });

  test('returns a MISS when Redis is not configured, rather than throwing', async () => {
    expect(await getCachedLicense(unconfiguredStore, 'KEY-123')).toBeNull();
  });

  test('returns a MISS when the read itself fails', async () => {
    expect(await getCachedLicense(() => failingStore(), 'KEY-123')).toBeNull();
  });
});

describe('cacheLicense', () => {
  test('writes the license under license:<key> with the 1 hour TTL', async () => {
    const store = recordingStore();

    await cacheLicense(() => store, 'KEY-123', validLicense);

    expect(store.sets).toEqual([
      { key: 'license:KEY-123', value: validLicense, opts: { ex: LICENSE_CACHE_TTL_SECONDS } },
    ]);
    expect(LICENSE_CACHE_TTL_SECONDS).toBe(3600);
  });

  test('caches an INVALID license too, so a bad key is not revalidated every request', async () => {
    const store = recordingStore();
    const invalid: CachedLicense = { isValid: false, credits: 0, cachedAt: '2026-09-01T00:00:00Z' };

    await cacheLicense(() => store, 'BAD-KEY', invalid);

    expect(store.sets[0]?.value).toEqual(invalid);
    expect(store.sets[0]?.opts).toEqual({ ex: LICENSE_CACHE_TTL_SECONDS });
  });

  test('swallows a failure when Redis is not configured, rather than throwing', async () => {
    expect(await cacheLicense(unconfiguredStore, 'KEY-123', validLicense)).toBeUndefined();
  });

  test('swallows a failure when the write itself fails', async () => {
    expect(await cacheLicense(() => failingStore(), 'KEY-123', validLicense)).toBeUndefined();
  });
});

describe('the round trip a request actually makes', () => {
  test('a license written by cacheLicense reads back through getCachedLicense', async () => {
    // auth.ts writes on a license-API hit and reads on the next request. Before
    // the seam, proving those two agree needed a live Upstash instance.
    let stored: unknown = null;
    const store: RedisStore = {
      async get<TData = unknown>(): Promise<TData | null> {
        return stored as TData | null;
      },
      async set(_key: string, value: unknown): Promise<unknown> {
        stored = value;
        return 'OK';
      },
    };

    expect(await getCachedLicense(() => store, 'KEY-123')).toBeNull();
    await cacheLicense(() => store, 'KEY-123', validLicense);
    expect(await getCachedLicense(() => store, 'KEY-123')).toEqual(validLicense);
  });
});
