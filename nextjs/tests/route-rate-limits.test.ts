/**
 * #2010: the per-IP limits on the site's REST routes that a caller can hit
 * without a session.
 *
 * - `GET /api/license/credits` answers "is this key real, and what is it
 *   worth" to anyone, so it gets validate's per-IP ceiling on its own budget
 *   (`licenseCreditsRateLimiter`). The POST (the deduction) is NOT limited
 *   here: it is cloud-only, gated on `x-internal-secret` (#2021).
 * - `POST /api/checkout/credits` creates a Stripe Checkout Session per call,
 *   and `GET /api/checkout` redirects into it; both use `checkoutRateLimiter`.
 *
 * Over the limit: 429, a `Retry-After` header, and no work done (no database
 * read, no Stripe call). Upstash unreachable: the request is served (fail
 * open) and a warning is logged.
 *
 * The routes are the real modules; the limiters are in-memory stand-ins
 * installed by `credit-routes-harness.ts`, counting per identifier so the
 * N+1th request is the first refused. The Upstash sliding window itself is
 * covered by tests/rate-limit.test.ts.
 */
import assert from "node:assert/strict";
import { after, before, beforeEach, describe, test } from "node:test";

import {
  accountKeyRow,
  behaviour,
  calls,
  getRequest,
  loadAccountCreditsRoute,
  loadCheckoutCreditsRoute,
  loadCheckoutRedirectRoute,
  loadLicenseCreditsRoute,
  logLines,
  postRequest,
  resetHarness,
  restoreRouteLogging,
  silenceRouteLogging,
  storeRow,
} from "./credit-routes-harness";

/** A made-up key string. Only the fake database in the harness knows it. */
const GRANTED_KEY = "HW-TEST-0000-0001";
const CALLER_IP = "203.0.113.7";
const OTHER_IP = "198.51.100.9";
const LIMIT = 3;

function fromIp(ip: string): Record<string, string> {
  return { "x-forwarded-for": ip };
}

before(() => {
  silenceRouteLogging();
});

after(() => {
  restoreRouteLogging();
});

beforeEach(() => {
  resetHarness();
  logLines.length = 0;
  storeRow(accountKeyRow({ key: GRANTED_KEY }));
});

interface LimitedRoute {
  name: string;
  limiter: "licenseCreditsRateLimiter" | "checkoutRateLimiter";
  /** Status a request under the limit gets. */
  okStatus: number;
  send(ip: string): Promise<Response>;
  /** How many units of real work the route has done so far. */
  work(): number;
}

const ROUTES: LimitedRoute[] = [
  {
    name: "GET /api/license/credits",
    limiter: "licenseCreditsRateLimiter",
    okStatus: 200,
    async send(ip) {
      const { GET } = await loadLicenseCreditsRoute();
      return GET(
        getRequest(`/api/license/credits?license_key=${GRANTED_KEY}`, fromIp(ip)),
      );
    },
    work: () => calls.findAccountByKey.length,
  },
  {
    name: "GET /api/account/credits (alias)",
    limiter: "licenseCreditsRateLimiter",
    okStatus: 200,
    async send(ip) {
      const { GET } = await loadAccountCreditsRoute();
      return GET(
        getRequest(`/api/account/credits?license_key=${GRANTED_KEY}`, fromIp(ip)),
      );
    },
    work: () => calls.findAccountByKey.length,
  },
  {
    name: "POST /api/checkout/credits",
    limiter: "checkoutRateLimiter",
    okStatus: 200,
    async send(ip) {
      const { POST } = await loadCheckoutCreditsRoute();
      return POST(postRequest("/api/checkout/credits", { amount: 25 }, fromIp(ip)));
    },
    work: () => calls.sessionCreate.length,
  },
  {
    name: "GET /api/checkout",
    limiter: "checkoutRateLimiter",
    okStatus: 307,
    async send(ip) {
      const { GET } = await loadCheckoutRedirectRoute();
      return GET(getRequest("/api/checkout?license_key=HW-TEST-0000-0001", fromIp(ip)));
    },
    // The redirect does no work beyond answering; count the answers instead.
    work: () => 0,
  },
];

for (const route of ROUTES) {
  describe(`${route.name} — rate limit`, () => {
    test(`request ${LIMIT + 1} from one IP gets 429 with Retry-After, and no work is done`, async () => {
      behaviour.rateLimitMax = LIMIT;
      behaviour.rateLimitResetInMs = 41_500;

      for (let i = 0; i < LIMIT; i += 1) {
        const ok = await route.send(CALLER_IP);
        assert.equal(ok.status, route.okStatus, `request ${i + 1}`);
      }
      const workBefore = route.work();

      const refused = await route.send(CALLER_IP);

      assert.equal(refused.status, 429);
      assert.equal(refused.headers.get("Retry-After"), "42");
      assert.deepEqual(await refused.json(), {
        error: "Too many requests. Please try again later.",
      });
      assert.equal(route.work(), workBefore, "a refused request must do no work");

      // Keyed by the caller's IP, on the limiter this route is meant to use.
      assert.deepEqual(
        new Set(calls.rateLimit.map((call) => `${call.limiter}|${call.identifier}`)),
        new Set([`${route.limiter}|${CALLER_IP}`]),
      );

      // Another IP has its own budget.
      const other = await route.send(OTHER_IP);
      assert.equal(other.status, route.okStatus);
    });

    test("fails open: Upstash throwing still serves the request, with a warning", async () => {
      behaviour.rateLimitMax = 0;
      behaviour.rateLimitError = new Error(`upstash down, saw ${CALLER_IP}`);

      const response = await route.send(CALLER_IP);

      assert.equal(response.status, route.okStatus);
      const warnings = logLines.filter((line) => line.startsWith("warn "));
      assert.equal(warnings.length, 1, logLines.join("\n"));
      assert.match(warnings[0], /Rate limit unavailable for .*failing open: Error/);
      // The error message (which can quote the command) is not logged.
      assert.ok(!warnings[0].includes(CALLER_IP), warnings[0]);
    });

    test("fails open on @upstash/ratelimit's timeout verdict, with a warning", async () => {
      behaviour.rateLimitTimeout = true;

      const response = await route.send(CALLER_IP);

      assert.equal(response.status, route.okStatus);
      assert.ok(
        logLines.some((line) => /warn Rate limit timed out for .*failing open/.test(line)),
        logLines.join("\n"),
      );
    });
  });
}

describe("GET /api/license/credits and its alias are one route", () => {
  test("credits GET and the account alias draw on one per-IP allowance", async () => {
    behaviour.rateLimitMax = 2;
    const { GET } = await loadLicenseCreditsRoute();
    const alias = await loadAccountCreditsRoute();

    const first = await GET(
      getRequest(`/api/license/credits?license_key=${GRANTED_KEY}`, fromIp(CALLER_IP)),
    );
    const second = await alias.GET(
      getRequest(`/api/account/credits?license_key=${GRANTED_KEY}`, fromIp(CALLER_IP)),
    );
    const third = await GET(
      getRequest(`/api/license/credits?license_key=${GRANTED_KEY}`, fromIp(CALLER_IP)),
    );

    assert.deepEqual([first.status, second.status, third.status], [200, 200, 429]);
  });
});

describe("POST /api/license/credits is not rate limited here (#2021)", () => {
  test("the deduction never consults a limiter, even with the budget at 0", async () => {
    // The deduction is cloud-only (x-internal-secret), so send it the way the
    // cloud does; the point here is that no limiter is asked.
    const realSecret = process.env.HYPERWHISPER_INTERNAL_SECRET;
    process.env.HYPERWHISPER_INTERNAL_SECRET = "test-internal-secret";
    behaviour.rateLimitMax = 0;
    try {
      const { POST } = await loadLicenseCreditsRoute();

      const response = await POST(
        postRequest(
          "/api/license/credits",
          { license_key: GRANTED_KEY, amount: 1 },
          { ...fromIp(CALLER_IP), "x-internal-secret": "test-internal-secret" },
        ),
      );

      assert.equal(response.status, 200);
      assert.deepEqual(calls.rateLimit, []);
    } finally {
      if (realSecret === undefined) delete process.env.HYPERWHISPER_INTERNAL_SECRET;
      else process.env.HYPERWHISPER_INTERNAL_SECRET = realSecret;
    }
  });
});
