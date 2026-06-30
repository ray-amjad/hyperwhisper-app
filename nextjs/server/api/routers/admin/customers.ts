/**
 * Admin Customers Router
 *
 * Customer management for the admin dashboard.
 * All procedures require admin authentication.
 */
import { z } from "zod";
import { TRPCError } from "@trpc/server";
import Stripe from "stripe";

import { createTRPCRouter, adminProcedure } from "../../trpc";
import {
  getAllAccountKeysWithCreditsForAdmin,
  searchAccountKeysByEmail,
  getOrCreateUser,
  insertAccountKey,
  findAccountByKey,
  findAccountById,
  getCreditBalance,
  grantCreditLot,
  refundCreditGrant,
  updateAccountKey,
  getAccountKeysWithCreditsForUserIds,
  getUserById,
  getUserByEmail,
  getUsersByIds,
  updateCustomerEmail,
} from "@/src/lib/db-layer";
import { generateLicenseKey } from "@/lib/services/license-key";
import { emailService } from "@/lib/services/email";

const stripe = new Stripe(process.env.STRIPE_SECRET_KEY!, {
  apiVersion: "2025-02-24.acacia" as any,
});

const MAX_ADMIN_CREDIT_GRANT = 1_000_000;

export const customersRouter = createTRPCRouter({
  /**
   * List customers, one row per customer (grouped by user), each carrying all
   * of that customer's license keys. Supports optional email search filter.
   *
   * A search matches individual license rows, but we then expand to each
   * matched customer's FULL license set (via their userId) so the license
   * count, total credits, and "moves all N licenses" copy reflect the true
   * totals — not just the licenses whose email matched the search term.
   */
  list: adminProcedure
    .input(z.object({ search: z.string().optional() }).optional())
    .query(async ({ input }) => {
      try {
        const search = input?.search?.trim();
        const matched = search
          ? await searchAccountKeysByEmail(search)
          : await getAllAccountKeysWithCreditsForAdmin(1000);

        // Expand search matches to each customer's full license set so counts
        // and credits are true totals. The no-search path already has every
        // license. Then pull canonical user.email for display.
        const userIds = Array.from(new Set(matched.map((l) => l.userId)));
        const licenses = search
          ? await getAccountKeysWithCreditsForUserIds(userIds)
          : matched;
        const userMap = await getUsersByIds(userIds);

        // Group licenses by their owning user. The source rows are ordered
        // newest-first, so Map insertion order keeps customers in that order.
        const customerMap = new Map<
          string,
          {
            userId: string;
            email: string;
            licenseCount: number;
            totalCredits: number;
            created: number;
            licenses: Array<{
              id: string;
              key: string;
              status: string;
              credits: number;
              stripeSessionId: string | null;
              stripeCustomerId: string | null;
              created: number;
            }>;
          }
        >();

        for (const l of licenses) {
          const created = Math.floor(l.createdAt.getTime() / 1000);
          let customer = customerMap.get(l.userId);
          if (!customer) {
            customer = {
              userId: l.userId,
              // Canonical login email from the user table; fall back to the
              // license email only if the user row is somehow missing.
              email: (userMap.get(l.userId)?.email ?? l.email).toLowerCase(),
              licenseCount: 0,
              totalCredits: 0,
              created,
              licenses: [],
            };
            customerMap.set(l.userId, customer);
          }
          customer.licenseCount += 1;
          // Credits are pooled per account, so every one of this customer's
          // licenses reports the SAME account balance. Set the total once rather
          // than summing per license (which would multiply it by the key count).
          customer.totalCredits = l.credits;
          // Show the customer's earliest license date as their "Created".
          if (created < customer.created) customer.created = created;
          customer.licenses.push({
            id: l.id,
            key: l.key,
            status: l.status,
            credits: l.credits,
            stripeSessionId: l.stripeSessionId,
            stripeCustomerId: l.stripeCustomerId,
            created,
          });
        }

        return { customers: Array.from(customerMap.values()) };
      } catch (error) {
        console.error("Customers fetch error:", error);
        throw new TRPCError({
          code: "INTERNAL_SERVER_ERROR",
          message:
            error instanceof Error ? error.message : "Failed to fetch customers",
        });
      }
    }),

  /**
   * Update a customer's email. Moves the whole customer: the user's canonical
   * email AND every license_keys.email row for that user, transactionally.
   * Blocks if the new email already belongs to a different account.
   */
  updateEmail: adminProcedure
    .input(
      z.object({
        userId: z.string().min(1),
        newEmail: z.string().email(),
      })
    )
    .mutation(async ({ input }) => {
      const email = input.newEmail.toLowerCase().trim();

      const target = await getUserById(input.userId);
      if (!target) {
        throw new TRPCError({ code: "NOT_FOUND", message: "Customer not found" });
      }

      // No-op when the email is unchanged: skip the collision check and the
      // (multi-row) write entirely.
      if (target.email.toLowerCase() === email) {
        return { success: true, email };
      }

      // Block if the new email already belongs to a different account.
      const existing = await getUserByEmail(email);
      if (existing && existing.id !== input.userId) {
        throw new TRPCError({
          code: "CONFLICT",
          message: "That email already belongs to another account",
        });
      }

      try {
        await updateCustomerEmail(input.userId, email);
      } catch (error) {
        // Unique-constraint race on user.email (Postgres 23505).
        const code = (error as { code?: string } | null)?.code;
        if (code === "23505") {
          throw new TRPCError({
            code: "CONFLICT",
            message: "That email already belongs to another account",
          });
        }
        console.error("Update customer email error:", error);
        throw new TRPCError({
          code: "INTERNAL_SERVER_ERROR",
          message:
            error instanceof Error ? error.message : "Failed to update email",
        });
      }

      return { success: true, email };
    }),

  /**
   * Grant a license key to a user by email.
   * Creates the user if they don't exist, generates a license key,
   * grants initial credits, and emails the key.
   */
  grant: adminProcedure
    .input(z.object({ email: z.string().email() }))
    .mutation(async ({ input }) => {
      const { email } = input;
      const name = email.split("@")[0];

      // Generate a unique license key with collision check
      let key: string;
      for (let i = 0; i < 5; i++) {
        key = generateLicenseKey();
        const existing = await findAccountByKey(key);
        if (!existing) break;
        if (i === 4) throw new TRPCError({ code: "INTERNAL_SERVER_ERROR", message: "Failed to generate unique license key" });
      }

      // Create or find the user
      const user = await getOrCreateUser(email, { name });
      if (!user) {
        throw new TRPCError({ code: "INTERNAL_SERVER_ERROR", message: "Failed to create user" });
      }

      // Insert the license key
      const license = await insertAccountKey({
        key: key!,
        email,
        userId: user.id,
        status: "granted",
      });
      if (!license) {
        throw new TRPCError({ code: "INTERNAL_SERVER_ERROR", message: "Failed to insert license key" });
      }

      // Grant initial credits
      await grantCreditLot({
        userId: license.userId,
        amount: 5000,
        sourceType: "admin_license_bundle",
        sourceId: license.id,
      });

      // Send the license key email
      await emailService.sendLicenseKey({
        customerName: name,
        customerEmail: email,
        licenseKey: key!,
        productName: "HyperWhisper",
        supportEmail: "support@hyperwhisper.com",
      });

      return { email, licenseKey: key! };
    }),

  /**
   * Add credits to a specific license key.
   * Adds the specified amount to the existing balance.
   */
  addCredits: adminProcedure
    .input(
      z.object({
        licenseKeyId: z.string().uuid(),
        amount: z.number().positive().max(MAX_ADMIN_CREDIT_GRANT),
      }),
    )
    .mutation(async ({ input }) => {
      const { licenseKeyId, amount } = input;

      const license = await findAccountById(licenseKeyId);
      if (!license) {
        throw new TRPCError({
          code: "NOT_FOUND",
          message: "License key not found",
        });
      }

      // Credits are pooled per account: grant to and read the license's owning
      // user, so the added credits land on the same wallet every key reads.
      const currentBalance = await getCreditBalance(license.userId);
      const grantResult = await grantCreditLot({
        userId: license.userId,
        amount,
        sourceType: "admin_manual",
        sourceId: crypto.randomUUID(),
      });

      return {
        licenseKeyId,
        previousBalance: currentBalance,
        addedAmount: amount,
        newBalance: grantResult.balance,
      };
    }),

  /**
   * Refund a Stripe payment for a license.
   * Optionally revokes the license key.
   */
  refund: adminProcedure
    .input(
      z.object({
        licenseKeyId: z.string().uuid(),
        revokeLicense: z.boolean(),
      })
    )
    .mutation(async ({ input }) => {
      const { licenseKeyId, revokeLicense } = input;

      const license = await findAccountById(licenseKeyId);
      if (!license) {
        throw new TRPCError({ code: "NOT_FOUND", message: "License key not found" });
      }
      if (!license.stripeSessionId) {
        throw new TRPCError({ code: "BAD_REQUEST", message: "No Stripe session associated with this license" });
      }

      // Retrieve the checkout session to get the payment intent
      const session = await stripe.checkout.sessions.retrieve(license.stripeSessionId);
      if (!session.payment_intent) {
        throw new TRPCError({ code: "BAD_REQUEST", message: "No payment intent found for this session" });
      }

      const paymentIntentId =
        typeof session.payment_intent === "string"
          ? session.payment_intent
          : session.payment_intent.id;

      // Create the refund. The idempotency key (keyed on the license, which maps
      // 1:1 to its refundable payment) makes a retried/double-clicked mutation
      // reuse the same refund instead of surfacing a raw Stripe error.
      await stripe.refunds.create(
        { payment_intent: paymentIntentId },
        { idempotencyKey: `admin-refund-${licenseKeyId}` }
      );

      // A full license refund reverses the included credit grant. Record the
      // admin refund as processed so retried mutations do not double-deduct,
      // while separately purchased credit packs remain on the license balance.
      await refundCreditGrant({
        sourceType: "license_bundle",
        sourceId: license.stripeSessionId,
      });

      // Optionally revoke the license
      if (revokeLicense) {
        await updateAccountKey(licenseKeyId, { status: "revoked" });
      }

      return { success: true, revoked: revokeLicense };
    }),
});
