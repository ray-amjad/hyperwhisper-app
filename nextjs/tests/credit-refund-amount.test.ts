/**
 * `refundedCreditsTotal` in `lib/services/credit-refund.ts`: how many credits
 * a Stripe refund of a credit pack is worth, at the price the buyer paid
 * (#1351, Ray 2026-10-07). Pure arithmetic, no Stripe and no database.
 */
import assert from "node:assert/strict";
import test from "node:test";

import { refundedCreditsTotal } from "@/lib/services/credit-refund";

// A $5 pack: 5,000 credits, $5.00 credit line, $0.30 fee line.
const pack = { grantCredits: 5000, listCreditCents: 500, listFeeCents: 30 };

test("the issue's case: $4.50 of a $5.30 charge is 4,500 credits", () => {
  assert.equal(refundedCreditsTotal({ ...pack, amountTotal: 530, amountTax: 0, amountRefunded: 450 }), 4500);
});

test("Ray's promo example: $2.00 of a pack bought for $4.00 is 2,500 credits", () => {
  // 20% off both lines: $4.00 + $0.24 = $4.24 charged.
  assert.equal(refundedCreditsTotal({ ...pack, amountTotal: 424, amountTax: 0, amountRefunded: 200 }), 2500);
  assert.equal(refundedCreditsTotal({ ...pack, amountTotal: 424, amountTax: 0, amountRefunded: 400 }), 5000);
});

test("tax is excluded, whether added on top or included in the price", () => {
  // Exclusive 10%: $5.83 charged; $4.50 + its $0.45 tax refunded.
  assert.equal(refundedCreditsTotal({ ...pack, amountTotal: 583, amountTax: 53, amountRefunded: 495 }), 4500);
  // Inclusive: $5.30 charged, $0.48 of it tax.
  assert.equal(refundedCreditsTotal({ ...pack, amountTotal: 530, amountTax: 48, amountRefunded: 450 }), 4500);
});

test("never more than the grant, even when the fee is refunded too", () => {
  assert.equal(refundedCreditsTotal({ ...pack, amountTotal: 530, amountTax: 0, amountRefunded: 500 }), 5000);
  assert.equal(refundedCreditsTotal({ ...pack, amountTotal: 530, amountTax: 0, amountRefunded: 530 }), 5000);
  assert.equal(refundedCreditsTotal({ ...pack, amountTotal: 530, amountTax: 0, amountRefunded: 10_000 }), 5000);
});

test("rounds to the nearest whole credit", () => {
  // 1 cent of a $5.30 charge is 10 credits; 1 cent of a $4.24 charge is 12.5.
  assert.equal(refundedCreditsTotal({ ...pack, amountTotal: 530, amountTax: 0, amountRefunded: 1 }), 10);
  assert.equal(refundedCreditsTotal({ ...pack, amountTotal: 424, amountTax: 0, amountRefunded: 1 }), 13);
  assert.equal(refundedCreditsTotal({ ...pack, amountTotal: 424, amountTax: 0, amountRefunded: 3 }), 38);
});

test("no fee line: every paid cent is credit value", () => {
  assert.equal(
    refundedCreditsTotal({ grantCredits: 1000, listCreditCents: 100, listFeeCents: 0, amountTotal: 100, amountTax: 0, amountRefunded: 45 }),
    450,
  );
});

test("nonsense input is worth no credits", () => {
  const base = { ...pack, amountTotal: 530, amountTax: 0, amountRefunded: 450 };
  assert.equal(refundedCreditsTotal({ ...base, amountRefunded: 0 }), 0);
  assert.equal(refundedCreditsTotal({ ...base, amountRefunded: -5 }), 0);
  assert.equal(refundedCreditsTotal({ ...base, amountTotal: 0 }), 0);
  assert.equal(refundedCreditsTotal({ ...base, grantCredits: 0 }), 0);
  assert.equal(refundedCreditsTotal({ ...base, listCreditCents: 0 }), 0);
  assert.equal(refundedCreditsTotal({ ...base, amountRefunded: Number.NaN }), 0);
  assert.equal(refundedCreditsTotal({ ...base, amountTax: 530 }), 0);
});
