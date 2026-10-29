/**
 * scripts/cf-build.mjs hands `next build` fake values for the server secrets
 * Workers Builds does not have. Every fake must be recognisable (the script
 * fails the build if one reaches .open-next/), and none may look real.
 */
import assert from "node:assert/strict";
import test from "node:test";

import {
  BUILD_PLACEHOLDERS,
  PLACEHOLDER_MARK,
} from "../scripts/cf-build.mjs";

test("every secret placeholder carries the leak-check mark", () => {
  for (const [name, value] of Object.entries(BUILD_PLACEHOLDERS)) {
    if (name === "BETTER_AUTH_URL") continue; // public origin, not a secret
    assert.ok(value.includes(PLACEHOLDER_MARK), `${name} = ${value}`);
  }
});

test("BETTER_AUTH_URL defaults to the real public origin", () => {
  assert.equal(BUILD_PLACEHOLDERS.BETTER_AUTH_URL, "https://www.hyperwhisper.com");
});

test("the Stripe placeholders keep the prefixes the SDK checks", () => {
  assert.ok(BUILD_PLACEHOLDERS.STRIPE_SECRET_KEY.startsWith("sk_"));
  assert.ok(BUILD_PLACEHOLDERS.STRIPE_WEBHOOK_SECRET.startsWith("whsec_"));
});
