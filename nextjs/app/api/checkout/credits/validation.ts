/**
 * Pure validation + pricing helpers for credit purchases.
 *
 * Kept free of Next/Stripe imports so they can be unit-tested directly (see
 * tests/credit-purchase-validation.test.ts) and reused by the checkout route.
 */

export const MIN_CREDIT_DOLLARS = 5;
export const MAX_CREDIT_DOLLARS = 500;
/** 1,000 credits per US$1. */
export const CREDITS_PER_DOLLAR = 1000;
/** Non-refundable processing fee added on top of the credit value. */
export const CREDIT_FEE_RATE = 0.06;

/**
 * Validate the requested purchase amount (whole US dollars, MIN..MAX inclusive).
 * Returns an error string, or null when valid.
 */
export function validateCreditPurchaseAmount(amount: unknown): string | null {
  if (typeof amount !== "number" || !Number.isFinite(amount)) {
    return "amount must be a finite number";
  }
  if (!Number.isInteger(amount)) {
    return "amount must be a whole number of dollars";
  }
  if (amount < MIN_CREDIT_DOLLARS || amount > MAX_CREDIT_DOLLARS) {
    return `amount must be between ${MIN_CREDIT_DOLLARS} and ${MAX_CREDIT_DOLLARS} dollars`;
  }
  return null;
}

export interface CreditPurchaseBreakdown {
  /** Credits granted: amount * CREDITS_PER_DOLLAR. */
  creditAmount: number;
  /** Credit line-item amount in cents: amount * 100. */
  creditCents: number;
  /** Processing fee in cents: round(creditCents * CREDIT_FEE_RATE). */
  feeCents: number;
}

/**
 * Compute the Stripe line-item breakdown for a validated dollar amount.
 * Caller must pass an amount that already passed validateCreditPurchaseAmount.
 */
export function computeCreditPurchase(amount: number): CreditPurchaseBreakdown {
  const creditCents = amount * 100;
  return {
    creditAmount: amount * CREDITS_PER_DOLLAR,
    creditCents,
    feeCents: Math.round(creditCents * CREDIT_FEE_RATE),
  };
}
