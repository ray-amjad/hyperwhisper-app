/**
 * The Stripe-backed customer spend reader, shared by the admin Customers page
 * (`customers.ts`) and the admin MCP `find_customer` tool
 * (`server/admin-mcp/tools.ts`), so both show the same "Total spend".
 */
import { createCustomerSpendReader } from "./customer-spend";

import { stripe } from "@/lib/clients/stripe";

export const customerSpendReader = createCustomerSpendReader({
  listCharges: async (stripeCustomerId) =>
    stripe.charges
      .list({ customer: stripeCustomerId, limit: 100 })
      .autoPagingToArray({ limit: 10_000 }),
  getLatestDisputeStatus: async (chargeId) => {
    const disputes = await stripe.disputes.list({ charge: chargeId, limit: 1 });

    return disputes.data[0]?.status ?? null;
  },
});
