/**
 * #1663: credits are pooled per account, so the admin Customers page shows
 * the balance once (Total) and never on each license row, and a row's status
 * pill reads as a word, not the raw account_keys.status enum.
 */
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";

const ADMIN_PAGE =
  "app/[locale]/user/(authenticated)/customers/CustomersClient.tsx";

const text = readFileSync(ADMIN_PAGE, "utf8").replace(/\r\n/g, "\n");

test("no license row renders its (pooled) balance", () => {
  assert.doesNotMatch(text, /formatCredits\(license\.credits\)/);
  assert.doesNotMatch(text, /license\.credits \/ CREDITS_PER_MINUTE/);
});

test("Total renders the pooled balance with its minutes", () => {
  assert.match(text, /formatCredits\(customer\.totalCredits\)/);
  assert.match(text, /customer\.totalCredits \/ CREDITS_PER_MINUTE/);
});

test("the status pill maps the enum to a label", () => {
  assert.doesNotMatch(text, /\{license\.status\}/);
  assert.match(text, /licenseStatusLabel\(license\.status\)/);
  assert.match(text, /status === "granted"\) return "Active"/);
  assert.match(text, /status === "revoked"\) return "Revoked"/);
});

test("Add credits shows once per account, beside Total, not per key", () => {
  const rows = text.slice(
    text.indexOf("customer.licenses.map((license) =>"),
    text.indexOf("{/* Total credits"),
  );
  assert.ok(rows.length > 0);
  assert.doesNotMatch(rows, /openAddCredits\(/);
  assert.equal(text.match(/openAddCredits\(\{/g)?.length, 1);
  assert.match(text, /credits: customer\.totalCredits,/);
  assert.match(text, /grid-cols-\[repeat\(3,max-content\)\]/);
});
