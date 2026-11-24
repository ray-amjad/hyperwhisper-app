import type Stripe from "stripe";

const STRIPE_SPEND_FETCH_CONCURRENCY = 10;

export type CustomerSpendCharge = Pick<
  Stripe.Charge,
  "id" | "status" | "disputed" | "amount" | "amount_refunded"
> &
  // Only for the per-charge breakdown; the net spend never reads them.
  Partial<Pick<Stripe.Charge, "created" | "description">>;

/**
 * One charge as the net spend saw it. `countedCents` is what it adds to the
 * total; `excluded` says why a charge adds nothing.
 */
export interface CustomerSpendChargeLine {
  id: string;
  createdAt: string | null;
  description: string | null;
  amountCents: number;
  refundedCents: number;
  countedCents: number;
  excluded?: "not_succeeded" | "dispute_not_won";
}

export interface CustomerSpendBreakdown {
  netCents: number;
  charges: CustomerSpendChargeLine[];
}

export interface CustomerSpendSource {
  listCharges: (
    stripeCustomerId: string,
  ) => Promise<readonly CustomerSpendCharge[]>;
  getLatestDisputeStatus: (
    chargeId: string,
  ) => Promise<Stripe.Dispute.Status | null>;
}

export interface CustomerSpendReader {
  /** The net spend and the charges behind it; null when Stripe failed. */
  getSpendBreakdownForStripeCustomer: (
    stripeCustomerId: string,
  ) => Promise<CustomerSpendBreakdown | null>;
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
  // The ONE definition of a customer's net spend: succeeded charges, less
  // what was refunded, and a disputed charge only when Stripe ruled for us.
  // The admin Customers page and the admin MCP `find_customer` both read it.
  async function getSpendBreakdownForStripeCustomer(
    stripeCustomerId: string,
  ): Promise<CustomerSpendBreakdown | null> {
    try {
      const charges = await source.listCharges(stripeCustomerId);
      const lines: CustomerSpendChargeLine[] = [];
      let netCents = 0;

      for (const charge of charges) {
        const line: CustomerSpendChargeLine = {
          id: charge.id,
          createdAt:
            typeof charge.created === "number"
              ? new Date(charge.created * 1000).toISOString()
              : null,
          description: charge.description ?? null,
          amountCents: charge.amount,
          refundedCents: charge.amount_refunded,
          countedCents: 0,
        };

        lines.push(line);
        if (charge.status !== "succeeded") {
          line.excluded = "not_succeeded";
          continue;
        }

        if (charge.disputed) {
          // `disputed` stays true after a dispute resolves. Count the charge
          // only when the latest dispute confirms that Stripe returned it.
          const disputeStatus = await source.getLatestDisputeStatus(charge.id);

          if (disputeStatus !== "won") {
            line.excluded = "dispute_not_won";
            continue;
          }
        }

        line.countedCents = charge.amount - charge.amount_refunded;
        netCents += line.countedCents;
      }

      return { netCents, charges: lines };
    } catch (error) {
      console.error(
        `Failed to fetch Stripe charges for customer ${stripeCustomerId}:`,
        error,
      );

      return null;
    }
  }

  async function getNetSpendCentsForStripeCustomer(
    stripeCustomerId: string,
  ): Promise<number | null> {
    const breakdown =
      await getSpendBreakdownForStripeCustomer(stripeCustomerId);

    return breakdown ? breakdown.netCents : null;
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
    getSpendBreakdownForStripeCustomer,
    getNetSpendCentsForStripeCustomer,
    getNetSpendCentsForStripeCustomers,
  };
}

/**
 * One account's total spend, as the admin Customers page shows it: the sum of
 * the net spend of every distinct Stripe customer id on the account's
 * licenses. Null when ANY of them failed to read (a possibly-too-low total is
 * worse than "unknown"). An account with no Stripe customer id is 0; a caller
 * that must tell that apart from "$0 spent" checks `stripeCustomerIds` itself.
 */
export function accountNetSpendCents(
  stripeCustomerIds: Iterable<string>,
  spendByStripeCustomerId: ReadonlyMap<string, number | null>,
): number | null {
  let total = 0;

  for (const id of Array.from(stripeCustomerIds)) {
    const cents = spendByStripeCustomerId.get(id);

    if (cents === null || cents === undefined) return null;
    total += cents;
  }

  return total;
}
