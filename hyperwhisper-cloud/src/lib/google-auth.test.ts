// GOOGLE OAUTH PRODUCTION EDGES
//
// lib/google-auth-core.ts is where the accessor logic is tested, with both I/O
// edges injected. This file covers the two edges themselves, which that suite
// deliberately cannot reach: the lazy `GOOGLE_SERVICE_ACCOUNT_JSON`-backed JWT
// client, and the Upstash cache wiring (key name, TTL argument, delete).
//
// Two mechanics make that possible without touching the network:
//
// 1. `google-auth-library` and `./redis` are replaced with mock.module. Both
//    factories spread the real module and override only what this file needs —
//    bun's module registry is process-wide, so a factory that LISTS exports
//    deletes the rest for every file that loads after this one. `redis.get()`
//    and `JWT` have exactly one importer each (lib/google-auth.ts), so the
//    override surface is that module and nothing else.
//
// 2. lib/google-auth is imported through a query-suffixed specifier. That is
//    NOT a cache-busting trick for this file's own benefit — it is how this
//    suite gets the REAL module. providers/google-chirp.test.ts replaces
//    `../lib/google-auth` process-wide, and if it loads first a plain import
//    here resolves to its stub, whose `getGoogleAccessToken` never reaches the
//    code under test. A distinct specifier is a distinct registry key, so this
//    file always exercises the real thing whatever order bun walks the tree in.
//    That order is filesystem-dependent, so a plain import passing locally
//    would prove nothing — see the note in providers/google-chirp.test.ts,
//    which records an earlier `lib/google-auth.test.ts` breaking exactly there.
//
// One consequence to expect in `bun test src --coverage`: the query-suffixed
// instance is a separate module, so lib/google-auth.ts still reports 0% funcs.
// The coverage number is the artefact; the code under test really does run.

import { afterAll, beforeEach, describe, expect, mock, test } from 'bun:test';
import * as realGoogleAuthLibrary from 'google-auth-library';
import * as realRedis from './redis';

// ---------------------------------------------------------------------------
// A fake JWT client. It records the credentials it was constructed with, so a
// test can assert what getJwtClient() parsed out of the env var, and how many
// times it was built, which is what the module's memoisation is.
// ---------------------------------------------------------------------------
interface JwtOptions {
  email?: string;
  key?: string;
  scopes?: string[];
}

const jwtConstructions: JwtOptions[] = [];
let authorizeResult: { access_token?: string | null; expiry_date?: number | null } = {
  access_token: 'access-token-from-jwt',
  expiry_date: null,
};

class FakeJWT {
  constructor(options: JwtOptions) {
    jwtConstructions.push(options);
  }

  async authorize() {
    return authorizeResult;
  }
}

mock.module('google-auth-library', () => ({
  ...realGoogleAuthLibrary,
  JWT: FakeJWT,
}));

// ---------------------------------------------------------------------------
// A fake Upstash client with one slot, recording every command it is given.
// ---------------------------------------------------------------------------
type RedisCommand =
  | { op: 'get'; key: string }
  | { op: 'set'; key: string; value: string; options: { ex?: number } }
  | { op: 'del'; key: string };

const redisCommands: RedisCommand[] = [];
let redisStore: string | null = null;
let redisReadError: Error | null = null;

const fakeRedisClient = {
  get: async <T,>(key: string): Promise<T | null> => {
    redisCommands.push({ op: 'get', key });
    if (redisReadError) throw redisReadError;
    return redisStore as T | null;
  },
  set: async (key: string, value: string, options: { ex?: number }) => {
    redisCommands.push({ op: 'set', key, value, options });
    redisStore = value;
    return 'OK';
  },
  del: async (key: string) => {
    redisCommands.push({ op: 'del', key });
    redisStore = null;
    return 1;
  },
};

mock.module('./redis', () => ({
  ...realRedis,
  redis: { get: () => fakeRedisClient },
}));

// ---------------------------------------------------------------------------
// See note 2 in the header — the query suffix is how this file reaches the
// real module. It is held in a variable so `tsc` treats the dynamic import as
// unresolved-but-legal rather than failing on a literal it cannot map to a
// file; the type comes from the plain specifier, which is type-only.
// ---------------------------------------------------------------------------
const REAL_GOOGLE_AUTH = './google-auth.ts?production-edges';
type GoogleAuthModule = typeof import('./google-auth');

const googleAuth = (await import(REAL_GOOGLE_AUTH)) as GoogleAuthModule;

// A syntactically well-formed service account that is not, and never was, a
// credential: the key body is a literal placeholder and the domain is the
// reserved `.invalid` TLD. This repo is public.
const PLACEHOLDER_PRIVATE_KEY =
  '-----BEGIN PRIVATE KEY-----\nNOT-A-REAL-KEY-THIS-IS-A-TEST-PLACEHOLDER\n-----END PRIVATE KEY-----\n';
const FAKE_SERVICE_ACCOUNT = JSON.stringify({
  type: 'service_account',
  client_email: 'stt-test@example.invalid',
  private_key: PLACEHOLDER_PRIVATE_KEY,
});

const originalServiceAccountJson = process.env.GOOGLE_SERVICE_ACCOUNT_JSON;

afterAll(() => {
  if (originalServiceAccountJson === undefined) {
    delete process.env.GOOGLE_SERVICE_ACCOUNT_JSON;
  } else {
    process.env.GOOGLE_SERVICE_ACCOUNT_JSON = originalServiceAccountJson;
  }
});

beforeEach(() => {
  redisCommands.length = 0;
  redisStore = null;
  redisReadError = null;
  authorizeResult = { access_token: 'access-token-from-jwt', expiry_date: null };
});

// The JWT client is memoised in a module-level variable for the life of the
// process, so the rejection cases have to run before the one that succeeds:
// after a successful construction the env var is never read again. That is the
// production behaviour — a Fly machine parses the credential once — so these
// two describes are ordered, not independent.
describe('getJwtClient credential parsing', () => {
  test('a blank GOOGLE_SERVICE_ACCOUNT_JSON fails with a named error', async () => {
    // A staged-but-not-deployed Fly secret arrives as a blank string, and an
    // absent one as undefined. `if (!raw)` is one branch for both; the blank
    // string is the case a test can set deterministically.
    process.env.GOOGLE_SERVICE_ACCOUNT_JSON = '';

    await expect(googleAuth.getGoogleAccessToken()).rejects.toThrow(
      'GOOGLE_SERVICE_ACCOUNT_JSON not configured',
    );
    expect(jwtConstructions).toHaveLength(0);
  });

  test('a malformed credential names the parse failure, not a generic one', async () => {
    process.env.GOOGLE_SERVICE_ACCOUNT_JSON = '{ not json';

    await expect(googleAuth.getGoogleAccessToken()).rejects.toThrow(
      /GOOGLE_SERVICE_ACCOUNT_JSON is not valid JSON/,
    );
    expect(jwtConstructions).toHaveLength(0);
  });

  test('a credential missing client_email is rejected before the JWT is built', async () => {
    // Losing a field to a bad Infisical sync must fail here, with a message
    // that says which env var is wrong, rather than at Google as invalid_grant.
    process.env.GOOGLE_SERVICE_ACCOUNT_JSON = JSON.stringify({
      private_key: PLACEHOLDER_PRIVATE_KEY,
    });

    await expect(googleAuth.getGoogleAccessToken()).rejects.toThrow(
      'GOOGLE_SERVICE_ACCOUNT_JSON is missing client_email or private_key',
    );
    expect(jwtConstructions).toHaveLength(0);
  });

  test('a credential missing private_key is rejected before the JWT is built', async () => {
    process.env.GOOGLE_SERVICE_ACCOUNT_JSON = JSON.stringify({
      client_email: 'stt-test@example.invalid',
    });

    await expect(googleAuth.getGoogleAccessToken()).rejects.toThrow(
      'GOOGLE_SERVICE_ACCOUNT_JSON is missing client_email or private_key',
    );
    expect(jwtConstructions).toHaveLength(0);
  });

  // REGRESSION. This is the test that found the wedge the `async` on
  // `jwtTokenMinter.authorize` exists to prevent. `getJwtClient()` throws
  // synchronously on all four faults above; before the fix that ran the core's
  // `inflight = null` clear before `inflight = mintAndCacheToken()` published
  // the rejected promise, so this call came back with the PREVIOUS test's
  // error message even though the credential is now valid — and would have,
  // for the life of the machine, after Infisical synced a correct secret.
  test('a machine recovers once the credential is fixed — no permanent wedge', async () => {
    process.env.GOOGLE_SERVICE_ACCOUNT_JSON = FAKE_SERVICE_ACCOUNT;

    expect(await googleAuth.getGoogleAccessToken()).toBe('access-token-from-jwt');
    expect(jwtConstructions).toHaveLength(1);
  });
});

describe('the JWT client built from the credential', () => {
  test('carries the parsed email, key and the cloud-platform scope', () => {
    // Asserted on the construction recorded by the test above. The scope is
    // load-bearing: the same token is used by Chirp AND the GCS scratch bucket.
    expect(jwtConstructions).toHaveLength(1);
    expect(jwtConstructions[0]).toEqual({
      email: 'stt-test@example.invalid',
      key: PLACEHOLDER_PRIVATE_KEY,
      scopes: ['https://www.googleapis.com/auth/cloud-platform'],
    });
  });

  test('is built once and reused across cold-cache mints', async () => {
    process.env.GOOGLE_SERVICE_ACCOUNT_JSON = FAKE_SERVICE_ACCOUNT;
    const constructionsBefore = jwtConstructions.length;

    // Two separate cold-cache calls: the store is cleared in beforeEach, and
    // nothing repopulates it between these two awaits.
    await googleAuth.getGoogleAccessToken();
    redisStore = null;
    await googleAuth.getGoogleAccessToken();

    expect(jwtConstructions).toHaveLength(constructionsBefore);
  });
});

describe('the Upstash cache edge', () => {
  test('reads the shared google_oauth_token key and returns a hit unchanged', async () => {
    redisStore = 'token-already-in-upstash';

    expect(await googleAuth.getGoogleAccessToken()).toBe('token-already-in-upstash');
    expect(redisCommands).toEqual([{ op: 'get', key: 'google_oauth_token' }]);
  });

  test('writes the minted token under the same key with the expiry-derived TTL', async () => {
    authorizeResult = { access_token: 'minted-token', expiry_date: Date.now() + 7_200_000 };

    expect(await googleAuth.getGoogleAccessToken()).toBe('minted-token');

    const write = redisCommands.find((command) => command.op === 'set');
    expect(write).toBeDefined();
    expect(write!.key).toBe('google_oauth_token');
    expect((write as { value: string }).value).toBe('minted-token');
    // 2 h expiry minus the 10 min safety margin, allowing for clock drift.
    const ttl = (write as { options: { ex?: number } }).options.ex;
    expect(ttl).toBeGreaterThanOrEqual(6598);
    expect(ttl).toBeLessThanOrEqual(6600);
  });

  test('falls back to a 50 minute TTL when Google returns no expiry', async () => {
    authorizeResult = { access_token: 'minted-token', expiry_date: null };

    await googleAuth.getGoogleAccessToken();

    const write = redisCommands.find((command) => command.op === 'set');
    expect((write as { options: { ex?: number } }).options.ex).toBe(3000);
  });

  test('an Upstash read failure still yields a token from the JWT client', async () => {
    redisReadError = new Error('upstash unreachable');
    authorizeResult = { access_token: 'minted-despite-cache-outage', expiry_date: null };

    expect(await googleAuth.getGoogleAccessToken()).toBe('minted-despite-cache-outage');
  });

  test('invalidate deletes the shared key so the next caller re-mints', async () => {
    redisStore = 'stale-token';
    expect(await googleAuth.getGoogleAccessToken()).toBe('stale-token');

    await googleAuth.invalidateGoogleAccessToken();

    expect(redisCommands).toContainEqual({ op: 'del', key: 'google_oauth_token' });
    expect(redisStore).toBeNull();

    authorizeResult = { access_token: 'token-after-invalidate', expiry_date: null };
    expect(await googleAuth.getGoogleAccessToken()).toBe('token-after-invalidate');
  });
});
