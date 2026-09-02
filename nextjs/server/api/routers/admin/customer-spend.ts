import type Stripe from "stripe";

const STRIPE_SPEND_FETCH_CONCURRENCY = 10;

export type CustomerSpendCharge = Pick<
  Stripe.Charge,
  "id" | "status" | "disputed" | "amount" | "amount_refunded"
>;

export interface CustomerSpendSource {
  listCharges: (
    stripeCustomerId: string,
  ) => Promise<readonly CustomerSpendCharge[]>;
  getLatestDisputeStatus: (
    chargeId: string,
  ) => Promise<Stripe.Dispute.Status | null>;
}

export interface CustomerSpendReader {
  getNetSpendCentsForStripeCustomer: (
    stripeCustomerId: string,
  ) => Promise<number | null>;
  getNetSpendCentsForStripeCustomers: (
    stripeCustomerIds: string[],
  ) => Promise<Map<string, number | null>>;
}

/**
 * Builds a net-spend reader around the source used to fetch charges and
 * disputes. A source failure affects only the customer whose spend was read.
 */
export function createCustomerSpendReader(
  source: CustomerSpendSource,
): CustomerSpendReader {
  async function getNetSpendCentsForStripeCustomer(
    stripeCustomerId: string,
  ): Promise<number | null> {
    try {
      const charges = await source.listCharges(stripeCustomerId);
      let totalCents = 0;

      for (const charge of charges) {
        if (charge.status !== "succeeded") continue;

        if (charge.disputed) {
          // `disputed` stays true after a dispute resolves. Count the charge
          // only when the latest dispute confirms that Stripe returned it.
          const disputeStatus = await source.getLatestDisputeStatus(charge.id);

          if (disputeStatus !== "won") continue;
        }

        totalCents += charge.amount - charge.amount_refunded;
      }

      return totalCents;
    } catch (error) {
      console.error(
        `Failed to fetch Stripe charges for customer ${stripeCustomerId}:`,
        error,
      );
      return null;
    }
  }

  async function getNetSpendCentsForStripeCustomers(
    stripeCustomerIds: string[],
  ): Promise<Map<string, number | null>> {
    const results = new Map<string, number | null>();

    for (
      let i = 0;
      i < stripeCustomerIds.length;
      i += STRIPE_SPEND_FETCH_CONCURRENCY
    ) {
      const chunk = stripeCustomerIds.slice(
        i,
        i + STRIPE_SPEND_FETCH_CONCURRENCY,
      );
      const chunkResults = await Promise.all(
        chunk.map(
          async (id) =>
            [id, await getNetSpendCentsForStripeCustomer(id)] as const,
        ),
      );

      for (const [id, cents] of chunkResults) {
        results.set(id, cents);
      }
    }

    return results;
  }

  return {
    getNetSpendCentsForStripeCustomer,
    getNetSpendCentsForStripeCustomers,
  };
}
