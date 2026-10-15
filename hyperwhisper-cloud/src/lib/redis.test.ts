// lib/redis.ts — the wiring, end to end
//
// `redis-core.test.ts` and `redis-client.test.ts` cover the logic and the
// getter with the client passed in. What they cannot see is this module's own
// job: that each of the three exports hands the CLOUD getter and the caller's
// argument to the right core function. A swapped key or a SITE getter here
// passes every other suite.
//
// Ten suites `mock.module('../lib/redis')`, and bun's registry is process-wide,
// so a plain `import('./redis')` can return another suite's stub. A query
// suffix makes bun load the file as a separate module that no `mock.module`
// matches, so this suite always runs the real code. The only fake is the
// network: a local server that speaks the Upstash REST protocol.

import { afterAll, beforeAll, beforeEach, describe, expect, test } from 'bun:test';
import type * as RedisModule from './redis';

const CLOUD_TOKEN = 'cloud-token-fixture';
const ENV_KEYS = ['UPSTASH_REDIS_CLOUD_URL', 'UPSTASH_REDIS_CLOUD_TOKEN'] as const;

type Command = [string, string, string?, ...unknown[]];

// The fake database, and every command and auth header the server saw.
const store = new Map<string, string>();
const commands: Command[] = [];
const authHeaders: (string | null)[] = [];

/** Upstash answers in base64 when the client asks for it, as this one does. */
function encode(value: string | null, base64: boolean): string | null {
  if (value === null || !base64) return value;
  return Buffer.from(value).toString('base64');
}

let server: ReturnType<typeof Bun.serve>;
let redis: typeof RedisModule;
const savedEnv: Record<string, string | undefined> = {};

beforeAll(async () => {
  server = Bun.serve({
    port: 0,
    async fetch(req) {
      authHeaders.push(req.headers.get('authorization'));
      const base64 = req.headers.get('upstash-encoding') === 'base64';
      const run = (cmd: Command) => {
        commands.push(cmd);
        const [name, key, value] = [cmd[0].toLowerCase(), cmd[1], cmd[2]];
        if (name === 'get') return { result: encode(store.get(key) ?? null, base64) };
        if (name === 'set') {
          store.set(key, String(value));
          return { result: encode('OK', base64) };
        }
        return { error: `unsupported command ${name}` };
      };
      // The client batches concurrent commands to /pipeline as an array of
      // commands; a lone command goes to / as one array.
      const body = await req.json();
      if (new URL(req.url).pathname === '/pipeline') {
        return Response.json((body as Command[]).map(run));
      }
      return Response.json(run(body as Command));
    },
  });

  for (const k of ENV_KEYS) {
    savedEnv[k] = process.env[k];
    delete process.env[k];
  }

  const specifier = './redis.ts?unmocked';
  redis = (await import(specifier)) as typeof RedisModule;
});

afterAll(() => {
  server.stop(true);
  for (const k of ENV_KEYS) {
    if (savedEnv[k] === undefined) delete process.env[k];
    else process.env[k] = savedEnv[k];
  }
});

beforeEach(() => {
  store.clear();
  commands.length = 0;
  authHeaders.length = 0;
});

describe('lib/redis with no Upstash env', () => {
  // Runs first: the getter memoises the client on its first SUCCESSFUL build,
  // and a build without env throws, so nothing is memoised yet.
  test('every export fails open and sends nothing', async () => {
    expect(await redis.isIPBlocked('203.0.113.7')).toBe(false);
    expect(await redis.getCachedLicense('cache-key-fixture')).toBeNull();
    await redis.cacheLicense('cache-key-fixture', {
      isValid: true,
      credits: 5,
      cachedAt: '2026-10-12T00:00:00.000Z',
    });
    expect(() => redis.redis.get()).toThrow('UPSTASH_REDIS_CLOUD_URL');
    expect(commands).toEqual([]);
  });
});

describe('lib/redis against an Upstash REST server', () => {
  beforeAll(() => {
    process.env.UPSTASH_REDIS_CLOUD_URL = `http://127.0.0.1:${server.port}`;
    process.env.UPSTASH_REDIS_CLOUD_TOKEN = CLOUD_TOKEN;
  });

  test('isIPBlocked reads ip_blocked:<ip> with the CLOUD token', async () => {
    // Stored with literal quotes, so the client's JSON parse gives the string.
    store.set('ip_blocked:203.0.113.7', JSON.stringify('true'));

    expect(await redis.isIPBlocked('203.0.113.7')).toBe(true);
    expect(await redis.isIPBlocked('198.51.100.9')).toBe(false);

    expect(commands).toEqual([
      ['get', 'ip_blocked:203.0.113.7'],
      ['get', 'ip_blocked:198.51.100.9'],
    ]);
    expect(authHeaders).toEqual([`Bearer ${CLOUD_TOKEN}`, `Bearer ${CLOUD_TOKEN}`]);
  });

  test('isIPBlocked blocks an entry stored as bare true', async () => {
    // `SET ip_blocked:<ip> true` from redis-cli, the Upstash console, or this
    // client's own `set(key, 'true')` (a string goes out raw) stores bare
    // `true`. The client JSON-parses it on read into the boolean `true`.
    store.set('ip_blocked:203.0.113.7', 'true');

    expect(await redis.isIPBlocked('203.0.113.7')).toBe(true);
  });

  test('cacheLicense writes license:<key> for one hour, and getCachedLicense reads it back', async () => {
    const license = { isValid: true, credits: 42, cachedAt: '2026-10-12T01:02:03.000Z' };

    await redis.cacheLicense('cache-key-a', license);

    expect(commands).toHaveLength(1);
    const [name, key, value, ex, ttl] = commands[0];
    expect([name, key, ex, ttl]).toEqual(['set', 'license:cache-key-a', 'ex', 3600]);
    expect(JSON.parse(String(value))).toEqual(license);

    expect(await redis.getCachedLicense('cache-key-a')).toEqual(license);
    // A different key is a miss, not the entry above.
    expect(await redis.getCachedLicense('cache-key-b')).toBeNull();
    expect(commands.slice(1)).toEqual([
      ['get', 'license:cache-key-a'],
      ['get', 'license:cache-key-b'],
    ]);
  });

  test('an invalid licence is cached and read back as invalid, not as a miss', async () => {
    const license = { isValid: false, credits: 0, cachedAt: '2026-10-12T01:02:03.000Z' };

    await redis.cacheLicense('cache-key-revoked', license);

    expect(await redis.getCachedLicense('cache-key-revoked')).toEqual(license);
  });

  test('redis.get returns one memoised client for every call', () => {
    expect(redis.redis.get()).toBe(redis.redis.get());
  });
});
