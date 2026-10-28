/**
 * The "License key is required" 400 on activate, validate and deactivate
 * leaves one warning (#1299), as the internal email routes do (#1226).
 *
 * A desktop client that sends the key under the wrong name gets the same 400
 * body as before, and the server now records the event: the pathname and the
 * TYPE of `license_key`, never a value from the body (it is a licence key).
 */
import assert from "node:assert/strict";
import { after, before, beforeEach, describe, test } from "node:test";

import {
  calls,
  loadActivateRoute,
  loadDeactivateRoute,
  loadValidateRoute,
  logLines,
  postRequest,
  resetHarness,
  restoreRouteLogging,
  silenceRouteLogging,
  warnLines,
} from "./license-routes-harness";

/** A made-up key sent under the wrong field name. It must never be logged. */
const SENT_KEY = "HW-MISKEYED-SECRET-1299";

before(() => {
  silenceRouteLogging();
});

after(() => {
  restoreRouteLogging();
});

beforeEach(() => {
  resetHarness();
  logLines.length = 0;
});

/** No captured console line (rendered with util.inspect) carries the sent key. */
function assertKeyNeverLogged(): void {
  for (const line of [...warnLines, ...logLines]) {
    assert.ok(!line.includes(SENT_KEY), line);
  }
}

type Load = () => Promise<{ POST: (req: never) => Promise<Response> }>;

const ROUTES: {
  name: string;
  path: string;
  load: Load;
  required: Record<string, unknown>;
}[] = [
  {
    name: "activate",
    path: "/api/license/activate",
    load: loadActivateRoute as unknown as Load,
    required: {
      valid: false,
      error: "License key is required",
      reason: "bad_request",
    },
  },
  {
    name: "validate",
    path: "/api/license/validate",
    load: loadValidateRoute as unknown as Load,
    required: {
      valid: false,
      error: "License key is required",
      reason: "bad_request",
    },
  },
  {
    name: "deactivate",
    path: "/api/license/deactivate",
    load: loadDeactivateRoute as unknown as Load,
    required: { success: false, error: "License key is required" },
  },
];

for (const route of ROUTES) {
  describe(`POST ${route.path} without a usable license_key`, () => {
    test("a mis-keyed licence key: same 400, one warning with the pathname, no key", async () => {
      const { POST } = await route.load();

      const response = await POST(
        postRequest(route.path, { License_Key: SENT_KEY }) as never,
      );

      assert.equal(response.status, 400);
      assert.deepEqual(await response.json(), route.required);
      assert.equal(warnLines.length, 1, warnLines.join("\n"));
      assert.match(warnLines[0], /license key missing/);
      assert.ok(warnLines[0].includes(route.path), warnLines[0]);
      assert.match(warnLines[0], /licenseKeyType: 'undefined'/);
      assertKeyNeverLogged();
      assert.deepEqual(calls.findAccountByKey, []);
    });

    test("a key of the wrong type logs its type, not its value", async () => {
      const { POST } = await route.load();

      const response = await POST(
        postRequest(route.path, { license_key: [SENT_KEY] }) as never,
      );

      assert.equal(response.status, 400);
      assert.deepEqual(await response.json(), route.required);
      assert.equal(warnLines.length, 1, warnLines.join("\n"));
      assert.match(warnLines[0], /licenseKeyType: 'object'/);
      assertKeyNeverLogged();
    });

    test("a body that is not an object logs no-body-object", async () => {
      const { POST } = await route.load();

      const response = await POST(
        postRequest(route.path, JSON.stringify(SENT_KEY)) as never,
      );

      assert.equal(response.status, 400);
      assert.deepEqual(await response.json(), route.required);
      assert.equal(warnLines.length, 1, warnLines.join("\n"));
      assert.match(warnLines[0], /licenseKeyType: 'no-body-object'/);
      assertKeyNeverLogged();
    });

    test("a blank key is still required and logs type string", async () => {
      const { POST } = await route.load();

      const response = await POST(
        postRequest(route.path, { license_key: "   " }) as never,
      );

      assert.equal(response.status, 400);
      assert.deepEqual(await response.json(), route.required);
      assert.equal(warnLines.length, 1, warnLines.join("\n"));
      assert.match(warnLines[0], /licenseKeyType: 'string'/);
    });
  });
}
