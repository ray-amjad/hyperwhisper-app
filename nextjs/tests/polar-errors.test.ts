import assert from "node:assert/strict";
import test from "node:test";

import { ConnectionError } from "@polar-sh/sdk/models/errors/httpclienterrors.js";
import { RequestTimeoutError } from "@polar-sh/sdk/models/errors/httpclienterrors.js";
import { ResourceNotFound } from "@polar-sh/sdk/models/errors/resourcenotfound.js";
import { SDKError } from "@polar-sh/sdk/models/errors/sdkerror.js";

import { isPolarKeyNotFoundError } from "../src/lib/polar-errors";

/**
 * `isPolarKeyNotFoundError` decides whether a thrown Polar error is an ordinary
 * "no such key" verdict (`not_entitled`) or a failed lookup (`lookup_failed`).
 * A false positive here would report a Polar outage as an ordinary rejection
 * and silence the alert, so every case below that is not a well-formed 404 is
 * asserted to be false.
 *
 * The first half builds REAL SDK error objects, so that an SDK upgrade which
 * changes the error shape fails here rather than silently degrading in
 * production. The second half covers shapes the SDK does not currently produce.
 */

const request = new Request("https://example.test/v1/x", { method: "POST" });

function sdkError(status: number, body: string): SDKError {
  return new SDKError("API error occurred", {
    response: new Response(body, { status }),
    request,
    body,
  });
}

// MARK: - Real SDK errors

test("recognises the SDK's typed 404 for a key Polar does not have", () => {
  const body = '{"error":"ResourceNotFound","detail":"LicenseKey not found"}';
  const err = new ResourceNotFound(
    { error: "ResourceNotFound", detail: "LicenseKey not found" },
    { response: new Response(body, { status: 404 }), request, body },
  );

  // Pin the three fields the predicate reads, so a rename in the SDK is a
  // failure here and not a silent behaviour change.
  assert.equal(err.name, "ResourceNotFound");
  assert.equal(err.error, "ResourceNotFound");
  assert.equal(err.statusCode, 404);

  assert.equal(isPolarKeyNotFoundError(err), true);
});

test("does NOT treat a 404 with a non-JSON body as a not-found verdict", () => {
  // A CDN/proxy error page or a misconfigured base URL: the SDK's 404 matcher
  // needs a JSON body, so this arrives as a generic SDKError that still carries
  // statusCode 404. It is an incident, not a verdict — this is exactly the case
  // a bare `statusCode === 404` check would have misclassified.
  const err = sdkError(404, "<!DOCTYPE html><html><body>Not Found</body></html>");

  assert.equal(err.name, "SDKError");
  assert.equal(err.statusCode, 404);
  assert.equal(isPolarKeyNotFoundError(err), false);
});

test("does NOT treat a Polar 5xx as a not-found verdict", () => {
  assert.equal(isPolarKeyNotFoundError(sdkError(500, "upstream boom")), false);
  assert.equal(isPolarKeyNotFoundError(sdkError(503, "unavailable")), false);
});

test("does NOT treat an auth failure as a not-found verdict", () => {
  // A rotated or missing POLAR_ACCESS_TOKEN. Silencing this would hide a
  // misconfiguration that rejects every paying customer.
  assert.equal(isPolarKeyNotFoundError(sdkError(401, '{"detail":"Unauthorized"}')), false);
  assert.equal(isPolarKeyNotFoundError(sdkError(403, '{"detail":"Forbidden"}')), false);
});

test("does NOT treat a transport failure as a not-found verdict", () => {
  // These extend HTTPClientError, not PolarError, so they carry no statusCode
  // at all — the branch that must never be read as "the key does not exist".
  const connection = new ConnectionError("Unable to make request", {
    cause: new TypeError("fetch failed"),
  });
  const timeout = new RequestTimeoutError("Request timed out", {
    cause: new Error("timeout"),
  });

  assert.equal((connection as unknown as { statusCode?: number }).statusCode, undefined);
  assert.equal(isPolarKeyNotFoundError(connection), false);
  assert.equal(isPolarKeyNotFoundError(timeout), false);
});

// MARK: - Shapes the SDK does not produce (defensive degradation)

test("degrades to lookup_failed for anything that is not an object", () => {
  for (const value of [undefined, null, "ResourceNotFound", 404, true, Symbol("x")]) {
    assert.equal(isPolarKeyNotFoundError(value), false);
  }
});

test("degrades to lookup_failed for a plain Error", () => {
  assert.equal(isPolarKeyNotFoundError(new Error("polar unreachable")), false);
});

test("requires all three fields, so a partial match does not qualify", () => {
  // Each of these is one field short of the real thing. If a future SDK stops
  // setting any one of them, the predicate must fail toward lookup_failed.
  assert.equal(
    isPolarKeyNotFoundError({ name: "ResourceNotFound", error: "ResourceNotFound" }),
    false,
  );
  assert.equal(
    isPolarKeyNotFoundError({ statusCode: 404, error: "ResourceNotFound" }),
    false,
  );
  assert.equal(
    isPolarKeyNotFoundError({ statusCode: 404, name: "ResourceNotFound" }),
    false,
  );
});

test("matches the status code exactly and by type", () => {
  const base = { name: "ResourceNotFound", error: "ResourceNotFound" };

  // A string "404" is not a number 404 — no coercion.
  assert.equal(isPolarKeyNotFoundError({ ...base, statusCode: "404" }), false);
  assert.equal(isPolarKeyNotFoundError({ ...base, statusCode: 400 }), false);
  assert.equal(isPolarKeyNotFoundError({ ...base, statusCode: 410 }), false);
  assert.equal(isPolarKeyNotFoundError({ ...base, statusCode: 404 }), true);
});

test("matches the error literal exactly, never by case or prefix", () => {
  const base = { statusCode: 404, name: "ResourceNotFound" };

  assert.equal(isPolarKeyNotFoundError({ ...base, error: "resourcenotfound" }), false);
  assert.equal(isPolarKeyNotFoundError({ ...base, error: "ResourceNotFoundError" }), false);
  assert.equal(isPolarKeyNotFoundError({ ...base, error: "" }), false);
});
