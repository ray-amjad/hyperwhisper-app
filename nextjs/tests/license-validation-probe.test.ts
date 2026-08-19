import assert from "node:assert/strict";
import test from "node:test";

import { probeLicenseKeyReadOnly } from "../src/lib/license-validation-probe";

test("returns a stored granted key as valid", async () => {
  const result = await probeLicenseKeyReadOnly("  stored-key  ", {
    findStoredLicense: async (key) => {
      assert.equal(key, "stored-key");
      return { status: "granted" };
    },
  });

  assert.deepEqual(result, { valid: true });
});

test("rejects a non-granted stored key as an entitlement verdict", async () => {
  const result = await probeLicenseKeyReadOnly("expired-key", {
    findStoredLicense: async () => ({ status: "expired" }),
  });

  assert.deepEqual(result, {
    valid: false,
    error: "License is expired",
    status: 400,
    reason: "not_entitled",
  });
});

test("reports an unknown key as not_entitled, not an incident", async () => {
  // The commonest rejection of all — a mistyped or non-existent key. It must
  // carry `not_entitled` so the macOS client does not file a Sentry error for
  // an ordinary bad key (HYPERWHISPER-SP / HYPERWHISPER-FM).
  const result = await probeLicenseKeyReadOnly("no-such-key", {
    findStoredLicense: async () => null,
  });

  assert.deepEqual(result, {
    valid: false,
    error: "License key not found",
    status: 400,
    reason: "not_entitled",
  });
});

test("keeps the HTTP status at 400 for the unknown-key verdict", async () => {
  // The status code is deliberately unchanged by the classification: `reason`
  // is the discriminator, not the status. A client keying off the status alone
  // sees exactly what it saw before.
  const result = await probeLicenseKeyReadOnly("no-such-key", {
    findStoredLicense: async () => null,
  });

  assert.equal(result.valid, false);
  assert.equal(result.valid === false && result.status, 400);
});
