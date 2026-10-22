/**
 * The admin devices `days` window is a whole number of days from 1 to 3650
 * (#2083). Before the bound, `admin.devices.list` with `{ days: 1e9 }` passed
 * the zod parser, the db-layer's `since.setDate(since.getDate() - days)` made
 * an Invalid Date, and the query answered 500 "Invalid time value". An
 * out-of-range or fractional window is now a 400 input error that never
 * reaches the database, on both procedures that take one.
 *
 * Drives the real tRPC route over HTTP; see `trpc-http-harness.ts` for what is
 * mocked (the db-layer among it, so `calls` shows whether the query ran).
 */
import assert from "node:assert/strict";
import { afterEach, beforeEach, describe, test } from "node:test";

import { behaviour, calls, httpQuery, resetHarness } from "./trpc-http-harness";

const LICENSE_ID = "33333333-3333-4333-8333-333333333333";
const REFUSED = [1e9, 3651, 1.5];

let restoreConsole: () => void;

beforeEach(() => {
  resetHarness();
  // The route logs every refused call; keep the test output readable.
  const original = console.error;
  console.error = () => {};
  restoreConsole = () => {
    console.error = original;
  };
});

afterEach(() => {
  restoreConsole();
  resetHarness();
});

describe("admin.devices.list days bounds", () => {
  for (const days of REFUSED) {
    test(`refuses days=${days} as BAD_REQUEST before it reaches the database`, async () => {
      const result = await httpQuery("admin.devices.list", { days });

      assert.equal(result.status, 400, `days=${days} answered ${result.status}: ${result.error?.message}`);
      assert.equal(result.error?.data.code, "BAD_REQUEST");
      assert.deepEqual(calls.deviceCounts, []);
    });
  }

  for (const days of [1, 3650]) {
    test(`accepts the edge value days=${days}`, async () => {
      const result = await httpQuery("admin.devices.list", { days });

      assert.equal(result.status, 200);
      assert.deepEqual(calls.deviceCounts, [days]);
    });
  }
});

describe("admin.devices.forLicense days bounds", () => {
  for (const days of REFUSED) {
    test(`refuses days=${days} as BAD_REQUEST before it reaches the database`, async () => {
      const result = await httpQuery("admin.devices.forLicense", { licenseKeyId: LICENSE_ID, days });

      assert.equal(result.status, 400, `days=${days} answered ${result.status}: ${result.error?.message}`);
      assert.equal(result.error?.data.code, "BAD_REQUEST");
      assert.deepEqual(calls.devicesForLicense, []);
    });
  }

  test("accepts the edge value days=3650", async () => {
    behaviour.devices = [];
    const result = await httpQuery("admin.devices.forLicense", { licenseKeyId: LICENSE_ID, days: 3650 });

    assert.equal(result.status, 200);
    assert.equal(calls.devicesForLicense.length, 1);
    assert.equal(calls.devicesForLicense[0].days, 3650);
  });
});
