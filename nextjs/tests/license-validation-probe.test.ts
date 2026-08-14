import assert from "node:assert/strict";
import test from "node:test";

import { ResourceNotFound } from "@polar-sh/sdk/models/errors/resourcenotfound.js";
import { SDKError } from "@polar-sh/sdk/models/errors/sdkerror.js";

import { probeLicenseKeyReadOnly } from "../src/lib/license-validation-probe";

/**
 * The Polar SDK THROWS for a key it has never heard of, so the probe's catch
 * has to separate that ordinary verdict from a genuine lookup failure. See
 * `LicenseInvalidReason` for why the distinction matters, and
 * `tests/polar-errors.test.ts` for the predicate's own cases.
 */
const polarKeyNotFound = () => {
  const body = '{"error":"ResourceNotFound","detail":"LicenseKey not found"}';
  return new ResourceNotFound(
    { error: "ResourceNotFound", detail: "LicenseKey not found" },
    {
      response: new Response(body, { status: 404 }),
      request: new Request("https://example.test/v1/x", { method: "POST" }),
      body,
    },
  );
};

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
  // A Polar outage or a rotated token: we could not establish the license's
  // state, so this must NOT be labelled `not_entitled` — the client reports
  // `lookup_failed` to Sentry.
  const result = await probeLicenseKeyReadOnly("unreachable-key", {
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

test("reports a key Polar does not have as not_entitled", async () => {
  // The commonest rejection of all — a mistyped or non-existent key. The SDK
  // signals it by throwing its typed 404, so it used to be swallowed by the
  // same catch as an outage and mislabelled `lookup_failed`, which kept the
  // macOS client filing a Sentry error for an ordinary bad key.
  const result = await probeLicenseKeyReadOnly("no-such-key", {
    findStoredLicense: async () => null,
    validateWithPolar: async () => {
      throw polarKeyNotFound();
    },
  });

  assert.deepEqual(result, {
    valid: false,
    error: "License key not found",
    status: 400,
    reason: "not_entitled",
  });
});

test("keeps the HTTP status at 400 for both catch branches", async () => {
  // The status code is deliberately unchanged by the classification: `reason`
  // is the discriminator, not the status. A client keying off the status alone
  // sees exactly what it saw before.
  const notFound = await probeLicenseKeyReadOnly("no-such-key", {
    findStoredLicense: async () => null,
    validateWithPolar: async () => {
      throw polarKeyNotFound();
    },
  });
  const outage = await probeLicenseKeyReadOnly("unreachable-key", {
    findStoredLicense: async () => null,
    validateWithPolar: async () => {
      throw new Error("polar unreachable");
    },
  });

  assert.equal(notFound.valid, false);
  assert.equal(outage.valid, false);
  assert.equal(notFound.valid === false && notFound.status, 400);
  assert.equal(outage.valid === false && outage.status, 400);
});

test("a 404 that is not Polar's typed not-found stays lookup_failed", async () => {
  // A proxy or CDN error page returning 404 arrives as a generic SDKError that
  // still carries statusCode 404. Reading that as "the key does not exist"
  // would silence a routing/config incident, so it must degrade to
  // `lookup_failed` — the direction this classification always fails in.
  const html = "<!DOCTYPE html><html><body>Not Found</body></html>";
  const result = await probeLicenseKeyReadOnly("no-such-key", {
    findStoredLicense: async () => null,
    validateWithPolar: async () => {
      throw new SDKError("API error occurred", {
        response: new Response(html, { status: 404 }),
        request: new Request("https://example.test/v1/x", { method: "POST" }),
        body: html,
      });
    },
  });

  assert.deepEqual(result, {
    valid: false,
    error: "Failed to validate with Polar",
    status: 400,
    reason: "lookup_failed",
  });
});
