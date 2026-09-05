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
import { stripe } from "@/lib/clients/stripe";
import {
  getAccountKeyCustomerPage,
  getOrCreateUser,
  insertAccountKey,
  findAccountByKey,
  findAccountById,
  getCreditBalance,
  grantCreditLot,
  refundCreditGrant,
  revokeAccountKey,
  getAccountKeysWithCreditsForUserIds,
  getUserById,
  getUserByEmail,
  getUsersByIds,
  updateCustomerEmail,
} from "@/src/lib/db-layer";
import { generateLicenseKey } from "@/lib/services/license-key";
import { emailService } from "@/lib/services/email";
import { createCustomerPaymentRefunder } from "./customer-refund";
import { createCustomerSpendReader } from "./customer-spend";

// Pre-existing inline Stripe client, pinned to the API version the refund
// flow (checkout.sessions.retrieve / refunds.create below) was built and
// tested against. Deliberately kept separate from the shared client in
// lib/clients/stripe.ts (which tracks a newer API version) so this PR's new
// spend-fetching code doesn't change behavior for the existing, unrelated,
// production money-handling refund flow. New code in this router should use
// the shared `stripe` client instead.
const legacyStripe = new Stripe(process.env.STRIPE_SECRET_KEY!, {
  // @ts-expect-error - the SDK's types only admit the API version it ships
  // with; this flow stays pinned to the older one it was tested against.
  apiVersion: "2025-02-24.acacia",
});

const MAX_ADMIN_CREDIT_GRANT = 1_000_000;

// Distinct customers (by userId) per page of the admin Customers list.
const CUSTOMERS_PAGE_SIZE = 100;

const customerSpendReader = createCustomerSpendReader({
  listCharges: async (stripeCustomerId) =>
    stripe.charges
      .list({ customer: stripeCustomerId, limit: 100 })
      .autoPagingToArray({ limit: 10_000 }),
  getLatestDisputeStatus: async (chargeId) => {
    const disputes = await stripe.disputes.list({ charge: chargeId, limit: 1 });

    return disputes.data[0]?.status ?? null;
  },
});

const customerPaymentRefunder = createCustomerPaymentRefunder({
  retrievePaymentIntent: async (stripeSessionId) => {
    const session =
      await legacyStripe.checkout.sessions.retrieve(stripeSessionId);

    return session.payment_intent;
  },
  createRefund: async (paymentIntentId, idempotencyKey) => {
    await legacyStripe.refunds.create(
      { payment_intent: paymentIntentId },
      { idempotencyKey },
    );
  },
});

export const customersRouter = createTRPCRouter({
  /**
   * List customers, one row per customer (grouped by user), each carrying all
   * of that customer's license keys. Supports optional email search filter
   * and is paginated at the distinct-customer (userId) level, CUSTOMERS_PAGE_SIZE
   * per page.
   *
   * A search matches individual license rows, but the page of matching
   * customers (by userId) is then expanded to each customer's FULL license
   * set so the license count, total credits, and "moves all N licenses" copy
   * reflect the true totals — not just the licenses whose email matched the
   * search term.
   */
  list: adminProcedure
    .input(
      z
        .object({
          search: z.string().optional(),
          page: z.number().int().min(1).optional(),
        })
        .optional()
    )
    .query(async ({ input }) => {
      try {
        const search = input?.search?.trim();
        const requestedPage = input?.page ?? 1;

        // `page` here is the clamped, actually-fetched page — it may differ
        // from `requestedPage` if the result set shrank (a search narrowed
        // it, or a mutation changed who matches) since the page number was
        // last chosen. See getAccountKeyCustomerPage for the clamping.
        const { userIds, totalCustomers, page } = await getAccountKeyCustomerPage({
          search,
          page: requestedPage,
          pageSize: CUSTOMERS_PAGE_SIZE,
        });

        // Pull every license (with credit balance) for this page's customers,
        // plus canonical user.email for display.
        const [licenses, userMap] = await Promise.all([
          getAccountKeysWithCreditsForUserIds(userIds),
          getUsersByIds(userIds),
        ]);

        // Group licenses by their owning user. Display order is NOT derived
        // from this Map's insertion order (see below, where we reorder by
        // `userIds`) — a search filters which licenses match, so a
        // customer's rank in `userIds` (based on their matching license's
        // date) can disagree with the newest-first order of their full,
        // unfiltered license rows here.
        const customerMap = new Map<
          string,
          {
            userId: string;
            email: string;
            licenseCount: number;
            totalCredits: number;
            // null = a Stripe fetch failed for at least one of this
            // customer's Stripe customer IDs, so the total is unknown rather
            // than a possibly-wrong number. Customers with no Stripe
            // customer ID at all keep the default 0 here; the client tells
            // that state apart from "$0 spent" via `licenses`.
            totalSpentCents: number | null;
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

        // Distinct Stripe customer IDs per customer (userId), collected once
        // here during the license-grouping pass rather than re-derived from
        // customer.licenses again later.
        const stripeCustomerIdsByUserId = new Map<string, Set<string>>();

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
              totalSpentCents: 0,
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

          if (l.stripeCustomerId) {
            let ids = stripeCustomerIdsByUserId.get(l.userId);

            if (!ids) {
              ids = new Set();
              stripeCustomerIdsByUserId.set(l.userId, ids);
            }
            ids.add(l.stripeCustomerId);
          }
        }

        // Attach each customer's net lifetime Stripe spend, summed across all
        // distinct stripeCustomerIds among their licenses (a customer can have
        // more than one, since some purchase flows use `customer_creation:
        // "always"`). Customers with no Stripe customer ID at all are skipped
        // entirely — no Stripe call, $0 spend.
        const allStripeCustomerIds = Array.from(
          new Set(
            Array.from(stripeCustomerIdsByUserId.values()).flatMap((ids) =>
              Array.from(ids)
            )
          )
        );
        const spendByStripeCustomerId =
          await customerSpendReader.getNetSpendCentsForStripeCustomers(
            allStripeCustomerIds,
          );
        for (const customer of Array.from(customerMap.values())) {
          const customerStripeIds = stripeCustomerIdsByUserId.get(
            customer.userId
          );

          if (!customerStripeIds || customerStripeIds.size === 0) {
            // No Stripe customer ID on any of this customer's licenses —
            // nothing was fetched. Leave the default 0; the client tells
            // this state apart from "$0 spent" via customer.licenses.
            continue;
          }

          let totalSpentCents = 0;
          let hadFailedFetch = false;

          for (const id of Array.from(customerStripeIds)) {
            const cents = spendByStripeCustomerId.get(id);

            if (cents === null || cents === undefined) {
              hadFailedFetch = true;
              break;
            }
            totalSpentCents += cents;
          }
          // If ANY of this customer's Stripe customer IDs failed to fetch,
          // report the whole total as unknown rather than a possibly-wrong
          // (too low) number.
          customer.totalSpentCents = hadFailedFetch ? null : totalSpentCents;
        }

        // Reorder explicitly by the canonical page order `userIds` came back
        // in (see getAccountKeyCustomerPage), rather than relying on Map
        // insertion order — the two can disagree under search (see the
        // comment above customerMap).
        const customers = userIds
          .map((userId) => customerMap.get(userId))
          .filter((customer): customer is NonNullable<typeof customer> => customer !== undefined);

        return {
          customers,
          totalCustomers,
          page,
          pageSize: CUSTOMERS_PAGE_SIZE,
          totalPages: Math.max(1, Math.ceil(totalCustomers / CUSTOMERS_PAGE_SIZE)),
        };
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
    .mutation(async ({ ctx, input }) => {
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
        // Also drops the moved account's web sessions — see
        // `updateCustomerEmail`. `actingUserId` keeps an admin from signing
        // themselves out when they correct their own address.
        await updateCustomerEmail(input.userId, email, {
          actingUserId: ctx.user.id,
        });
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
      let key: string | undefined;
      for (let i = 0; i < 5; i++) {
        const candidate = generateLicenseKey();
        const existing = await findAccountByKey(candidate);
        if (!existing) {
          key = candidate;
          break;
        }
        if (i === 4) throw new TRPCError({ code: "INTERNAL_SERVER_ERROR", message: "Failed to generate unique license key" });
      }
      // Unreachable: the loop either assigns `key` or throws on its last pass.
      // Kept so `key` is a `string` below without a non-null assertion.
      if (key === undefined) {
        throw new TRPCError({
          code: "INTERNAL_SERVER_ERROR",
          message: "Failed to generate unique license key",
        });
      }

      // Create or find the user
      const user = await getOrCreateUser(email, { name });
      if (!user) {
        throw new TRPCError({ code: "INTERNAL_SERVER_ERROR", message: "Failed to create user" });
      }

      // Insert the license key
      const license = await insertAccountKey({
        key,
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
        licenseKey: key,
        productName: "HyperWhisper",
        supportEmail: "hi@support.hyperwhisper.com",
      });

      return { email, licenseKey: key };
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
    .mutation(async ({ ctx, input }) => {
      const { licenseKeyId, revokeLicense } = input;

      const license = await findAccountById(licenseKeyId);
      if (!license) {
        throw new TRPCError({ code: "NOT_FOUND", message: "License key not found" });
      }
      if (!license.stripeSessionId) {
        throw new TRPCError({ code: "BAD_REQUEST", message: "No Stripe session associated with this license" });
      }

      // Create the refund. The idempotency key (keyed on the license, which maps
      // 1:1 to its refundable payment) makes a retried/double-clicked mutation
      // reuse the same refund instead of surfacing a raw Stripe error.
      await customerPaymentRefunder.refundLicensePayment(
        licenseKeyId,
        license.stripeSessionId,
      );

      // A full license refund reverses the included credit grant. Record the
      // admin refund as processed so retried mutations do not double-deduct,
      // while separately purchased credit packs remain on the license balance.
      await refundCreditGrant({
        sourceType: "license_bundle",
        sourceId: license.stripeSessionId,
      });

      // Optionally revoke the license. This also drops the owner's web
      // sessions — a 90-day session minted by license-key sign-in would
      // otherwise outlive the revoked key. See `revokeAccountKey`.
      //
      // Except when the owner is the admin running the refund: comped keys are
      // routinely minted against an admin's own email, and signing yourself out
      // mid-mutation is never the intent. The key is still revoked; only the
      // session sweep is skipped, and only for the one account we know is
      // legitimately in use right now.
      if (revokeLicense) {
        await revokeAccountKey(licenseKeyId, license.userId, {
          actingUserId: ctx.user.id,
        });
      }

      return { success: true, revoked: revokeLicense };
    }),
});
