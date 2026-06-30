import assert from "node:assert/strict";
import test from "node:test";

import {
  MAX_CREDIT_DEDUCTION_AMOUNT,
  validateCreditDeductionAmount,
} from "../app/api/license/credits/validation";

test("accepts finite positive deduction amounts within the limit", () => {
  assert.equal(validateCreditDeductionAmount(1), null);
  assert.equal(validateCreditDeductionAmount(150.5), null);
  assert.equal(validateCreditDeductionAmount(MAX_CREDIT_DEDUCTION_AMOUNT), null);
});

test("rejects non-finite deduction amounts", () => {
  const parsedPayload = JSON.parse('{"amount":1e999}') as { amount: unknown };

  assert.equal(
    validateCreditDeductionAmount(parsedPayload.amount),
    "amount must be a finite positive number"
  );
  assert.equal(
    validateCreditDeductionAmount(Number.POSITIVE_INFINITY),
    "amount must be a finite positive number"
  );
  assert.equal(
    validateCreditDeductionAmount(Number.NEGATIVE_INFINITY),
    "amount must be a finite positive number"
  );
  assert.equal(
    validateCreditDeductionAmount(Number.NaN),
    "amount must be a finite positive number"
  );
});

test("rejects non-positive deduction amounts", () => {
  assert.equal(
    validateCreditDeductionAmount(0),
    "amount must be a finite positive number"
  );
  assert.equal(
    validateCreditDeductionAmount(-1),
    "amount must be a finite positive number"
  );
});

test("rejects deduction amounts above the request limit", () => {
  assert.equal(
    validateCreditDeductionAmount(MAX_CREDIT_DEDUCTION_AMOUNT + 1),
    `amount must be ${MAX_CREDIT_DEDUCTION_AMOUNT} or less`
  );
});

test("rejects deduction amounts with sub-scale precision", () => {
  // numeric(_, 2) would silently round these on write, netting a smaller (or
  // zero) deduction than requested.
  assert.equal(
    validateCreditDeductionAmount(0.005),
    "amount must have at most 2 decimal places"
  );
  assert.equal(
    validateCreditDeductionAmount(12.345),
    "amount must have at most 2 decimal places"
  );
});

test("accepts deduction amounts at or above 2-decimal scale", () => {
  // The legitimate Fly caller sends 0.1-granularity amounts; these and any
  // value with <= 2 decimals must round-trip exactly.
  assert.equal(validateCreditDeductionAmount(0.1), null);
  assert.equal(validateCreditDeductionAmount(0.01), null);
  assert.equal(validateCreditDeductionAmount(0.07), null);
  assert.equal(validateCreditDeductionAmount(12.34), null);
});
