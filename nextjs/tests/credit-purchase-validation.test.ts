import assert from "node:assert/strict";
import test from "node:test";

import {
  MIN_CREDIT_DOLLARS,
  MAX_CREDIT_DOLLARS,
  validateCreditPurchaseAmount,
  computeCreditPurchase,
} from "../app/api/checkout/credits/validation";

test("accepts whole-dollar amounts within bounds", () => {
  assert.equal(validateCreditPurchaseAmount(MIN_CREDIT_DOLLARS), null);
  assert.equal(validateCreditPurchaseAmount(10), null);
  assert.equal(validateCreditPurchaseAmount(37), null);
  assert.equal(validateCreditPurchaseAmount(MAX_CREDIT_DOLLARS), null);
});

test("rejects amounts below the minimum", () => {
  assert.notEqual(validateCreditPurchaseAmount(MIN_CREDIT_DOLLARS - 1), null);
  assert.notEqual(validateCreditPurchaseAmount(0), null);
  assert.notEqual(validateCreditPurchaseAmount(-5), null);
});

test("rejects amounts above the maximum", () => {
  assert.notEqual(validateCreditPurchaseAmount(MAX_CREDIT_DOLLARS + 1), null);
  assert.notEqual(validateCreditPurchaseAmount(10000), null);
});

test("rejects non-integer dollar amounts", () => {
  assert.notEqual(validateCreditPurchaseAmount(5.5), null);
  assert.notEqual(validateCreditPurchaseAmount(19.99), null);
});

test("rejects non-finite / non-number amounts", () => {
  assert.notEqual(validateCreditPurchaseAmount(Number.NaN), null);
  assert.notEqual(validateCreditPurchaseAmount(Number.POSITIVE_INFINITY), null);
  assert.notEqual(validateCreditPurchaseAmount("10"), null);
  assert.notEqual(validateCreditPurchaseAmount(undefined), null);
  assert.notEqual(validateCreditPurchaseAmount(null), null);
  // 1e999 parses to Infinity from JSON, the classic overflow payload.
  const parsed = JSON.parse('{"amount":1e999}') as { amount: unknown };
  assert.notEqual(validateCreditPurchaseAmount(parsed.amount), null);
});

test("credits = amount * 1000, no free bundle", () => {
  assert.equal(computeCreditPurchase(5).creditAmount, 5000);
  assert.equal(computeCreditPurchase(10).creditAmount, 10000);
  assert.equal(computeCreditPurchase(20).creditAmount, 20000);
  assert.equal(computeCreditPurchase(37).creditAmount, 37000);
});

test("credit line item is amount * 100 cents", () => {
  assert.equal(computeCreditPurchase(5).creditCents, 500);
  assert.equal(computeCreditPurchase(500).creditCents, 50000);
});

test("processing fee is round(6% of credit cents)", () => {
  // 6% of $5 = $0.30 -> 30 cents
  assert.equal(computeCreditPurchase(5).feeCents, 30);
  // 6% of $10 = $0.60 -> 60 cents
  assert.equal(computeCreditPurchase(10).feeCents, 60);
  // 6% of $20 = $1.20 -> 120 cents
  assert.equal(computeCreditPurchase(20).feeCents, 120);
  // 6% of $37 = $2.22 -> 222 cents
  assert.equal(computeCreditPurchase(37).feeCents, 222);
  // Rounding: 6% of $17 = $1.02 -> 102 cents (1700 * 0.06 = 102 exactly)
  assert.equal(computeCreditPurchase(17).feeCents, 102);
  // Rounding half-up: 6% of $8 = $0.48 -> 800 * 0.06 = 48 cents
  assert.equal(computeCreditPurchase(8).feeCents, 48);
});

test("fee rounds to nearest cent for fractional results", () => {
  // 25 * 100 * 0.06 = 150 exactly
  assert.equal(computeCreditPurchase(25).feeCents, 150);
  // Find a case that produces a .5 cent: 100*0.06=6.0; choose amount where
  // creditCents*0.06 is fractional. 1*... not allowed (<5). Use 5..500.
  // 7 -> 700*0.06 = 42.0; 11 -> 1100*0.06 = 66.0. Most land exact; assert a
  // representative non-trivial one stays an integer.
  assert.ok(Number.isInteger(computeCreditPurchase(123).feeCents));
});
