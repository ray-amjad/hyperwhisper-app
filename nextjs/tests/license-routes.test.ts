/**
 * The three public license endpoints, driven as HTTP.
 *
 * These routes are the server-side entitlement gate: every installed macOS,
 * Windows and iOS build asks them whether a key may be used, and HyperWhisper
 * Cloud asks `/validate` for the credit balance it bills against. A wrong
 * answer here either gives the product away or locks a paying customer out, so
 * the tests below assert the reply a client actually reads — status, `valid`,
 * `reason`, and which fields are present — not that a mock was called.
 *
 * Collaborators are replaced at the module boundary in
 * `license-routes-harness.ts`; the route modules themselves are the real ones.
 */
import assert from "node:assert/strict";
import { after, before, beforeEach, describe, test } from "node:test";

import {
  accountKeyRow,
  behaviour,
  calls,
  loadAccountActivateRoute,
  loadAccountDeactivateRoute,
  loadAccountValidateRoute,
  loadActivateRoute,
  loadDeactivateRoute,
  loadValidateRoute,
  postRequest,
  resetHarness,
  restoreRouteLogging,
  silenceRouteLogging,
  storeRow,
} from "./license-routes-harness";

const VALIDATE_PATH = "/api/license/validate";
const ACTIVATE_PATH = "/api/license/activate";
const DEACTIVATE_PATH = "/api/license/deactivate";

/** A made-up key string. Only the fake database in the harness knows it. */
const GRANTED_KEY = "HW-TEST-0000-0001";

type JsonBody = Record<string, unknown>;

async function readJson(response: Response): Promise<JsonBody> {
  return (await response.json()) as JsonBody;
}

before(() => {
  silenceRouteLogging();
});

after(() => {
  restoreRouteLogging();
});

beforeEach(() => {
  resetHarness();
});

describe("POST /api/license/validate", () => {
  test("rejects a rate-limited caller before it touches the database", async () => {
    behaviour.rateLimitSuccess = false;
    storeRow(accountKeyRow({ key: GRANTED_KEY }));
    const { POST } = await loadValidateRoute();

    const response = await POST(
      postRequest(VALIDATE_PATH, { license_key: GRANTED_KEY }),
    );
    const body = await readJson(response);

    assert.equal(response.status, 429);
    assert.equal(body.valid, false);
    // A throttled request is not an entitlement verdict: the client must keep
    // reporting it. See `LicenseInvalidReason`.
    assert.equal(body.reason, "lookup_failed");
    assert.deepEqual(calls.findAccountByKey, []);
  });

  test("keys the rate limiter on the Vercel forwarded address, not the raw chain", async () => {
    storeRow(accountKeyRow({ key: GRANTED_KEY }));
    const { POST } = await loadValidateRoute();

    await POST(
      postRequest(
        VALIDATE_PATH,
        { license_key: GRANTED_KEY },
        {
          "x-vercel-forwarded-for": "203.0.113.7, 198.51.100.9",
          "x-forwarded-for": "198.51.100.9",
        },
      ),
    );

    assert.deepEqual(calls.rateLimit, ["203.0.113.7"]);
  });

  test("falls back to a single bucket when no header carries a usable address", async () => {
    storeRow(accountKeyRow({ key: GRANTED_KEY }));
    const { POST } = await loadValidateRoute();

    await POST(
      postRequest(
        VALIDATE_PATH,
        { license_key: GRANTED_KEY },
        { "x-forwarded-for": "not-an-ip" },
      ),
    );

    assert.deepEqual(calls.rateLimit, ["unknown"]);
  });

  test("rejects a body that is not JSON", async () => {
    const { POST } = await loadValidateRoute();

    const response = await POST(postRequest(VALIDATE_PATH, "{not json"));
    const body = await readJson(response);

    assert.equal(response.status, 400);
    assert.equal(body.valid, false);
    assert.equal(body.reason, "bad_request");
    assert.deepEqual(calls.findAccountByKey, []);
  });

  test("rejects a body whose license_key is absent or not a string", async () => {
    const { POST } = await loadValidateRoute();

    for (const body of [{}, { license_key: 12345 }, { license_key: null }, []]) {
      const response = await POST(postRequest(VALIDATE_PATH, body));
      const json = await readJson(response);

      assert.equal(response.status, 400, `body ${JSON.stringify(body)}`);
      assert.equal(json.valid, false);
      assert.equal(json.reason, "bad_request");
    }
    assert.deepEqual(calls.findAccountByKey, []);
  });

  test("rejects an empty license_key rather than looking it up", async () => {
    const { POST } = await loadValidateRoute();

    const response = await POST(postRequest(VALIDATE_PATH, { license_key: "" }));
    const body = await readJson(response);

    assert.equal(response.status, 400);
    assert.equal(body.reason, "bad_request");
    assert.deepEqual(calls.findAccountByKey, []);
  });

  test("rejects a key the database does not hold", async () => {
    const { POST } = await loadValidateRoute();

    const response = await POST(
      postRequest(VALIDATE_PATH, { license_key: "HW-TEST-NOPE-0000" }),
    );
    const body = await readJson(response);

    assert.equal(response.status, 400);
    assert.equal(body.valid, false);
    assert.equal(body.error, "License key not found");
    // An unknown key is an ordinary verdict, so the client must NOT alert.
    assert.equal(body.reason, "not_entitled");
  });

  test("rejects a key whose status is not granted, and names the status", async () => {
    for (const status of ["revoked", "disabled", "expired"]) {
      resetHarness();
      storeRow(accountKeyRow({ key: GRANTED_KEY, status }));
      const { POST } = await loadValidateRoute();

      const response = await POST(
        postRequest(VALIDATE_PATH, { license_key: GRANTED_KEY }),
      );
      const body = await readJson(response);

      assert.equal(response.status, 400, status);
      assert.equal(body.valid, false, status);
      assert.equal(body.error, `License is ${status}`);
      assert.equal(body.reason, "not_entitled");
      assert.deepEqual(calls.getCreditBalance, []);
    }
  });

  test("trims the key before the lookup, so a pasted key with whitespace still validates", async () => {
    storeRow(accountKeyRow({ key: GRANTED_KEY }));
    const { POST } = await loadValidateRoute();

    const response = await POST(
      postRequest(VALIDATE_PATH, { license_key: `  ${GRANTED_KEY}\n` }),
    );

    assert.equal(response.status, 200);
    assert.deepEqual(await readJson(response), { valid: true });
    assert.deepEqual(calls.findAccountByKey, [GRANTED_KEY]);
  });

  test("answers a granted key with valid:true and nothing else", async () => {
    storeRow(accountKeyRow({ key: GRANTED_KEY }));
    const { POST } = await loadValidateRoute();

    const response = await POST(
      postRequest(VALIDATE_PATH, { license_key: GRANTED_KEY }),
    );
    const body = await readJson(response);

    assert.equal(response.status, 200);
    // The basic reply must not leak the balance or the Stripe customer id to a
    // caller that did not ask for them, and must not pay for the balance read.
    assert.deepEqual(body, { valid: true });
    assert.deepEqual(calls.getCreditBalance, []);
  });

  test("returns the balance and the Stripe customer id when include_credits is true", async () => {
    behaviour.creditBalance = 987;
    storeRow(
      accountKeyRow({
        key: GRANTED_KEY,
        userId: "user_42",
        stripeCustomerId: "cus_42",
      }),
    );
    const { POST } = await loadValidateRoute();

    const response = await POST(
      postRequest(VALIDATE_PATH, {
        license_key: GRANTED_KEY,
        include_credits: true,
      }),
    );

    assert.equal(response.status, 200);
    assert.deepEqual(await readJson(response), {
      valid: true,
      credits: 987,
      stripe_customer_id: "cus_42",
    });
    // The balance belongs to the key's owner, not to the key.
    assert.deepEqual(calls.getCreditBalance, ["user_42"]);
  });

  test("normalises a missing stripe_customer_id to null, and always sends the field", async () => {
    // The Cloud reads this field to bill against. A blank customer id is not a
    // customer id, so both a null column and a legacy empty string must come
    // back as JSON null — and the key must be present either way, so a reader
    // can tell "no customer" from "field not sent".
    for (const stored of [null, ""]) {
      resetHarness();
      storeRow(
        accountKeyRow({ key: GRANTED_KEY, stripeCustomerId: stored }),
      );
      const { POST } = await loadValidateRoute();

      const response = await POST(
        postRequest(VALIDATE_PATH, {
          license_key: GRANTED_KEY,
          include_credits: true,
        }),
      );
      const body = await readJson(response);

      assert.ok("stripe_customer_id" in body, `stored ${JSON.stringify(stored)}`);
      assert.equal(body.stripe_customer_id, null);
    }
  });

  test("treats a truthy non-boolean include_credits as absent", async () => {
    storeRow(accountKeyRow({ key: GRANTED_KEY }));
    const { POST } = await loadValidateRoute();

    const response = await POST(
      postRequest(VALIDATE_PATH, {
        license_key: GRANTED_KEY,
        include_credits: "true",
      }),
    );

    assert.deepEqual(await readJson(response), { valid: true });
    assert.deepEqual(calls.getCreditBalance, []);
  });

  test("records the device when device_id is sent", async () => {
    storeRow(accountKeyRow({ key: GRANTED_KEY, id: "key_row_9" }));
    const { POST } = await loadValidateRoute();

    await POST(
      postRequest(VALIDATE_PATH, {
        license_key: GRANTED_KEY,
        device_id: "device-abc",
        device_name: "Studio Mac",
      }),
    );

    assert.deepEqual(calls.upsertDeviceValidation, [
      {
        licenseKeyId: "key_row_9",
        deviceId: "device-abc",
        deviceName: "Studio Mac",
      },
    ]);
  });

  test("records the device with no name when device_name is not a string", async () => {
    storeRow(accountKeyRow({ key: GRANTED_KEY, id: "key_row_9" }));
    const { POST } = await loadValidateRoute();

    await POST(
      postRequest(VALIDATE_PATH, {
        license_key: GRANTED_KEY,
        device_id: "device-abc",
        device_name: 7,
      }),
    );

    assert.deepEqual(calls.upsertDeviceValidation, [
      {
        licenseKeyId: "key_row_9",
        deviceId: "device-abc",
        deviceName: undefined,
      },
    ]);
  });

  test("does not record a device when device_id is absent", async () => {
    storeRow(accountKeyRow({ key: GRANTED_KEY }));
    const { POST } = await loadValidateRoute();

    await POST(
      postRequest(VALIDATE_PATH, {
        license_key: GRANTED_KEY,
        device_name: "Studio Mac",
      }),
    );

    assert.deepEqual(calls.upsertDeviceValidation, []);
  });

  test("still validates when device tracking fails", async () => {
    storeRow(accountKeyRow({ key: GRANTED_KEY }));
    behaviour.deviceTrackingError = new Error("device_validations is down");
    const { POST } = await loadValidateRoute();

    const response = await POST(
      postRequest(VALIDATE_PATH, {
        license_key: GRANTED_KEY,
        device_id: "device-abc",
      }),
    );

    // Tracking is for fair-usage monitoring only. It must never cost a paying
    // customer their license.
    assert.equal(response.status, 200);
    assert.deepEqual(await readJson(response), { valid: true });
  });

  test("fails closed when the lookup itself throws", async () => {
    behaviour.lookupError = new Error("connection terminated unexpectedly");
    const { POST } = await loadValidateRoute();

    const response = await POST(
      postRequest(VALIDATE_PATH, { license_key: GRANTED_KEY }),
    );
    const body = await readJson(response);

    assert.equal(response.status, 500);
    assert.equal(body.valid, false);
    // Not a verdict — we never established the license's state.
    assert.equal(body.reason, "lookup_failed");
  });

  test("probe_only answers from the stored row without writing anything", async () => {
    storeRow(accountKeyRow({ key: GRANTED_KEY }));
    const { POST } = await loadValidateRoute();

    const response = await POST(
      postRequest(VALIDATE_PATH, {
        license_key: GRANTED_KEY,
        probe_only: true,
        device_id: "device-abc",
        include_credits: true,
      }),
    );

    assert.equal(response.status, 200);
    // The probe is the Test button in the app. It reports the verdict only: no
    // device row, no balance read, no credits in the reply.
    assert.deepEqual(await readJson(response), { valid: true });
    assert.deepEqual(calls.upsertDeviceValidation, []);
    assert.deepEqual(calls.getCreditBalance, []);
  });

  test("probe_only rejects an unknown key with the same verdict as a full check", async () => {
    const { POST } = await loadValidateRoute();

    const response = await POST(
      postRequest(VALIDATE_PATH, {
        license_key: "HW-TEST-NOPE-0000",
        probe_only: true,
      }),
    );
    const body = await readJson(response);

    assert.equal(response.status, 400);
    assert.equal(body.valid, false);
    assert.equal(body.reason, "not_entitled");
    assert.deepEqual(calls.upsertDeviceValidation, []);
  });

  test("probe_only rejects a key whose status is not granted", async () => {
    storeRow(accountKeyRow({ key: GRANTED_KEY, status: "revoked" }));
    const { POST } = await loadValidateRoute();

    const response = await POST(
      postRequest(VALIDATE_PATH, {
        license_key: GRANTED_KEY,
        probe_only: true,
      }),
    );
    const body = await readJson(response);

    assert.equal(response.status, 400);
    assert.equal(body.error, "License is revoked");
    assert.equal(body.reason, "not_entitled");
  });

  test("treats a truthy non-boolean probe_only as absent, so the full path runs", async () => {
    storeRow(accountKeyRow({ key: GRANTED_KEY, id: "key_row_9" }));
    const { POST } = await loadValidateRoute();

    await POST(
      postRequest(VALIDATE_PATH, {
        license_key: GRANTED_KEY,
        probe_only: "yes",
        device_id: "device-abc",
      }),
    );

    assert.equal(calls.upsertDeviceValidation.length, 1);
  });

  test("every invalid reply carries a reason", async () => {
    const { POST } = await loadValidateRoute();

    const cases: Array<() => Promise<Response>> = [
      // Rate limited.
      async () => {
        behaviour.rateLimitSuccess = false;
        return POST(postRequest(VALIDATE_PATH, { license_key: GRANTED_KEY }));
      },
      // Unparseable body.
      async () => POST(postRequest(VALIDATE_PATH, "{")),
      // Missing key.
      async () => POST(postRequest(VALIDATE_PATH, {})),
      // Unknown key.
      async () => POST(postRequest(VALIDATE_PATH, { license_key: "HW-X" })),
      // Lookup fault.
      async () => {
        behaviour.lookupError = new Error("down");
        return POST(postRequest(VALIDATE_PATH, { license_key: GRANTED_KEY }));
      },
    ];

    const allowed = new Set(["not_entitled", "lookup_failed", "bad_request"]);
    for (let index = 0; index < cases.length; index += 1) {
      resetHarness();
      const body = await readJson(await cases[index]!());

      assert.equal(body.valid, false, `case ${index}`);
      assert.ok(
        typeof body.reason === "string" && allowed.has(body.reason),
        `case ${index} reason was ${String(body.reason)}`,
      );
    }
  });
});

describe("POST /api/license/activate", () => {
  test("answers a granted key with a fresh activation id", async () => {
    storeRow(accountKeyRow({ key: GRANTED_KEY }));
    const { POST } = await loadActivateRoute();

    const first = await readJson(
      await POST(postRequest(ACTIVATE_PATH, { license_key: GRANTED_KEY })),
    );
    const second = await readJson(
      await POST(postRequest(ACTIVATE_PATH, { license_key: GRANTED_KEY })),
    );

    assert.equal(first.valid, true);
    assert.match(
      String(first.activation_id),
      /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/,
    );
    assert.notEqual(first.activation_id, second.activation_id);
  });

  test("fails closed for a key whose status is not granted", async () => {
    storeRow(accountKeyRow({ key: GRANTED_KEY, status: "revoked" }));
    const { POST } = await loadActivateRoute();

    const response = await POST(
      postRequest(ACTIVATE_PATH, { license_key: GRANTED_KEY }),
    );
    const body = await readJson(response);

    // An old macOS build treats this reply as authoritative, so a revoked key
    // must not come back with an activation id.
    assert.equal(response.status, 400);
    assert.equal(body.valid, false);
    assert.equal(body.reason, "not_entitled");
    assert.ok(!("activation_id" in body));
  });

  test("fails closed for a key the database does not hold", async () => {
    const { POST } = await loadActivateRoute();

    const response = await POST(
      postRequest(ACTIVATE_PATH, { license_key: "HW-TEST-NOPE-0000" }),
    );
    const body = await readJson(response);

    assert.equal(response.status, 400);
    assert.equal(body.valid, false);
    assert.equal(body.reason, "not_entitled");
    assert.ok(!("activation_id" in body));
  });

  test("rejects a rate-limited caller before it touches the database", async () => {
    behaviour.rateLimitSuccess = false;
    storeRow(accountKeyRow({ key: GRANTED_KEY }));
    const { POST } = await loadActivateRoute();

    const response = await POST(
      postRequest(ACTIVATE_PATH, { license_key: GRANTED_KEY }),
    );
    const body = await readJson(response);

    assert.equal(response.status, 429);
    assert.equal(body.reason, "lookup_failed");
    assert.deepEqual(calls.findAccountByKey, []);
  });

  test("rejects a malformed body and a missing key", async () => {
    const { POST } = await loadActivateRoute();

    const unparseable = await POST(postRequest(ACTIVATE_PATH, "{nope"));
    assert.equal(unparseable.status, 400);
    assert.equal((await readJson(unparseable)).reason, "bad_request");

    const missing = await POST(postRequest(ACTIVATE_PATH, { license_key: 9 }));
    assert.equal(missing.status, 400);
    assert.equal((await readJson(missing)).reason, "bad_request");

    assert.deepEqual(calls.findAccountByKey, []);
  });

  test("fails closed when the lookup throws", async () => {
    behaviour.lookupError = new Error("connection terminated unexpectedly");
    const { POST } = await loadActivateRoute();

    const response = await POST(
      postRequest(ACTIVATE_PATH, { license_key: GRANTED_KEY }),
    );
    const body = await readJson(response);

    assert.equal(response.status, 500);
    assert.equal(body.valid, false);
    assert.equal(body.reason, "lookup_failed");
    assert.ok(!("activation_id" in body));
  });
});

describe("POST /api/license/deactivate", () => {
  test("accepts a well-formed request without touching the database", async () => {
    const { POST } = await loadDeactivateRoute();

    const response = await POST(
      postRequest(DEACTIVATE_PATH, { license_key: GRANTED_KEY }),
    );

    assert.equal(response.status, 200);
    assert.deepEqual(await readJson(response), {
      success: true,
      message: "License deactivated successfully",
    });
    // The endpoint is a stub: deactivation is local to the app now.
    assert.deepEqual(calls.findAccountByKey, []);
    assert.deepEqual(calls.rateLimit, []);
  });

  test("rejects a body that is not JSON", async () => {
    const { POST } = await loadDeactivateRoute();

    const response = await POST(postRequest(DEACTIVATE_PATH, "{"));

    assert.equal(response.status, 400);
    assert.deepEqual(await readJson(response), {
      success: false,
      error: "Invalid request body",
    });
  });

  test("rejects an absent, empty or non-string license_key", async () => {
    const { POST } = await loadDeactivateRoute();

    for (const body of [{}, { license_key: "" }, { license_key: 5 }, []]) {
      const response = await POST(postRequest(DEACTIVATE_PATH, body));

      assert.equal(response.status, 400, JSON.stringify(body));
      assert.deepEqual(await readJson(response), {
        success: false,
        error: "License key is required",
      });
    }
  });
});

describe("/api/account/* is the same handler as /api/license/*", () => {
  test("each account route re-exports the license handler itself", async () => {
    // The canonical /api/account/* paths must not drift from the installed
    // apps' /api/license/* paths. Identity is the only check that stays true
    // when either handler changes.
    const pairs: Array<[Promise<{ POST: unknown }>, Promise<{ POST: unknown }>]> =
      [
        [loadAccountValidateRoute(), loadValidateRoute()],
        [loadAccountActivateRoute(), loadActivateRoute()],
        [loadAccountDeactivateRoute(), loadDeactivateRoute()],
      ];

    for (const [accountRoute, licenseRoute] of pairs) {
      const account = await accountRoute;
      const license = await licenseRoute;
      assert.equal(account.POST, license.POST);
    }
  });

  test("the account validate path enforces entitlement too", async () => {
    storeRow(accountKeyRow({ key: GRANTED_KEY, status: "revoked" }));
    const { POST } = await loadAccountValidateRoute();

    const response = await POST(
      postRequest("/api/account/validate", { license_key: GRANTED_KEY }),
    );
    const body = await readJson(response);

    assert.equal(response.status, 400);
    assert.equal(body.valid, false);
    assert.equal(body.reason, "not_entitled");
  });
});
