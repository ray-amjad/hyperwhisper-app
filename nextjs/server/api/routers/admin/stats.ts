/**
 * Admin Stats Router
 *
 * Dashboard statistics from Stripe and Polar.
 * All procedures require admin authentication.
 *
 * PROCEDURES:
 * - get: Returns customer counts from Stripe and Polar
 *
 * INTEGRATIONS:
 * - Stripe: Customer count
 * - Polar: Customer count
 */
import { TRPCError } from "@trpc/server";

import { createTRPCRouter, adminProcedure } from "../../trpc";
import { polarClient, POLAR_ORGANIZATION_ID } from "@/lib/clients/polar";
import { stripe } from "@/lib/clients/stripe";

export const statsRouter = createTRPCRouter({
  /**
   * Get dashboard statistics.
   *
   * FETCHES:
   * 1. Stripe customer count (paginated, up to 100)
   * 2. Polar customer count (paginated)
   *
   * @returns { totalCustomers, totalCreditsUsed, polarCustomers, stripeCustomers }
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

      // Fetch Polar customers count
      let polarCustomers = 0;
      try {
        const polarResult = await polarClient.customers.list({
          organizationId: POLAR_ORGANIZATION_ID,
          limit: 100,
        });

        for await (const page of polarResult) {
          if (page && "items" in page) {
            polarCustomers += (page as { items: unknown[] }).items.length;
          } else if (page && "id" in page) {
            polarCustomers++;
          }
        }
      } catch {
        // Polar not configured
      }

      // Calculate total credits used
      // Note: Stripe's listEventSummaries requires a customer parameter,
      // so we'd need to sum across all customers. For now, return 0.
      const totalCreditsUsed = 0;

      return {
        totalCustomers: stripeCustomers + polarCustomers,
        totalCreditsUsed,
        polarCustomers,
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
