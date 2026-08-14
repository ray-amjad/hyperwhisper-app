import assert from "node:assert/strict";
import test from "node:test";

import { probeLicenseKeyReadOnly } from "../src/lib/license-validation-probe";

test("returns a stored granted key without contacting Polar", async () => {
  let polarCalls = 0;

  const result = await probeLicenseKeyReadOnly("  stored-key  ", {
    findStoredLicense: async (key) => {
      assert.equal(key, "stored-key");
      return { status: "granted" };
    },
    validateWithPolar: async () => {
      polarCalls += 1;
      return { status: "granted" };
    },
  });

  assert.deepEqual(result, { valid: true });
  assert.equal(polarCalls, 0);
});

test("validates an unknown key through read-only Polar fallback", async () => {
  const calls: string[] = [];

  const result = await probeLicenseKeyReadOnly("polar-key", {
    findStoredLicense: async () => null,
    validateWithPolar: async (key) => {
      calls.push(key);
      return { status: "granted" };
    },
  });

  assert.deepEqual(result, { valid: true });
  assert.deepEqual(calls, ["polar-key"]);
});

test("rejects non-granted Polar keys without a persistence dependency", async () => {
  const result = await probeLicenseKeyReadOnly("revoked-key", {
    findStoredLicense: async () => null,
    validateWithPolar: async () => ({ status: "revoked" }),
  });

  assert.deepEqual(result, {
    valid: false,
    error: "License is revoked",
    status: 400,
    reason: "not_entitled",
  });
});

test("rejects a non-granted stored key as an entitlement verdict", async () => {
  let polarCalls = 0;

  const result = await probeLicenseKeyReadOnly("expired-key", {
    findStoredLicense: async () => ({ status: "expired" }),
    validateWithPolar: async () => {
      polarCalls += 1;
      return { status: "granted" };
    },
  });

  assert.deepEqual(result, {
    valid: false,
    error: "License is expired",
    status: 400,
    reason: "not_entitled",
  });
  assert.equal(polarCalls, 0);
});

test("reports a failed Polar lookup as lookup_failed, not an entitlement verdict", async () => {
  // A Polar outage, a rotated token and a genuinely unknown key all land in
  // this catch. Because they are indistinguishable, none of them may be
  // labelled `not_entitled` — the client reports `lookup_failed` to Sentry.
  const result = await probeLicenseKeyReadOnly("unknown-key", {
    findStoredLicense: async () => null,
    validateWithPolar: async () => {
      throw new Error("polar unreachable");
    },
  });

  assert.deepEqual(result, {
    valid: false,
    error: "Failed to validate with Polar",
    status: 400,
    reason: "lookup_failed",
  });
});
