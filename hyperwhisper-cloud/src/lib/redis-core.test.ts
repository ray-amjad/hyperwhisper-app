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

import { afterEach, beforeEach, describe, expect, spyOn, test } from 'bun:test';
// The REAL error classes, not a hand-rolled stand-in. A fixture that assigns
// `error.name = 'UpstashError'` only ever confirms the fixture's own field, so
// it cannot notice the library renaming or restructuring what it throws. This
// is safe from bun's process-wide module registry for the same reason the note
// at the top of the file gives: nothing anywhere in `src/` does
// `mock.module('@upstash/redis')`, so this import always resolves to the real
// package.
import { errors } from '@upstash/redis';
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
 * The message `@upstash/redis` really throws on a non-ok HTTP response. It
 * builds it as `${body.error}, command was: ${JSON.stringify(req.body)}`, and
 * `req.body` is the whole REQUEST — which under auto-pipelining (on by default;
 * `lib/redis.ts` does not override it) is every command issued in the same
 * tick, not only ours. So a single failure carries this caller's `ip_blocked:`
 * key, a concurrent caller's, the `license:` key that is the bearer credential
 * for every request, and a cached licence VALUE. That is the payload the log
 * line must not ship.
 */
function autoPipelinedFailure(upstreamError: string, ...commands: unknown[]): errors.UpstashError {
  return new errors.UpstashError(`${upstreamError}, command was: ${JSON.stringify(commands)}`);
}

type ConsoleErrorSpy = ReturnType<typeof spyOn<Console, 'error'>>;

/**
 * Installs a silenced console.error spy before each test of the enclosing
 * describe and restores it after, and returns a getter for the current spy.
 * Call it from a describe body. Describe-SCOPED, measured on bun 1.4.2: a hook
 * declared in one describe never runs for its siblings. The fail-open tests
 * reach each catch, and an unsuppressed failure line in the suite output reads
 * as a genuine failure sitting next to a green result — so the line is
 * asserted, not printed.
 */
function silenceConsoleError(): () => ConsoleErrorSpy {
  let spy: ConsoleErrorSpy | undefined;
  beforeEach(() => {
    spy = spyOn(console, 'error').mockImplementation(() => {});
  });
  afterEach(() => {
    spy?.mockRestore();
  });
  return () => {
    if (spy === undefined) throw new Error('console.error spy read outside a test');
    return spy;
  };
}

/**
 * The single string argument `console.error` received on call `index`, after
 * the message `prefix`. Every catch in redis-core keeps its own prefix, which
 * existing Axiom queries match on, so the prefix is pinned exactly.
 */
function readLoggedLine(spy: ConsoleErrorSpy, prefix: string, index = 0): string {
  const call = spy.mock.calls[index];
  // Pin the ARITY here, once. `console.error(MSG, redacted, { ip })` would
  // satisfy every assertion in every test below while putting the address
  // back on the line.
  expect(call).toHaveLength(2);
  expect(call?.[0]).toBe(prefix);
  // A string, not the Error: a raw Error prints a multi-line stack that a
  // line-oriented shipper splits into several records. And never assert on
  // `JSON.stringify(call)`: an Error's message is non-enumerable, so that
  // passes over a live leak.
  expect(typeof call?.[1]).toBe('string');
  return call?.[1] as string;
}

/**
 * The licence key and cached value the tests below must keep off the log. The
 * key has the real shape — `HW-XXXX-XXXX-XXXX-XXXX` over the 31-character
 * alphabet `nextjs/lib/services/license-key.ts` draws from — because the key
 * IS the bearer credential for every request (middleware/auth.ts).
 */
const LIVE_KEY = 'HW-7F3A-9C2B-K4MN-PQRS';
const liveLicense: CachedLicense = { isValid: true, credits: 4200, cachedAt: '2026-09-01T00:00:00Z' };

const validLicense: CachedLicense = { isValid: true, credits: 1000, cachedAt: '2026-09-01T00:00:00Z' };

describe('isIPBlocked', () => {
  const consoleError = silenceConsoleError();
  const PREFIX = 'IP block check failed — failing open:';

  /** The redacted string `console.error` received on call `index`. */
  function loggedLine(index = 0): string {
    return readLoggedLine(consoleError(), PREFIX, index);
  }

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
    // and the operation name plus the bounded error is enough to spot the
    // outage.
    expect(await isIPBlocked(unconfiguredStore, '203.0.113.7')).toBe(false);
    expect(consoleError()).toHaveBeenCalledTimes(1);

    expect(await isIPBlocked(() => failingStore(), '203.0.113.7')).toBe(false);
    expect(consoleError()).toHaveBeenCalledTimes(2);

    // The real leak shape: a non-ok HTTP response from @upstash/redis carries
    // the request body — and so the ip_blocked: key — inside its message.
    const httpFailure = autoPipelinedFailure('WRONGPASS invalid password', [
      'get',
      'ip_blocked:203.0.113.7',
    ]);
    expect(await isIPBlocked(() => throwingStore(httpFailure), '203.0.113.7')).toBe(false);
    expect(consoleError()).toHaveBeenCalledTimes(3);

    for (let i = 0; i < 3; i++) {
      // Asserted on the argument console.error ACTUALLY received. Never
      // `JSON.stringify(call)`: an Error's message, stack and cause are all
      // non-enumerable, so `JSON.stringify(['x', new Error(ip)])` is
      // `["x",{}]` and that assertion passes over a live leak.
      expect(loggedLine(i)).not.toContain('203.0.113.7');
    }

    // And the exact line for the leak case, so the result is pinned to a
    // readable value rather than only to the absence of the address.
    expect(loggedLine(2)).toBe(
      'UpstashError: WRONGPASS invalid password, command was: <redacted>'
    );
  });

  test('drops the WHOLE co-batched command payload, not just this caller keys', async () => {
    // `enableAutoPipelining` is on by default and `lib/redis.ts` does not turn
    // it off, so one failed HTTP request carries every command issued in the
    // same tick. A deny-list that redacts `ip_blocked:` and this request's own
    // address leaves a concurrent getCachedLicense / cacheLicense in the clear
    // — and the licence key is the BEARER CREDENTIAL for every request
    // (middleware/auth.ts). getCachedLicense and cacheLicense route the same
    // failure through the same helper (#921), and their describes below pin
    // that; this test pins it for isIPBlocked.
    const coBatched = autoPipelinedFailure(
      'WRONGPASS invalid password',
      ['get', 'ip_blocked:203.0.113.7'],
      ['get', `license:${LIVE_KEY}`],
      ['set', `license:${LIVE_KEY}`, { isValid: true, credits: 4200 }]
    );
    // Guard the FIXTURE: if the library ever stops putting all of this in one
    // message these assertions would pass vacuously.
    expect(coBatched).toBeInstanceOf(errors.UpstashError);
    expect(coBatched.message).toContain(`license:${LIVE_KEY}`);
    expect(coBatched.message).toContain('4200');

    expect(await isIPBlocked(() => throwingStore(coBatched), '203.0.113.7')).toBe(false);

    const logged = loggedLine();
    expect(logged).toBe('UpstashError: WRONGPASS invalid password, command was: <redacted>');
    for (const secret of ['203.0.113.7', LIVE_KEY, 'credits', '4200']) {
      expect(logged).not.toContain(secret);
    }

    consoleError().mockClear();

    // And the licence key named OUTSIDE any command payload — the shape the
    // auto-pipeline executor re-throws per command, which has no
    // `, command was:` suffix for the cut to find. Only the key pass reaches it.
    const perCommand = new errors.UpstashError(
      `Command failed: WRONGTYPE key license:${LIVE_KEY} holds the wrong kind of value`
    );
    expect(await isIPBlocked(() => throwingStore(perCommand), '203.0.113.7')).toBe(false);
    expect(loggedLine()).toBe(
      'UpstashError: Command failed: WRONGTYPE key license:<redacted> holds the wrong kind of value'
    );
    expect(loggedLine()).not.toContain(LIVE_KEY);
  });

  test("redacts ANOTHER caller's IP, which the ip argument cannot reach", async () => {
    // Auto-pipelining co-batches a second request's ip_blocked: read into the
    // same body. `198.51.100.9` is not the `ip` we were handed, so the
    // by-VALUE pass is blind to it and only the key pass and the payload cut
    // stand between it and the log.
    const other = '198.51.100.9';

    // (a) inside the command payload — the cut removes it.
    const coBatched = autoPipelinedFailure(
      'ERR max daily request limit exceeded',
      ['get', 'ip_blocked:203.0.113.7'],
      ['get', `ip_blocked:${other}`]
    );
    expect(await isIPBlocked(() => throwingStore(coBatched), '203.0.113.7')).toBe(false);
    expect(loggedLine()).toBe(
      'UpstashError: ERR max daily request limit exceeded, command was: <redacted>'
    );
    expect(loggedLine()).not.toContain(other);

    consoleError().mockClear();

    // (b) named OUTSIDE any command payload — the shape of the per-command
    // error the auto-pipeline executor re-throws (`Command failed: ...`), which
    // has no `, command was:` suffix at all. Only the key pass covers this.
    const perCommand = new errors.UpstashError(
      `Command failed: ERR key ip_blocked:${other} is read-only in replica mode`
    );
    expect(await isIPBlocked(() => throwingStore(perCommand), '203.0.113.7')).toBe(false);
    expect(loggedLine()).toBe(
      'UpstashError: Command failed: ERR key ip_blocked:<redacted> is read-only in replica mode'
    );
    expect(loggedLine()).not.toContain(other);
  });

  test('flattens a multi-line upstream body into ONE record', async () => {
    // UpstashJSONParseError is built from `res.text()` verbatim on the !res.ok
    // branch, so an intermediary's HTML 502 goes in with real newlines. A
    // line-oriented shipper splits that into ~8 unrelated records — on exactly
    // the outage class this log was added to surface.
    const html = [
      '<html>',
      '<head><title>502 Bad Gateway</title></head>',
      '<body>',
      '<center><h1>502 Bad Gateway</h1></center>',
      '<hr><center>nginx/1.25.3</center>',
      '<!-- client 203.0.113.7 upstream eu-central-1.upstash.io -->',
      '</body>',
      '</html>',
    ].join('\r\n');
    const parseFailure = new errors.UpstashJSONParseError(html);
    // Guard the FIXTURE: the newlines must really be in the message.
    expect(parseFailure).toBeInstanceOf(errors.UpstashError);
    expect(parseFailure.message).toContain('\r\n');

    expect(await isIPBlocked(() => throwingStore(parseFailure), '203.0.113.7')).toBe(false);

    const logged = loggedLine();
    expect(logged).not.toContain('\n');
    expect(logged).not.toContain('\r');
    expect(logged.split('\n')).toHaveLength(1);
    expect(logged).not.toContain('203.0.113.7');
    expect(logged).toStartWith(
      'UpstashJSONParseError: Unable to parse response body: <html> <head><title>502 Bad Gateway'
    );
    // And BOUNDED, so 200 characters of someone else's HTML cannot be the
    // whole record. 200 + '<truncated>'.length.
    expect(logged).toEndWith('<truncated>');
    expect(logged.length).toBe(211);
  });

  test("does not corrupt English when the address is the 'unknown' sentinel", async () => {
    // `getClientIP` returns the literal 'unknown' for an off-edge 6PN peer and
    // for a request with neither Fly-Client-IP nor X-Forwarded-For — both
    // pinned as production shapes in request-id.test.ts. Substituting that as a
    // literal would log `ERR <redacted-ip> command 'GET'` and put a redaction
    // marker exactly where an operator reads the fault code.
    const wrongCommand = autoPipelinedFailure("ERR unknown command 'GET'", [
      'get',
      'ip_blocked:unknown',
    ]);
    expect(await isIPBlocked(() => throwingStore(wrongCommand), 'unknown')).toBe(false);
    expect(loggedLine()).toBe(
      "UpstashError: ERR unknown command 'GET', command was: <redacted>"
    );

    consoleError().mockClear();

    // The key pass still covers `ip_blocked:unknown` outside a command payload,
    // so skipping the value pass costs nothing.
    const perCommand = new errors.UpstashError(
      'Command failed: ERR unknown key ip_blocked:unknown in an unknown shard'
    );
    expect(await isIPBlocked(() => throwingStore(perCommand), 'unknown')).toBe(false);
    expect(loggedLine()).toBe(
      'UpstashError: Command failed: ERR unknown key ip_blocked:<redacted> in an unknown shard'
    );
  });

  test('redacts the client IP from the error however the message carries it', async () => {
    // The by-VALUE pass is the only one that reaches an address the message
    // carries with no key and no command suffix, so it stays even though the
    // payload cut covers the Upstash shapes.
    const cases: Array<{ thrown: unknown; logged: string }> = [
      {
        thrown: autoPipelinedFailure('max requests limit exceeded', [
          'get',
          'ip_blocked:203.0.113.7',
        ]),
        logged: 'UpstashError: max requests limit exceeded, command was: <redacted>',
      },
      // No ip_blocked: key and no command suffix: both shape passes are blind.
      // MEASURED on bun 1.4.2 against the real client: a host that does not
      // resolve is the one socket-level failure whose message really does
      // carry the address. (`connect ETIMEDOUT <addr>:443` does NOT occur on
      // this runtime — bun's fetch says "Unable to connect. Is the computer
      // able to access the url?" and keeps the address off the message.)
      {
        thrown: new TypeError('getaddrinfo ENOTFOUND 203.0.113.7.invalid'),
        logged: 'TypeError: getaddrinfo ENOTFOUND <redacted-ip>.invalid',
      },
      // A synthetic shape, kept because the pass must not depend on where in
      // the sentence the address sits.
      {
        thrown: new Error('connect ETIMEDOUT 203.0.113.7:443'),
        logged: 'Error: connect ETIMEDOUT <redacted-ip>:443',
      },
      // Not every throw is an Error.
      {
        thrown: 'raw string failure for 203.0.113.7',
        logged: 'raw string failure for <redacted-ip>',
      },
    ];

    for (const { thrown, logged } of cases) {
      consoleError().mockClear();

      expect(await isIPBlocked(() => throwingStore(thrown), '203.0.113.7')).toBe(false);

      expect(consoleError().mock.calls).toEqual([
        ['IP block check failed — failing open:', logged],
      ]);
    }
  });

  test('strips the password from a redis:// URL the client quotes back, and keeps the host', async () => {
    // Upstash hands an operator TWO connection strings for one database: a
    // REST URL, which carries no secret, and
    // `redis://default:<PASSWORD>@host:6379`, which carries the password
    // inline. Pasting the wrong one into UPSTASH_REDIS_CLOUD_URL is an
    // ordinary ops mistake, and the REAL client then throws UrlError from its
    // constructor — inside getRedis(), inside this catch — with the whole URL
    // quoted back. MEASURED against @upstash/redis v1.36.1 in a real child
    // process: without this pass the password reaches stderr verbatim.
    //
    // Every other pass is blind to it: no `, command was:` suffix, no Redis
    // key, not the client IP, and the line is UNDER the 200-char cap.
    const password = 'AX9sASQgNmI4ZTk1YTctSUPERSECRET';
    const host = 'eu2-lucky-crab-12345.upstash.io';
    const urlError = new errors.UrlError(`redis://default:${password}@${host}:6379`);
    // Guard the FIXTURE: if the library ever stops quoting the URL back, the
    // assertions below would pass vacuously.
    expect(urlError).toBeInstanceOf(errors.UrlError);
    expect(urlError.message).toContain(password);

    // Thrown from the FACTORY, which is where the real client constructor runs.
    expect(
      await isIPBlocked(() => {
        throw urlError;
      }, '203.0.113.7')
    ).toBe(false);

    const logged = loggedLine();
    expect(logged).not.toContain(password);
    expect(logged).not.toContain('SUPERSECRET');
    // The host SURVIVES — an operator has to be able to see which url is
    // wrong, or the line cannot be acted on.
    expect(logged).toContain(host);
    expect(logged).toBe(
      'UrlError: Upstash Redis client was passed an invalid URL. You should pass a URL starting with https. Received: "redis://<redacted-credentials>@eu2-lucky-crab-12345.upstash.io:6379".'
    );
    // And prove the CAP is not what saved it: the line is under the bound, and
    // the password sat at the front of the URL where truncation never reaches.
    expect(logged).not.toEndWith('<truncated>');
    expect(logged.length).toBeLessThan(200);
  });

  test('does not corrupt an ordinary message that merely contains an @', async () => {
    // The credential pass is a regex over free text, so its false POSITIVES
    // matter as much as its negatives: a marker written over a support address
    // or a module path destroys the only diagnostic the operator has. The
    // character class stops at the characters RFC 3986 says end an authority,
    // which is what keeps all three of these whole.
    const untouched = [
      // An email address: no `scheme://` in front of it at all.
      'quota exceeded — contact support@hyperwhisper.com to raise it',
      // A scoped npm package inside a file:// URL. The `/` in the class is the
      // only thing standing between this and
      // `file://<redacted-credentials>@upstash/redis/nodejs.mjs`.
      'Cannot find module imported from file:///app/node_modules/@upstash/redis/nodejs.mjs',
      // An `@` inside a query-string value, after a perfectly ordinary https URL.
      'request to https://console.upstash.com/redis?owner=ops@example.com failed',
    ];

    for (const message of untouched) {
      consoleError().mockClear();

      expect(await isIPBlocked(() => throwingStore(new Error(message)), '203.0.113.7')).toBe(false);

      expect(loggedLine()).toBe(`Error: ${message}`);
      expect(loggedLine()).not.toContain('<redacted-credentials>');
    }
  });

  test('still fails open, and still logs a string, when the thrown value resists String()', async () => {
    // The helper runs inside the catch, so a throw from IT would turn the
    // fail-open into a 500 — the exact outcome #898 exists to prevent.
    const hostile: Array<{ thrown: unknown; logged: string }> = [
      // `String(aSymbol)` does NOT throw, contrary to round 1's note — only a
      // template literal does. So the symbol keeps its readable form here, and
      // this case is what stops someone "fixing" `String(x)` into `` `${x}` ``.
      // (The trailing `)` falls inside the key regex and is redacted with the
      // address — redacting MORE, never less.)
      { thrown: Symbol('ip_blocked:203.0.113.7'), logged: 'Symbol(ip_blocked:<redacted>' },
      // `String()` on a null-prototype object throws "Cannot convert object to
      // primitive value".
      { thrown: Object.assign(Object.create(null), { nope: true }), logged: '<unloggable failure>' },
      // An Error whose `message` read throws, so `${error.message}` throws
      // INSIDE the instanceof branch rather than at the String() fallback.
      {
        thrown: Object.defineProperty(new Error('boom'), 'message', {
          get(): string {
            throw new Error('nope');
          },
        }),
        logged: '<unloggable failure>',
      },
    ];

    for (const { thrown, logged } of hostile) {
      consoleError().mockClear();

      expect(await isIPBlocked(() => throwingStore(thrown), '203.0.113.7')).toBe(false);

      expect(loggedLine()).toBe(logged);
      expect(loggedLine()).not.toContain('203.0.113.7');
    }
  });

  test('stays silent on the paths that are not a failure', async () => {
    // A hit and a miss are the normal case. Logging them would bury the
    // fail-open line the tests above pin, on every single request.
    expect(await isIPBlocked(() => recordingStore('true'), '203.0.113.7')).toBe(true);
    expect(await isIPBlocked(() => recordingStore(null), '203.0.113.7')).toBe(false);
    expect(consoleError()).not.toHaveBeenCalled();
  });
});

describe('getCachedLicense', () => {
  const consoleError = silenceConsoleError();
  const PREFIX = 'Failed to get cached license:';

  /** The redacted string `console.error` received on call `index`. */
  function loggedLine(index = 0): string {
    return readLoggedLine(consoleError(), PREFIX, index);
  }

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
      expect(await getCachedLicense(() => recordingStore(stored), LIVE_KEY)).toBeNull();
    }

    // Five of these are a silent MISS. The non-JSON string is not: the catch
    // also wraps `JSON.parse`, so it logs once, through the same redaction,
    // under the same prefix. Measured on bun 1.4.2: the parser quotes the first
    // bad token of the STORED VALUE (never the key) back in its message.
    expect(consoleError()).toHaveBeenCalledTimes(1);
    const logged = loggedLine();
    expect(logged).toBe('SyntaxError: JSON Parse error: Unexpected identifier "not"');
    expect(logged).not.toContain(LIVE_KEY);
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

  test('logs an Upstash failure WITHOUT the licence key, and still returns a MISS', async () => {
    // #921. The real @upstash/redis message embeds the command it sent, and for
    // this read that is the bearer credential, once per authenticated request.
    // The un-pipelined body is ONE command, verbatim the shape #921 names.
    const wrongPass = new errors.UpstashError(
      `WRONGPASS invalid password, command was: ["get","license:${LIVE_KEY}"]`
    );
    // Guard the FIXTURE: the key really is in the message.
    expect(wrongPass.message).toContain(`license:${LIVE_KEY}`);

    expect(await getCachedLicense(() => throwingStore(wrongPass), LIVE_KEY)).toBeNull();

    expect(consoleError()).toHaveBeenCalledTimes(1);
    const logged = loggedLine();
    expect(logged).toBe('UpstashError: WRONGPASS invalid password, command was: <redacted>');
    expect(logged).not.toContain(LIVE_KEY);
  });

  test("drops a co-batched write's credit balance and another caller's IP too", async () => {
    // Auto-pipelining puts every command of the same tick into one failed body:
    // another request's ip_blocked: read and a cacheLicense write with the
    // cached licence value beside this read.
    const coBatched = autoPipelinedFailure(
      'ERR max daily request limit exceeded',
      ['get', 'ip_blocked:198.51.100.9'],
      ['get', `license:${LIVE_KEY}`],
      ['set', `license:${LIVE_KEY}`, liveLicense]
    );
    // Guard the FIXTURE: every secret asserted absent below is really present.
    for (const secret of [LIVE_KEY, '4200', 'credits', '198.51.100.9']) {
      expect(coBatched.message).toContain(secret);
    }

    expect(await getCachedLicense(() => throwingStore(coBatched), LIVE_KEY)).toBeNull();

    expect(consoleError()).toHaveBeenCalledTimes(1);
    const logged = loggedLine();
    expect(logged).toBe('UpstashError: ERR max daily request limit exceeded, command was: <redacted>');
    for (const secret of [LIVE_KEY, '4200', 'credits', '198.51.100.9']) {
      expect(logged).not.toContain(secret);
    }
  });

  test('redacts the key when the message names it outside any command payload', async () => {
    // The per-command error the auto-pipeline executor re-throws has no
    // `, command was:` suffix, so only the key pass reaches it.
    const perCommand = new errors.UpstashError(
      `Command failed: WRONGTYPE key license:${LIVE_KEY} holds the wrong kind of value`
    );
    // Guard the FIXTURE: the key really is in the message, and there is no
    // command suffix for the payload cut to find.
    expect(perCommand.message).toContain(LIVE_KEY);
    expect(perCommand.message).not.toContain(', command was:');

    expect(await getCachedLicense(() => throwingStore(perCommand), LIVE_KEY)).toBeNull();

    expect(consoleError()).toHaveBeenCalledTimes(1);
    const logged = loggedLine();
    expect(logged).toBe(
      'UpstashError: Command failed: WRONGTYPE key license:<redacted> holds the wrong kind of value'
    );
    expect(logged).not.toContain(LIVE_KEY);
  });

  test('stays silent on a hit and on an empty entry', async () => {
    // Not on every MISS: a non-JSON entry logs its parse failure (pinned in
    // the unrecognised-shape test above).
    expect(await getCachedLicense(() => recordingStore(validLicense), 'KEY-123')).toEqual(validLicense);
    expect(await getCachedLicense(() => recordingStore(null), 'KEY-123')).toBeNull();
    expect(consoleError()).not.toHaveBeenCalled();
  });
});

describe('cacheLicense', () => {
  const consoleError = silenceConsoleError();
  const PREFIX = 'Failed to cache license:';

  /** The redacted string `console.error` received on call `index`. */
  function loggedLine(index = 0): string {
    return readLoggedLine(consoleError(), PREFIX, index);
  }

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

  test('logs an Upstash failure WITHOUT the licence key or the credit balance, and still swallows it', async () => {
    // #921. On a write the embedded command also carries the cached VALUE.
    const wrongPass = new errors.UpstashError(
      `WRONGPASS invalid password, command was: ${JSON.stringify(['set', `license:${LIVE_KEY}`, liveLicense, 'ex', LICENSE_CACHE_TTL_SECONDS])}`
    );
    // Guard the FIXTURE: the key and the balance really are in the message.
    expect(wrongPass.message).toContain(`["set","license:${LIVE_KEY}",{"isValid":true,"credits":4200,`);

    expect(await cacheLicense(() => throwingStore(wrongPass), LIVE_KEY, liveLicense)).toBeUndefined();

    expect(consoleError()).toHaveBeenCalledTimes(1);
    const logged = loggedLine();
    expect(logged).toBe('UpstashError: WRONGPASS invalid password, command was: <redacted>');
    for (const secret of [LIVE_KEY, '4200', 'credits', 'isValid']) {
      expect(logged).not.toContain(secret);
    }
  });

  test("drops another caller's IP from a co-batched failure", async () => {
    const coBatched = autoPipelinedFailure(
      'ERR max daily request limit exceeded',
      ['get', 'ip_blocked:198.51.100.9'],
      ['set', `license:${LIVE_KEY}`, liveLicense, { ex: LICENSE_CACHE_TTL_SECONDS }]
    );
    // Guard the FIXTURE: every secret asserted absent below is really present.
    for (const secret of [LIVE_KEY, '4200', '198.51.100.9']) {
      expect(coBatched.message).toContain(secret);
    }

    expect(await cacheLicense(() => throwingStore(coBatched), LIVE_KEY, liveLicense)).toBeUndefined();

    expect(consoleError()).toHaveBeenCalledTimes(1);
    const logged = loggedLine();
    expect(logged).toBe('UpstashError: ERR max daily request limit exceeded, command was: <redacted>');
    for (const secret of [LIVE_KEY, '4200', '198.51.100.9']) {
      expect(logged).not.toContain(secret);
    }
  });

  test('stays silent on a successful write', async () => {
    await cacheLicense(() => recordingStore(), 'KEY-123', validLicense);
    expect(consoleError()).not.toHaveBeenCalled();
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
