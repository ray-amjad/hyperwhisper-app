/**
 * #1657: one credits-per-minute rate for every credits <-> minutes conversion.
 *
 * The admin Customers page used 6.3 credits per minute while the customer
 * dashboard used 1.67, so the admin saw about a quarter of the minutes the
 * customer saw for the same balance. Both pages now read
 * `lib/credits-per-minute.ts`, and the cloud's own `CREDITS_PER_MINUTE` holds
 * the same value.
 *
 * The admin page is a client component the node runner cannot render, so the
 * checks on it read its source: it imports the shared constant, declares no
 * rate of its own, and every minutes figure divides by that constant.
 */
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";

import { CREDITS_PER_MINUTE } from "@/lib/credits-per-minute";

const IMPORT_LINE =
  'import { CREDITS_PER_MINUTE } from "@/lib/credits-per-minute";';

const ADMIN_PAGE =
  "app/[locale]/user/(authenticated)/customers/CustomersClient.tsx";
const DASHBOARD_ROUTER = "server/api/routers/customer.ts";
const CLOUD_CONSTANTS = "../hyperwhisper-cloud/src/lib/constants.ts";

function source(path: string): string {
  return readFileSync(path, "utf8").replace(/\r\n/g, "\n");
}

test("the shared rate is 1.67 credits per minute (Ray, #1657)", () => {
  assert.equal(CREDITS_PER_MINUTE, 1.67);
});

for (const path of [ADMIN_PAGE, DASHBOARD_ROUTER]) {
  test(`${path} uses the shared rate and declares none of its own`, () => {
    const text = source(path);
    assert.ok(text.includes(IMPORT_LINE), `${path} must import the shared rate`);
    assert.doesNotMatch(text, /\bCREDITS_PER_MINUTE\s*=/);
    assert.doesNotMatch(text, /\b6\.3\b/);
  });
}

test("every minutes figure on the admin page divides by the shared rate", () => {
  const text = source(ADMIN_PAGE);
  const divisions = text.match(/\/ CREDITS_PER_MINUTE\)/g) ?? [];
  // The row's "(… min)", the dialog's current balance, and the dialog's
  // "= … minutes of transcription" preview.
  assert.equal(divisions.length, 3);
});

test("the cloud's CREDITS_PER_MINUTE equals the portal's", () => {
  const match = source(CLOUD_CONSTANTS).match(
    /export const CREDITS_PER_MINUTE = ([\d.]+);/,
  );
  assert.ok(match, "the cloud constant was not found");
  assert.equal(Number(match[1]), CREDITS_PER_MINUTE);
});
