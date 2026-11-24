/**
 * #2010: Better Auth's rate limiter counts in Cloudflare Rate Limiting
 * bindings, not in each isolate's memory.
 *
 * - The REAL `auth` config has `rateLimit.enabled` and the Cloudflare
 *   `customStorage`, and NO `secondaryStorage` (which would also move sessions
 *   off Postgres — see `src/lib/auth-rate-limit-storage.ts`).
 * - Driven through the REAL `/api/auth/[...all]` route, the 6th license-key
 *   sign-in from one IP in a minute gets 429 (the plugin's own 5 / 60 s rule),
 *   and a SECOND Better Auth instance built from the same options draws on the
 *   same counter — what two isolates do.
 * - No binding (local dev, a broken deploy) lets the request through.
 *
 * The binding is a stand-in counter installed by mocking
 * `lib/clients/cloudflare-binding.ts`; the storage's own fail-open cases are
 * in rate-limit.test.ts. Every sign-in below sends an empty body, so Better
 * Auth answers 400 at validation and no case reaches the database. The other
 * mocked modules are `lib/clients/email.ts`, as in
 * auth-catch-all-route.test.ts, and `src/env/server.mjs` (#682).
 */
import assert from "node:assert/strict";
import { before, beforeEach, describe, test } from "node:test";

import { captureConsole, moduleMock, moduleUrl } from "./module-mock";

import type { auth as AuthInstance } from "@/src/lib/auth";
import type * as StorageModule from "@/src/lib/auth-rate-limit-storage";

moduleMock.module(moduleUrl("../lib/clients/email.ts"), {
  namedExports: {
    sendEmail: async () => ({ data: { id: "unused" }, error: null }),
    DEFAULT_FROM_EMAIL: "HyperWhisper <support@hyperwhisper.com>",
  },
});

/** Per binding name, per key: how many calls it has counted. */
const counts = new Map<string, number>();
/** Each binding's limit, as wrangler.jsonc sets it. Unset: no binding at all. */
const bindingLimits = new Map<string, number>();

moduleMock.module(moduleUrl("../lib/clients/cloudflare-binding.ts"), {
  namedExports: {
    cloudflareBinding: (name: string) => {
      const limit = bindingLimits.get(name);
      if (limit === undefined) return undefined;
      return {
        limit: async ({ key }: { key: string }) => {
          const id = `${name}:${key}`;
          const count = (counts.get(id) ?? 0) + 1;
          counts.set(id, count);
          return { success: count <= limit };
        },
      };
    },
  },
});

const fakeEnv: Record<string, string | undefined> = {};

moduleMock.module(moduleUrl("../src/env/server.mjs"), {
  namedExports: { env: fakeEnv },
});

const ORIGIN = "http://localhost:3000";
const BASE = `${ORIGIN}/api/auth`;

type Handler = (request: Request) => Promise<Response>;

let POST: Handler;
let auth: typeof AuthInstance;
let storageModule: typeof StorageModule;

// Variable specifiers, loaded after the mocks and the env below: `auth.ts`
// builds `betterAuth()` at import time.
const ROUTE_PATH = "../app/api/auth/[...all]/route.ts";
const AUTH_PATH = "../src/lib/auth.ts";
const STORAGE_PATH = "../src/lib/auth-rate-limit-storage.ts";

before(async () => {
  fakeEnv.BETTER_AUTH_URL = ORIGIN;

  POST = (await import(ROUTE_PATH)).POST;
  auth = (await import(AUTH_PATH)).auth;
  storageModule = await import(STORAGE_PATH);
});

beforeEach(() => {
  counts.clear();
  bindingLimits.clear();
  bindingLimits.set("RATE_LIMIT_AUTH_60S_5", 5);
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

describe("Better Auth sign-in limits count in the Cloudflare binding", () => {
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

    // Counted in the plugin rule's own binding, under Better Auth's key.
    assert.equal(counts.get(`RATE_LIMIT_AUTH_60S_5:${ip}|/sign-in/license-key`), 6);

    // Another IP is unaffected.
    assert.equal((await POST(licenseSignIn("203.0.113.51"))).status, 400);
  });

  test("a second Better Auth instance with the same options shares the budget", async () => {
    const { betterAuth } = await import("better-auth");
    const { licenseKeyPlugin } = await import("@/src/lib/auth-license-key-plugin");
    // What a second isolate is: its own storage object, the same binding.
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

  test("no binding: every sign-in is served, with a warning", async () => {
    bindingLimits.clear();
    const ip = "203.0.113.70";
    const capture = captureConsole(["warn"]);
    const statuses: number[] = [];
    try {
      for (let i = 0; i < 7; i += 1) statuses.push((await POST(licenseSignIn(ip))).status);
    } finally {
      capture.restore();
    }

    assert.deepEqual(statuses, [400, 400, 400, 400, 400, 400, 400]);
    assert.ok(
      capture.lines.some((line) => /Rate limit unavailable for Better Auth, failing open: RateLimitBindingMissingError/.test(line)),
      capture.lines.join("\n"),
    );
    assert.ok(capture.lines.every((line) => !line.includes(ip)), "no IP in the log line");
  });
});
