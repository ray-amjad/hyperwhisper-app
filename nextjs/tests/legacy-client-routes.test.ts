/**
 * Three small routes that installed, un-upgraded desktop builds still call.
 * Nothing in the current apps exercises them, so a regression here reaches
 * only the users who never update — and nobody on the team sees it.
 *
 * `GET /api/config` returns the local trial caps. Legacy clients apply the
 * numbers as live limits: the Rust core blocks when `used >= limit`, so a 0 or
 * a negative value stops every local transcription, and the legacy Windows
 * ConfigService parses the fields as a 32-bit `int`, so a value above
 * 2,147,483,647 breaks its JSON deserialisation.
 *
 * `GET /api/checkout` is the retired licence checkout. Old apps still open it
 * with the key in the query string; it must send the buyer to /credits on the
 * same deploy and keep every parameter, or the purchase lands on no wallet.
 *
 * Both routes have no collaborators, so the tests import the real modules and
 * drive them with real Requests.
 */
import assert from "node:assert/strict";
import { describe, test } from "node:test";

import { NextRequest } from "next/server";

import { GET as getConfig } from "../app/api/config/route";
import { GET as getCheckout } from "../app/api/checkout/route";

const INT32_MAX = 2_147_483_647;

// Not the production host: a preview deploy must redirect to ITS OWN /credits.
const ORIGIN = "https://preview-42.example.test";

describe("GET /api/config (legacy trial caps)", () => {
  test("returns exactly the two cap fields, each 2,000,000,000", async () => {
    const response = await getConfig();

    assert.equal(response.status, 200);
    assert.match(response.headers.get("content-type") ?? "", /application\/json/);
    assert.deepEqual(await response.json(), {
      trial_daily_limit_seconds: 2_000_000_000,
      trial_model_download_limit: 2_000_000_000,
    });
  });

  test("every cap is a positive integer that fits a 32-bit int", async () => {
    const body = (await (await getConfig()).json()) as Record<string, unknown>;

    for (const [field, value] of Object.entries(body)) {
      assert.equal(typeof value, "number", field);
      assert.ok(Number.isInteger(value), `${field} is not an integer`);
      assert.ok((value as number) <= INT32_MAX, `${field} overflows int32`);
      // The core computes `remaining = limit - used` and blocks on
      // `used >= limit`; a day of recording must still leave headroom.
      assert.ok((value as number) > 86_400 * 365, `${field} is not unlimited`);
    }
  });

  test("lets a shared cache hold the answer for 6 hours", async () => {
    const response = await getConfig();

    assert.equal(response.headers.get("cache-control"), "public, max-age=21600");
  });
});

describe("GET /api/checkout (retired licence checkout)", () => {
  function checkout(pathAndQuery: string): Promise<Response> | Response {
    return getCheckout(new NextRequest(`${ORIGIN}${pathAndQuery}`));
  }

  test("redirects to /credits on the same origin with every parameter kept", async () => {
    const response = await checkout(
      "/api/checkout?license_key=HW-TEST-0000-0001&id=abc&code=SAVE10",
    );

    assert.equal(response.status, 307);
    const location = new URL(response.headers.get("location") ?? "");
    assert.equal(location.origin, ORIGIN);
    assert.equal(location.pathname, "/credits");
    assert.deepEqual(Array.from(location.searchParams.entries()), [
      ["license_key", "HW-TEST-0000-0001"],
      ["id", "abc"],
      ["code", "SAVE10"],
    ]);
  });

  test("keeps an encoded value byte for byte", async () => {
    const response = await checkout("/api/checkout?code=A%2BB%20C&license_key=");

    const location = new URL(response.headers.get("location") ?? "");
    assert.equal(location.search, "?code=A%2BB%20C&license_key=");
    assert.equal(location.searchParams.get("code"), "A+B C");
  });

  test("a bare request goes to /credits with no query string", async () => {
    const response = await checkout("/api/checkout");

    assert.equal(response.status, 307);
    assert.equal(response.headers.get("location"), `${ORIGIN}/credits`);
  });
});
