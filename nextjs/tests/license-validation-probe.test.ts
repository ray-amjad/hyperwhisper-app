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
  });
});
