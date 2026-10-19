/**
 * #2010: Better Auth's rate limiter counts in the site's shared Upstash, not
 * in each Vercel instance's memory.
 *
 * - The REAL `auth` config has `rateLimit.enabled` and the Upstash
 *   `customStorage`, and NO `secondaryStorage` (which would also move sessions
 *   off Postgres — see `src/lib/auth-rate-limit-storage.ts`).
 * - Driven through the REAL `/api/auth/[...all]` route, the 6th license-key
 *   sign-in from one IP in a minute gets 429 (the plugin's own 5 / 60 s rule),
 *   and a SECOND Better Auth instance built from the same options draws on the
 *   same counter — what two Vercel instances do.
 * - Upstash failing (throwing, hanging, unconfigured) lets the request through
 *   and logs a warning.
 *
 * Upstash is the local REST server in `fake-upstash-server.ts`: the REAL
 * `@upstash/redis` client and the REAL `@upstash/ratelimit` Lua run against
 * `ioredis-mock`. Every sign-in below sends an empty body, so Better Auth
 * answers 400 at validation and no case reaches the database. The one mocked
 * module is `lib/clients/resend.ts`, as in auth-catch-all-route.test.ts.
 */
import assert from "node:assert/strict";
import http from "node:http";
import { after, before, beforeEach, describe, mock, test } from "node:test";

import { Redis } from "@upstash/redis";

import { listen, store, upstashRestServer } from "./fake-upstash-server";

import type { auth as AuthInstance } from "@/src/lib/auth";
import type * as StorageModule from "@/src/lib/auth-rate-limit-storage";

/** See credit-routes-harness.ts: @types/node@20 has no `mock.module`. */
interface ModuleMocker {
  module(
    specifier: string,
    options: { namedExports: Record<string, unknown> },
  ): void;
}

(mock as unknown as ModuleMocker).module(
  new URL("../lib/clients/resend.ts", import.meta.url).href,
  {
    namedExports: {
      resend: { emails: { send: async () => ({ data: null, error: null }) } },
      DEFAULT_FROM_EMAIL: "HyperWhisper <support@hyperwhisper.com>",
    },
  },
);

const ORIGIN = "http://localhost:3000";
const BASE = `${ORIGIN}/api/auth`;

type Handler = (request: Request) => Promise<Response>;

let site: http.Server;
let POST: Handler;
let auth: typeof AuthInstance;
let storageModule: typeof StorageModule;

// Variable specifiers, loaded after the mock and the env below: `auth.ts`
// builds `betterAuth()` at import time.
const ROUTE_PATH = "../app/api/auth/[...all]/route.ts";
const AUTH_PATH = "../src/lib/auth.ts";
const STORAGE_PATH = "../src/lib/auth-rate-limit-storage.ts";

before(async () => {
  site = upstashRestServer();
  process.env.UPSTASH_REDIS_SITE_URL = await listen(site);
  process.env.UPSTASH_REDIS_SITE_TOKEN = "site-token-for-tests";
  process.env.BETTER_AUTH_URL = ORIGIN;

  POST = (await import(ROUTE_PATH)).POST;
  auth = (await import(AUTH_PATH)).auth;
  storageModule = await import(STORAGE_PATH);
});

after(() => {
  site.closeAllConnections();
  site.close();
  store.disconnect();
});

beforeEach(async () => {
  await store.flushall();
});

function licenseSignIn(ip: string): Request {
  return new Request(`${BASE}/sign-in/license-key`, {
    method: "POST",
    headers: {
      "content-type": "application/json",
      origin: ORIGIN,
      "x-forwarded-for": ip,
    },
    body: "{}",
  });
}

/** Swallows console.warn for one call and returns what was warned. */
async function captureWarnings<T>(run: () => Promise<T>): Promise<{ result: T; warnings: string[] }> {
  const warnings: string[] = [];
  const realWarn = console.warn;
  console.warn = (...args: unknown[]) => {
    warnings.push(args.map(String).join(" "));
  };
  try {
    return { result: await run(), warnings };
  } finally {
    console.warn = realWarn;
  }
}

describe("the real auth config", () => {
  test("rate limiting is on, in shared customStorage, with no secondaryStorage", async () => {
    const rateLimit = auth.options.rateLimit;

    assert.equal(rateLimit?.enabled, true);
    assert.equal(typeof rateLimit?.customStorage?.consume, "function");
    // `storage` is ignored when `customStorage` is set; leave it unset so the
    // config does not read as if memory/database were in use.
    assert.equal(rateLimit?.storage, undefined);
    // Sessions stay in Postgres: no secondaryStorage at all.
    assert.equal("secondaryStorage" in auth.options, false);

    const context = await auth.$context;
    assert.equal(context.rateLimit.enabled, true);
  });
});

describe("Better Auth sign-in limits count in Upstash", () => {
  test("the 6th license-key sign-in from one IP in a minute gets 429", async () => {
    const ip = "203.0.113.50";
    for (let i = 0; i < 5; i += 1) {
      const response = await POST(licenseSignIn(ip));
      assert.equal(response.status, 400, `request ${i + 1} reaches validation`);
    }

    const refused = await POST(licenseSignIn(ip));

    assert.equal(refused.status, 429);
    const retryAfter = Number(refused.headers.get("X-Retry-After"));
    assert.ok(retryAfter >= 1 && retryAfter <= 60, `X-Retry-After=${retryAfter}`);

    // The counter lives in Upstash, under the plugin rule's own window.
    const keys = await store.keys("ratelimit:better-auth:60s:5:*");
    assert.ok(
      keys.some((key) => key.includes(`${ip}|/sign-in/license-key`)),
      keys.join("\n"),
    );

    // Another IP is unaffected.
    assert.equal((await POST(licenseSignIn("203.0.113.51"))).status, 400);
  });

  test("a second Better Auth instance with the same options shares the budget", async () => {
    const { betterAuth } = await import("better-auth");
    const { licenseKeyPlugin } = await import("@/src/lib/auth-license-key-plugin");
    // What a second Vercel instance is: its own process, its own storage
    // object, the same Upstash.
    const otherInstance = betterAuth({
      baseURL: ORIGIN,
      rateLimit: storageModule.authRateLimitOptions(),
      plugins: [licenseKeyPlugin()],
    });
    const ip = "203.0.113.60";

    for (let i = 0; i < 3; i += 1) {
      assert.equal((await POST(licenseSignIn(ip))).status, 400);
    }
    for (let i = 0; i < 2; i += 1) {
      assert.equal((await otherInstance.handler(licenseSignIn(ip))).status, 400);
    }

    assert.equal((await POST(licenseSignIn(ip))).status, 429);
    assert.equal((await otherInstance.handler(licenseSignIn(ip))).status, 429);
  });
});

describe("Upstash unavailable fails open", () => {
  const rule = { window: 10, max: 1 };

  test("a throwing client lets every request through and warns", async () => {
    const storage = storageModule.createUpstashAuthRateLimitStorage(async () => {
      throw new Error("ECONNREFUSED 203.0.113.70");
    });

    const { result, warnings } = await captureWarnings(async () => [
      await storage.consume("203.0.113.70|/sign-in/license-key", rule),
      await storage.consume("203.0.113.70|/sign-in/license-key", rule),
    ]);

    assert.deepEqual(result, [
      { allowed: true, retryAfter: null },
      { allowed: true, retryAfter: null },
    ]);
    assert.equal(warnings.length, 2);
    assert.match(warnings[0], /Rate limit unavailable for Better Auth, failing open: Error/);
    assert.ok(!warnings[0].includes("203.0.113.70"), "no IP in the log line");
  });

  test("an Upstash that answers errors lets the request through and warns", async () => {
    const broken = http.createServer((req, res) => {
      req.resume();
      res.statusCode = 500;
      res.end(JSON.stringify({ error: "ERR internal" }));
    });
    const url = await listen(broken);
    try {
      const storage = storageModule.createUpstashAuthRateLimitStorage(
        async () => new Redis({ url, token: "t", retry: false }),
      );

      const { result, warnings } = await captureWarnings(() =>
        storage.consume("203.0.113.71|/sign-in/license-key", rule),
      );

      assert.deepEqual(result, { allowed: true, retryAfter: null });
      assert.equal(warnings.length, 1);
      assert.match(warnings[0], /failing open/);
    } finally {
      broken.closeAllConnections();
      broken.close();
    }
  });

  test("an Upstash that hangs past the timeout lets the request through and warns", async () => {
    const hanging = http.createServer(() => {
      // Never answers.
    });
    const url = await listen(hanging);
    try {
      const storage = storageModule.createUpstashAuthRateLimitStorage(
        async () => new Redis({ url, token: "t", retry: false }),
        50,
      );

      const { result, warnings } = await captureWarnings(() =>
        storage.consume("203.0.113.72|/sign-in/license-key", rule),
      );

      assert.deepEqual(result, { allowed: true, retryAfter: null });
      assert.equal(warnings.length, 1);
      assert.match(warnings[0], /Rate limit timed out for Better Auth, failing open/);
    } finally {
      hanging.closeAllConnections();
      hanging.close();
    }
  });

  test("no Upstash env: allowed, and said once, not per request", async () => {
    const storage = storageModule.createUpstashAuthRateLimitStorage(async () => null);

    const { result, warnings } = await captureWarnings(async () => [
      await storage.consume("a|/sign-in/license-key", rule),
      await storage.consume("a|/sign-in/license-key", rule),
    ]);

    assert.deepEqual(result, [
      { allowed: true, retryAfter: null },
      { allowed: true, retryAfter: null },
    ]);
    assert.equal(warnings.length, 1);
    assert.match(warnings[0], /not configured, failing open/);
  });

  test("siteRedis is null without the site's Upstash env", async () => {
    const saved = process.env.UPSTASH_REDIS_SITE_URL;
    delete process.env.UPSTASH_REDIS_SITE_URL;
    try {
      assert.equal(await storageModule.siteRedis(), null);
    } finally {
      process.env.UPSTASH_REDIS_SITE_URL = saved;
    }
  });
});
