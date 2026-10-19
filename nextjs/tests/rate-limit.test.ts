/**
 * The three Upstash rate limiters in `lib/rate-limit.ts`, run for real.
 *
 * The license and latency route tests replace this module with a stub, so
 * nothing else checks the numbers in it. They are the whole abuse budget of
 * the public endpoints: the license validate/activate pair is open to anyone
 * with a key to guess, and the download form sends an email per request. A
 * slipped limit, window or prefix fails silently — the routes still answer.
 *
 * Nothing here is stubbed inside the site. The REAL `lib/clients/redis.ts`
 * builds the REAL `@upstash/redis` client, and the REAL `@upstash/ratelimit`
 * sends its REAL Lua scripts over the Upstash REST protocol. The far end is a
 * local HTTP server that speaks that protocol and runs each command on
 * `ioredis-mock`, which executes Lua in an embedded VM. So the counting, the
 * blocking and the key layout below are what production Redis would compute.
 */
import assert from "node:assert/strict";
import http from "node:http";
import { after, afterEach, before, describe, mock, test } from "node:test";

import { listen, siteAuthHeaders, store, upstashRestServer } from "./fake-upstash-server";

type RateLimitModule = typeof import("../lib/rate-limit");

const SITE_TOKEN = "site-token-for-tests";
const CLOUD_TOKEN = "cloud-token-for-tests";

let cloudHits = 0;

let site: http.Server;
let cloud: http.Server;
let limiters: RateLimitModule;

/** Calls `limit` once and waits for the analytics write it leaves behind. */
async function hit(
  limiter: RateLimitModule[keyof RateLimitModule],
  identifier: string,
) {
  const result = await limiter.limit(identifier);
  await result.pending;
  return result;
}

/** The keys a limiter holds for one identifier, read back from Redis. */
async function bucketKeys(prefix: string, identifier: string): Promise<string[]> {
  return (await store.keys(`${prefix}:${identifier}:*`)).sort();
}

/**
 * Fixes `Date.now()` until the test ends; the limiter and the store both read it.
 * Node 22 reads `{ apis, now }`. The installed @types/node still types the
 * array form, so the options are cast, as in license-keys-card.test.ts.
 */
function pinClock(now: number) {
  mock.timers.enable({ apis: ["Date"], now } as unknown as Parameters<
    typeof mock.timers.enable
  >[0]);
}

afterEach(() => mock.timers.reset());

before(async () => {
  site = upstashRestServer();
  // A second server stands in for the transcription service's own Upstash
  // database. The site client must never reach it.
  cloud = http.createServer((req, res) => {
    cloudHits += 1;
    req.resume();
    res.end(JSON.stringify({ result: null }));
  });
  process.env.UPSTASH_REDIS_SITE_URL = await listen(site);
  process.env.UPSTASH_REDIS_SITE_TOKEN = SITE_TOKEN;
  process.env.UPSTASH_REDIS_CLOUD_URL = await listen(cloud);
  process.env.UPSTASH_REDIS_CLOUD_TOKEN = CLOUD_TOKEN;
  limiters = await import("../lib/rate-limit");
});

after(() => {
  for (const server of [site, cloud]) {
    server.closeAllConnections();
    server.close();
  }
  store.disconnect();
});

describe("downloadEmailRateLimiter", () => {
  test("allows 10 requests from one IP in an hour and blocks the 11th", async () => {
    const ip = "203.0.113.10";
    const remaining: number[] = [];
    for (let i = 0; i < 10; i++) {
      const result = await hit(limiters.downloadEmailRateLimiter, ip);
      assert.equal(result.success, true, `request ${i + 1} must pass`);
      assert.equal(result.limit, 10);
      remaining.push(result.remaining);
    }
    assert.deepEqual(remaining, [9, 8, 7, 6, 5, 4, 3, 2, 1, 0]);

    const blocked = await hit(limiters.downloadEmailRateLimiter, ip);
    assert.equal(blocked.success, false);
    assert.equal(blocked.remaining, 0);
  });

  test("counts in one-hour buckets under its own prefix", async () => {
    const ip = "203.0.113.11";
    const before = Date.now();
    const result = await hit(limiters.downloadEmailRateLimiter, ip);
    const hour = 60 * 60 * 1000;

    const bucket = Math.floor(before / hour);
    assert.deepEqual(await bucketKeys("ratelimit:download-email", ip), [
      `ratelimit:download-email:${ip}:${bucket}`,
    ]);
    // A sliding window resets at the end of the current bucket.
    assert.equal(result.reset, (bucket + 1) * hour);
  });

  test("keeps a separate budget per IP", async () => {
    for (let i = 0; i < 10; i++) await hit(limiters.downloadEmailRateLimiter, "203.0.113.12");
    assert.equal((await hit(limiters.downloadEmailRateLimiter, "203.0.113.12")).success, false);

    const other = await hit(limiters.downloadEmailRateLimiter, "203.0.113.13");
    assert.equal(other.success, true);
    assert.equal(other.remaining, 9);
  });

  test("records allowed and blocked requests for the Upstash dashboard", async () => {
    const ip = "203.0.113.14";
    for (let i = 0; i < 11; i++) await hit(limiters.downloadEmailRateLimiter, ip);

    const counts = new Map<string, number>();
    for (const key of await store.keys("ratelimit:download-email:events:*")) {
      const flat = await store.zrange(key, 0, -1, "WITHSCORES");
      for (let i = 0; i < flat.length; i += 2) {
        const event = JSON.parse(flat[i]) as { identifier: string; success: boolean };
        if (event.identifier !== ip) continue;
        const outcome = event.success ? "allowed" : "blocked";
        counts.set(outcome, (counts.get(outcome) ?? 0) + Number(flat[i + 1]));
      }
    }
    assert.deepEqual(Object.fromEntries(counts), { allowed: 10, blocked: 1 });
  });
});

describe("licenseValidateRateLimiter", () => {
  test("allows 30 requests from one IP in a minute and blocks the 31st", async () => {
    const ip = "198.51.100.20";
    for (let i = 0; i < 30; i++) {
      const result = await hit(limiters.licenseValidateRateLimiter, ip);
      assert.equal(result.success, true, `request ${i + 1} must pass`);
      assert.equal(result.limit, 30);
      assert.equal(result.remaining, 29 - i);
    }
    const blocked = await hit(limiters.licenseValidateRateLimiter, ip);
    assert.equal(blocked.success, false);
  });

  test("counts in one-minute buckets under its own prefix", async () => {
    const ip = "198.51.100.21";
    const before = Date.now();
    const result = await hit(limiters.licenseValidateRateLimiter, ip);
    const minute = 60 * 1000;

    const bucket = Math.floor(before / minute);
    assert.deepEqual(await bucketKeys("ratelimit:license-validate", ip), [
      `ratelimit:license-validate:${ip}:${bucket}`,
    ]);
    assert.equal(result.reset, (bucket + 1) * minute);
  });

  test("an IP blocked on the download form still has its license budget", async () => {
    const ip = "198.51.100.22";
    for (let i = 0; i < 10; i++) await hit(limiters.downloadEmailRateLimiter, ip);
    assert.equal((await hit(limiters.downloadEmailRateLimiter, ip)).success, false);

    const license = await hit(limiters.licenseValidateRateLimiter, ip);
    assert.equal(license.success, true);
    assert.equal(license.remaining, 29);
  });
});

describe("checkoutRateLimiter (#2010)", () => {
  test("allows 10 checkouts from one IP in a minute and blocks the 11th", async () => {
    const ip = "198.51.100.30";
    for (let i = 0; i < 10; i++) {
      const result = await hit(limiters.checkoutRateLimiter, ip);
      assert.equal(result.success, true, `request ${i + 1} must pass`);
      assert.equal(result.limit, 10);
    }
    const blocked = await hit(limiters.checkoutRateLimiter, ip);
    assert.equal(blocked.success, false);
  });

  test("counts under its own prefix, apart from the licence budget", async () => {
    const ip = "198.51.100.31";
    const before = Date.now();
    await hit(limiters.checkoutRateLimiter, ip);
    const bucket = Math.floor(before / (60 * 1000));

    assert.deepEqual(await bucketKeys("ratelimit:checkout", ip), [
      `ratelimit:checkout:${ip}:${bucket}`,
    ]);
    const license = await hit(limiters.licenseValidateRateLimiter, ip);
    assert.equal(license.remaining, 29);
  });
});

describe("latencyIngestRateLimiter", () => {
  test("gives each Fly region 6000 batches a minute", async () => {
    const before = Date.now();
    const result = await hit(limiters.latencyIngestRateLimiter, "fra");
    const minute = 60 * 1000;

    assert.equal(result.success, true);
    assert.equal(result.limit, 6000);
    assert.equal(result.remaining, 5999);
    const bucket = Math.floor(before / minute);
    assert.deepEqual(await bucketKeys("ratelimit:latency-ingest", "fra"), [
      `ratelimit:latency-ingest:fra:${bucket}`,
    ]);
    assert.equal(result.reset, (bucket + 1) * minute);
  });

  test("blocks a region at its ceiling and leaves the other regions alone", async () => {
    await hit(limiters.latencyIngestRateLimiter, "iad");
    const [key] = await bucketKeys("ratelimit:latency-ingest", "iad");
    // 6000 real round trips take minutes, so the bucket is filled to one
    // under the ceiling directly. The Lua script reads this counter as-is.
    await store.set(key, "5999");

    const last = await hit(limiters.latencyIngestRateLimiter, "iad");
    assert.equal(last.success, true);
    assert.equal(last.remaining, 0);

    const blocked = await hit(limiters.latencyIngestRateLimiter, "iad");
    assert.equal(blocked.success, false);

    const otherRegion = await hit(limiters.latencyIngestRateLimiter, "sin");
    assert.equal(otherRegion.success, true);
    assert.equal(otherRegion.remaining, 5999);
  });
});

describe("sliding windows", () => {
  // A fixed window would hand a burst at 10:59 a fresh budget at 11:00. A
  // sliding window weights the previous bucket by the share of it still
  // inside the last window: one tenth of the way in, that is 90%.
  const cases = [
    { name: "downloadEmailRateLimiter", prefix: "ratelimit:download-email", limit: 10, window: 60 * 60 * 1000, id: "203.0.113.40" },
    { name: "licenseValidateRateLimiter", prefix: "ratelimit:license-validate", limit: 30, window: 60 * 1000, id: "198.51.100.40" },
    { name: "latencyIngestRateLimiter", prefix: "ratelimit:latency-ingest", limit: 6000, window: 60 * 1000, id: "lhr" },
  ] as const;

  for (const c of cases) {
    test(`${c.name} carries 90% of a full previous window into the next`, async () => {
      const bucket = Math.floor(Date.now() / c.window);
      pinClock(bucket * c.window + c.window / 10);
      await store.set(`${c.prefix}:${c.id}:${bucket - 1}`, String(c.limit));

      const carried = Math.floor(0.9 * c.limit);
      const first = await hit(limiters[c.name], c.id);
      assert.equal(first.success, true);
      assert.equal(first.remaining, c.limit - carried - 1);
    });
  }

  test("a burst that fills the last hour is still blocked 6 minutes later", async () => {
    const ip = "203.0.113.41";
    const hour = 60 * 60 * 1000;
    const bucket = Math.floor(Date.now() / hour);
    pinClock(bucket * hour + 6 * 60 * 1000);
    await store.set(`ratelimit:download-email:${ip}:${bucket - 1}`, "10");

    // floor(0.9 * 10) = 9 carried over, so one request fits and then none.
    assert.equal((await hit(limiters.downloadEmailRateLimiter, ip)).success, true);
    assert.equal((await hit(limiters.downloadEmailRateLimiter, ip)).success, false);
  });
});

describe("site Redis client", () => {
  test("talks only to the site database, with the site token", async () => {
    await hit(limiters.licenseValidateRateLimiter, "192.0.2.30");

    assert.ok(siteAuthHeaders.length > 0);
    assert.ok(siteAuthHeaders.every((h) => h === `Bearer ${SITE_TOKEN}`));
    assert.equal(cloudHits, 0);
  });
});
