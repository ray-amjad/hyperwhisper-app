/**
 * The "license_key is required" 400 on GET and POST /api/license/credits
 * leaves one warning (#1299): the pathname and the TYPE of `license_key`,
 * never a value from the request (it is a licence key).
 */
import assert from "node:assert/strict";
import { after, before, beforeEach, describe, test } from "node:test";

import {
  calls,
  getRequest,
  loadLicenseCreditsRoute,
  logLines,
  postRequest,
  resetHarness,
  restoreRouteLogging,
  silenceRouteLogging,
} from "./credit-routes-harness";

const PATH = "/api/license/credits";
/** A made-up key sent under the wrong field name. It must never be logged. */
const SENT_KEY = "HW-MISKEYED-SECRET-1299";
const REQUIRED = { error: "license_key is required" };

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

/** The harness captures error, log and warn, each line prefixed by its level. */
function warnings(): string[] {
  return logLines.filter((line) => line.startsWith("warn "));
}

function assertKeyNeverLogged(): void {
  for (const line of logLines) assert.ok(!line.includes(SENT_KEY), line);
}

describe("GET /api/license/credits without a usable license_key", () => {
  test("a mis-keyed query: same 400, one warning with the pathname, no key", async () => {
    const { GET } = await loadLicenseCreditsRoute();

    const response = await GET(
      getRequest(`${PATH}?License_Key=${encodeURIComponent(SENT_KEY)}`),
    );

    assert.equal(response.status, 400);
    assert.deepEqual(await response.json(), REQUIRED);
    assert.equal(warnings().length, 1, logLines.join("\n"));
    assert.match(warnings()[0], /Credits balance: license_key missing/);
    assert.ok(warnings()[0].includes(PATH), warnings()[0]);
    assert.match(warnings()[0], /licenseKeyType: 'undefined'/);
    assertKeyNeverLogged();
  });

  test("a blank key logs type string", async () => {
    const { GET } = await loadLicenseCreditsRoute();

    const response = await GET(getRequest(`${PATH}?license_key=%20%20`));

    assert.equal(response.status, 400);
    assert.deepEqual(await response.json(), REQUIRED);
    assert.equal(warnings().length, 1, logLines.join("\n"));
    assert.match(warnings()[0], /licenseKeyType: 'string'/);
  });
});

describe("POST /api/license/credits without a usable license_key", () => {
  test("a mis-keyed body: same 400, one warning with the pathname, no key", async () => {
    const { POST } = await loadLicenseCreditsRoute();

    const response = await POST(
      postRequest(PATH, { License_Key: SENT_KEY, amount: 1 }),
    );

    assert.equal(response.status, 400);
    assert.deepEqual(await response.json(), REQUIRED);
    assert.equal(warnings().length, 1, logLines.join("\n"));
    assert.match(warnings()[0], /Credits deduction: license_key missing/);
    assert.ok(warnings()[0].includes(PATH), warnings()[0]);
    assert.match(warnings()[0], /licenseKeyType: 'undefined'/);
    assertKeyNeverLogged();
    assert.deepEqual(calls.deductCreditBalance, []);
  });

  test("a key of the wrong type logs its type, not its value", async () => {
    const { POST } = await loadLicenseCreditsRoute();

    const response = await POST(
      postRequest(PATH, { license_key: { key: SENT_KEY }, amount: 1 }),
    );

    assert.equal(response.status, 400);
    assert.deepEqual(await response.json(), REQUIRED);
    assert.equal(warnings().length, 1, logLines.join("\n"));
    assert.match(warnings()[0], /licenseKeyType: 'object'/);
    assertKeyNeverLogged();
  });

  test("a body that is not an object logs no-body-object", async () => {
    const { POST } = await loadLicenseCreditsRoute();

    const response = await POST(postRequest(PATH, JSON.stringify(SENT_KEY)));

    assert.equal(response.status, 400);
    assert.deepEqual(await response.json(), REQUIRED);
    assert.equal(warnings().length, 1, logLines.join("\n"));
    assert.match(warnings()[0], /licenseKeyType: 'no-body-object'/);
    assertKeyNeverLogged();
  });
});
