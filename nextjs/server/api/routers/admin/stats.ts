/**
 * Admin Stats Router
 *
 * Dashboard statistics from Stripe.
 * All procedures require admin authentication.
 *
 * PROCEDURES:
 * - get: Returns customer counts from Stripe
 *
 * INTEGRATIONS:
 * - Stripe: Customer count
 */
import { TRPCError } from "@trpc/server";

import { createTRPCRouter, adminProcedure } from "../../trpc";
import { stripe } from "@/lib/clients/stripe";

export const statsRouter = createTRPCRouter({
  /**
   * Get dashboard statistics.
   *
   * FETCHES:
   * 1. Stripe customer count (paginated, up to 100)
   *
   * @returns { totalCustomers, totalCreditsUsed, stripeCustomers }
   */
  get: adminProcedure.query(async () => {
    try {
      // Fetch Stripe customers count
      let stripeCustomers = 0;
      try {
        const allCustomers = await stripe.customers.list({ limit: 100 });
        stripeCustomers = allCustomers.data.length;
      } catch {
        // Stripe not configured
      }

      // Calculate total credits used
      // Note: Stripe's listEventSummaries requires a customer parameter,
      // so we'd need to sum across all customers. For now, return 0.
      const totalCreditsUsed = 0;

      return {
        totalCustomers: stripeCustomers,
        totalCreditsUsed,
        stripeCustomers,
      };
    } catch (error) {
      console.error("Stats fetch error:", error);
      throw new TRPCError({
        code: "INTERNAL_SERVER_ERROR",
        message:
          error instanceof Error ? error.message : "Failed to fetch stats",
      });
    }
  }),
});
