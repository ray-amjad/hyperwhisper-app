/**
 * How many credits a Stripe refund of a credit-pack purchase removes (#1351).
 *
 * The Refund Policy refunds unused credits at the price the buyer PAID, not at
 * list price (Ray, 2026-10-07):
 *
 *   credits removed = refunded credit cents x grant credits / paid credit cents
 *
 * Both cent figures exclude sales tax and the non-refundable processing fee,
 * and the paid figure is after any promotion-code discount. Example: 5,000
 * credits bought for $4.00 with a 20% promo, $2.00 refunded -> 2,500 credits.
 *
 * Kept free of Stripe and database imports so it is unit-tested directly.
 */

export interface CreditRefundInput {
  /** Credits the purchase granted (`credit_amount` metadata). */
  grantCredits: number;
  /** List price of the credit line item, in cents, before any discount. */
  listCreditCents: number;
  /** List price of the processing-fee line item, in cents (0 when absent). */
  listFeeCents: number;
  /** What the buyer was charged, in cents: discount applied, tax included. */
  amountTotal: number;
  /** Sales tax inside `amountTotal`, in cents (exclusive or inclusive tax). */
  amountTax: number;
  /** Stripe's `charge.amount_refunded`: cumulative across every refund. */
  amountRefunded: number;
}

/**
 * The TOTAL credits refunded so far for one credit-pack purchase, from the
 * cumulative refunded amount. Never more than the grant; never negative.
 *
 * The steps, all in cents:
 * - paid ex tax = amountTotal - amountTax. This is already after the promo.
 * - paid credit cents = paid ex tax x listCredit / (listCredit + listFee):
 *   a whole-order promo is spread over the two line items in proportion to
 *   their list prices, so the credit line keeps its list share.
 * - refunded ex tax = amountRefunded x paid ex tax / amountTotal: a refund
 *   carries its proportional share of tax.
 * - refunded credit cents = min(refunded ex tax, paid credit cents): the fee is
 *   non-refundable, so a refunded cent is credit value first, up to what was
 *   paid for credits.
 * - credits = refunded credit cents x grantCredits / paid credit cents.
 *
 * The tax ratio cancels out of the uncapped case, so it is computed as one
 * integer fraction and rounded to the nearest credit once, at the end.
 */
export function refundedCreditsTotal(input: CreditRefundInput): number {
  const {
    grantCredits,
    listCreditCents,
    listFeeCents,
    amountTotal,
    amountTax,
    amountRefunded,
  } = input;

  if (
    !(grantCredits > 0) ||
    !(listCreditCents > 0) ||
    !(amountTotal > 0) ||
    !(amountRefunded > 0)
  ) {
    return 0;
  }

  const listTotal = listCreditCents + Math.max(0, listFeeCents);
  const paidExTax = amountTotal - Math.max(0, amountTax);

  if (!(paidExTax > 0)) return 0;

  // refunded ex tax >= paid credit cents
  //   <=> amountRefunded * paidExTax / amountTotal >= paidExTax * listCredit / listTotal
  //   <=> amountRefunded * listTotal >= amountTotal * listCredit
  if (amountRefunded * listTotal >= amountTotal * listCreditCents) {
    return grantCredits;
  }

  const credits = Math.round(
    (amountRefunded * listTotal * grantCredits) /
      (amountTotal * listCreditCents),
  );

  return Math.min(grantCredits, Math.max(0, credits));
}
