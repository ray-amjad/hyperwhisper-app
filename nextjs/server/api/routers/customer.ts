/**
 * Customer Router
 *
 * Provides authenticated customer endpoints for the customer portal.
 * All procedures require authentication (protectedProcedure).
 *
 * PROCEDURES:
 * - credits: Get total credit balance for logged-in user
 * - billingProviders: Check which billing providers the user has history with
 * - stripePortalUrl: Generate Stripe billing portal session URL
 */
import { TRPCError } from "@trpc/server";

import { createTRPCRouter, protectedProcedure } from "../trpc";
import {
  getLicensesByEmail,
  getCreditBalancesForLicenses,
  getPaidCreditGrantsForLicenses,
} from "@/src/lib/db-layer";
import { stripe } from "@/lib/clients/stripe";

// Credits per minute for the default HyperWhisper Cloud STT route.
// 1 credit = $0.001; xAI Grok STT batch is $0.10/hour = 1.6667 credits/min.
const CREDITS_PER_MINUTE = 1.67;

export const customerRouter = createTRPCRouter({
  /**
   * Get all licenses with their individual credit balances.
   */
  licensesWithCredits: protectedProcedure.query(async ({ ctx }) => {
    const userEmail = ctx.user.email?.toLowerCase();

    if (!userEmail) {
      throw new TRPCError({
        code: "BAD_REQUEST",
        message: "User email not found",
      });
    }

    const licenses = await getLicensesByEmail(userEmail);

    if (licenses.length === 0) {
      return {
        licenses: [],
        creditsPerMinute: CREDITS_PER_MINUTE,
      };
    }

    const licenseIds = licenses.map((l) => l.id);
    const balanceMap = await getCreditBalancesForLicenses(licenseIds);

    const licensesWithCredits = licenses.map((license) => {
      const credits = balanceMap.get(license.id) || 0;
      return {
        id: license.id,
        key: license.key,
        status: license.status,
        credits,
        minutesRemaining: Math.floor(credits / CREDITS_PER_MINUTE),
        createdAt: license.createdAt.toISOString(),
        stripeCustomerId: license.stripeCustomerId,
        polarCustomerId: license.polarCustomerId,
      };
    });

    return {
      licenses: licensesWithCredits,
      creditsPerMinute: CREDITS_PER_MINUTE,
    };
  }),

  /**
   * Get credit balance for the authenticated user.
   */
  credits: protectedProcedure.query(async ({ ctx }) => {
    const userEmail = ctx.user.email?.toLowerCase();

    if (!userEmail) {
      throw new TRPCError({
        code: "BAD_REQUEST",
        message: "User email not found",
      });
    }

    const licenses = await getLicensesByEmail(userEmail);

    if (licenses.length === 0) {
      return {
        totalCredits: 0,
        minutesRemaining: 0,
        creditsPerMinute: CREDITS_PER_MINUTE,
      };
    }

    const licenseIds = licenses.map((l) => l.id);
    const balanceMap = await getCreditBalancesForLicenses(licenseIds);

    let totalCredits = 0;
    for (const balance of Array.from(balanceMap.values())) {
      totalCredits += balance;
    }

    const minutesRemaining = Math.floor(totalCredits / CREDITS_PER_MINUTE);

    return {
      totalCredits,
      minutesRemaining,
      creditsPerMinute: CREDITS_PER_MINUTE,
    };
  }),

  /**
   * Paid credit-pack top-up history for the authenticated user.
   *
   * Returns the user's PAID credit grants (source_type = 'stripe_credit_pack')
   * newest-first, including expired ones so the dashboard can show their status.
   * Free/included bundles and admin/legacy grants are intentionally excluded.
   */
  creditHistory: protectedProcedure.query(async ({ ctx }) => {
    const userEmail = ctx.user.email?.toLowerCase();

    if (!userEmail) {
      throw new TRPCError({
        code: "BAD_REQUEST",
        message: "User email not found",
      });
    }

    const licenses = await getLicensesByEmail(userEmail);
    if (licenses.length === 0) {
      return { grants: [] };
    }

    const grants = await getPaidCreditGrantsForLicenses(
      licenses.map((l) => l.id)
    );

    const now = Date.now();
    return {
      grants: grants.map((g) => ({
        id: g.id,
        createdAt: g.createdAt.toISOString(),
        expiresAt: g.expiresAt ? g.expiresAt.toISOString() : null,
        originalAmount: g.originalAmount,
        remainingAmount: g.remainingAmount,
        // Surface expiry as a derived flag so the client doesn't re-parse dates;
        // a grant is expired once its expiresAt has passed, regardless of the
        // stored status (enforcement is a lazy filter, not a cron).
        expired: g.expiresAt ? g.expiresAt.getTime() <= now : false,
        status: g.status,
      })),
    };
  }),

  /**
   * Check which billing providers the user has history with.
   * Queries license_keys to determine billing provider history.
   */
  billingProviders: protectedProcedure.query(async ({ ctx }) => {
    const userEmail = ctx.user.email?.toLowerCase();
    if (!userEmail) return { hasStripe: false, hasPolar: false };
    const licenses = await getLicensesByEmail(userEmail);
    return {
      hasStripe: licenses.some((l) => !!l.stripeCustomerId),
      hasPolar: licenses.some((l) => !!l.polarCustomerId),
    };
  }),

  /**
   * Generate Stripe billing portal session URL.
   */
  stripePortalUrl: protectedProcedure.mutation(async ({ ctx }) => {
    const userEmail = ctx.user.email?.toLowerCase();

    if (!userEmail) {
      throw new TRPCError({
        code: "BAD_REQUEST",
        message: "User email not found",
      });
    }

    // Find license with Stripe customer ID
    const licenses = await getLicensesByEmail(userEmail);
    const licenseWithStripe = licenses.find((l) => l.stripeCustomerId);

    if (!licenseWithStripe?.stripeCustomerId) {
      throw new TRPCError({
        code: "NOT_FOUND",
        message: "No Stripe billing history found",
      });
    }

    const siteUrl =
      process.env.NEXT_PUBLIC_SITE_URL || "https://hyperwhisper.com";

    // Create Stripe billing portal session
    const session = await stripe.billingPortal.sessions.create({
      customer: licenseWithStripe.stripeCustomerId,
      return_url: `${siteUrl}/user`,
    });

    return { url: session.url };
  }),
});
