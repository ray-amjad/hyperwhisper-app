export const MAX_CREDIT_DEDUCTION_AMOUNT = 1_000_000;

// credit_balances.balance is numeric(_, 2); amounts with finer precision than
// the column scale would be silently rounded by Postgres on write, so a
// sub-cent deduction (e.g. 0.005) can net a zero deduction. Reject anything
// that does not round-trip at 2 decimal places.
export const CREDIT_BALANCE_SCALE = 2;

export function validateCreditDeductionAmount(amount: unknown): string | null {
  if (typeof amount !== "number" || !Number.isFinite(amount) || amount <= 0) {
    return "amount must be a finite positive number";
  }

  if (amount > MAX_CREDIT_DEDUCTION_AMOUNT) {
    return `amount must be ${MAX_CREDIT_DEDUCTION_AMOUNT} or less`;
  }

  if (Number(amount.toFixed(CREDIT_BALANCE_SCALE)) !== amount) {
    return `amount must have at most ${CREDIT_BALANCE_SCALE} decimal places`;
  }

  return null;
}
