/**
 * The Cloudflare rate limiters in `lib/rate-limit.ts` and the Better Auth
 * storage in `src/lib/auth-rate-limit-storage.ts`, against their bindings.
 *
 * The license and latency route tests replace `lib/rate-limit.ts` with a stub,
 * so nothing else checks the numbers. They live in `wrangler.jsonc`, and they
 * are the whole abuse budget of the public endpoints: the license
 * validate/activate pair is open to anyone with a key to guess, and the
 * download form sends an email per request. A slipped limit, period or
 * binding name fails silently — the routes still answer.
 *
 * The bindings themselves are Cloudflare's; here a binding is a counter that
 * refuses past its limit, so what is checked is which binding each limiter
 * asks, with which key, and what it does with the answer.
 */
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { describe, mock, test } from "node:test";

import * as limiters from "../lib/rate-limit";
import {
  cloudflareLimiter,
  RateLimitBindingMissingError,
  type RateLimitBinding,
} from "../lib/cloudflare-rate-limit";
import {
  AUTH_RATE_LIMIT_BINDINGS,
  createCloudflareAuthRateLimitStorage,
} from "../src/lib/auth-rate-limit-storage";
import { captureConsole } from "./module-mock";

interface RateLimitConfig {
  name: string;
  namespace_id: string;
  simple: { limit: number; period: number };
}

/** wrangler.jsonc's `ratelimits`, with its full-line `//` comments removed. */
function wranglerRateLimits(): RateLimitConfig[] {
  const raw = readFileSync(new URL("../wrangler.jsonc", import.meta.url), "utf8");
  const json = raw
    .split("\n")
    .filter((line) => !/^\s*\/\//.test(line))
    .join("\n");
  return (JSON.parse(json) as { ratelimits?: RateLimitConfig[] }).ratelimits ?? [];
}

/** A binding that allows `limit` calls per key, recording every key it sees. */
function countingBinding(limit: number) {
  const counts = new Map<string, number>();
  const keys: string[] = [];
  const binding: RateLimitBinding = {
    async limit({ key }) {
      keys.push(key);
      const count = (counts.get(key) ?? 0) + 1;
      counts.set(key, count);
      return { success: count <= limit };
    },
  };
  return { binding, keys };
}

describe("wrangler.jsonc ratelimits", () => {
  const configs = wranglerRateLimits();
  const byName = new Map(configs.map((c) => [c.name, c]));

  const SITE_LIMITS: Array<[string, number]> = [
    ["RATE_LIMIT_DOWNLOAD_EMAIL", 2],
    ["RATE_LIMIT_LICENSE_VALIDATE", 30],
    ["RATE_LIMIT_LICENSE_CREDITS", 300],
    ["RATE_LIMIT_LATENCY_INGEST", 6000],
    ["RATE_LIMIT_CHECKOUT", 10],
    ["RATE_LIMIT_ACCOUNT_KEY_ROTATE", 1],
  ];

  for (const [name, limit] of SITE_LIMITS) {
    test(`${name} allows ${limit} per minute`, () => {
      assert.deepEqual(byName.get(name)?.simple, { limit, period: 60 });
    });
  }

  test("every Better Auth rule has a binding with the rule's own numbers", () => {
    for (const [ruleId, name] of Object.entries(AUTH_RATE_LIMIT_BINDINGS)) {
      const [, window, max] = /^(\d+)s:(\d+)$/.exec(ruleId) ?? [];
      assert.deepEqual(
        byName.get(name)?.simple,
        { limit: Number(max), period: Number(window) },
        `${ruleId} -> ${name}`,
      );
    }
  });

  test("namespace ids are integers, unique, and outside the school's 1001-1003", () => {
    const ids = configs.map((c) => c.namespace_id);
    assert.equal(new Set(ids).size, ids.length, "each limiter counts on its own");
    for (const id of ids) {
      assert.match(id, /^\d+$/);
      assert.ok(Number(id) > 1003, id);
    }
  });

  test("periods are 10 or 60 seconds, the only ones Cloudflare takes", () => {
    for (const c of configs) assert.ok([10, 60].includes(c.simple.period), c.name);
  });
});

describe("cloudflareLimiter", () => {
  test("counts the identifier against its own binding and refuses past the limit", async () => {
    const { binding, keys } = countingBinding(2);
    const asked: string[] = [];
    const limiter = cloudflareLimiter("RATE_LIMIT_X", 60, (name) => {
      asked.push(name);
      return binding;
    });

    assert.equal((await limiter.limit("203.0.113.1")).success, true);
    assert.equal((await limiter.limit("203.0.113.1")).success, true);
    assert.equal((await limiter.limit("203.0.113.1")).success, false);
    assert.equal((await limiter.limit("203.0.113.2")).success, true);
    assert.deepEqual(keys, ["203.0.113.1", "203.0.113.1", "203.0.113.1", "203.0.113.2"]);
    assert.ok(asked.every((name) => name === "RATE_LIMIT_X"));
  });

  test("reset is one period from now", async () => {
    mock.timers.enable({ apis: ["Date"], now: 1_000_000 } as unknown as Parameters<
      typeof mock.timers.enable
    >[0]);
    try {
      const { binding } = countingBinding(1);
      const verdict = await cloudflareLimiter("RATE_LIMIT_X", 10, () => binding).limit("k");
      assert.equal(verdict.reset, 1_000_000 + 10_000);
    } finally {
      mock.timers.reset();
    }
  });

  test("no binding throws RateLimitBindingMissingError (countFailOpen lets it through)", async () => {
    await assert.rejects(
      cloudflareLimiter("RATE_LIMIT_X", 60, () => undefined).limit("k"),
      RateLimitBindingMissingError,
    );
  });

  test("off the Worker the site limiters have no binding", async () => {
    await assert.rejects(
      limiters.licenseValidateRateLimiter.limit("198.51.100.1"),
      RateLimitBindingMissingError,
    );
  });
});

describe("Better Auth storage", () => {
  test("a rule counts in the binding for that rule, keyed by Better Auth's key", async () => {
    const { binding, keys } = countingBinding(5);
    const asked: string[] = [];
    const storage = createCloudflareAuthRateLimitStorage((name) => {
      asked.push(name);
      return binding;
    });
    const rule = { window: 60, max: 5 };
    const key = "203.0.113.9|/sign-in/license-key";

    for (let i = 0; i < 5; i++) {
      assert.deepEqual(await storage.consume(key, rule), { allowed: true, retryAfter: null });
    }
    assert.deepEqual(await storage.consume(key, rule), { allowed: false, retryAfter: 60 });
    assert.ok(asked.every((name) => name === "RATE_LIMIT_AUTH_60S_5"));
    assert.ok(keys.every((k) => k === key));
  });

  test("a rule with no binding is allowed, and said once", async () => {
    const storage = createCloudflareAuthRateLimitStorage(() => {
      throw new Error("must not look up a binding");
    });
    const capture = captureConsole(["warn"]);
    try {
      for (let i = 0; i < 3; i++) {
        assert.deepEqual(await storage.consume("k", { window: 30, max: 7 }), {
          allowed: true,
          retryAfter: null,
        });
      }
    } finally {
      capture.restore();
    }
    assert.equal(capture.lines.length, 1);
    assert.match(capture.lines[0] ?? "", /no binding for rule 30s:7, failing open/);
  });

  test("a missing or throwing binding is allowed, with a warning", async () => {
    for (const getBinding of [
      () => undefined,
      () => ({
        limit: async () => {
          throw new TypeError("binding failed");
        },
      }),
    ]) {
      const storage = createCloudflareAuthRateLimitStorage(getBinding);
      const capture = captureConsole(["warn"]);
      let verdict;
      try {
        verdict = await storage.consume("k", { window: 10, max: 3 });
      } finally {
        capture.restore();
      }
      assert.deepEqual(verdict, { allowed: true, retryAfter: null });
      assert.ok(
        capture.lines.some((line) => /Rate limit unavailable for Better Auth, failing open/.test(line)),
        capture.lines.join("\n"),
      );
    }
  });
});
