import { stripe } from "@/lib/clients/stripe";
import { isRecord } from "@/src/lib/type-guards";

/**
 * Whether the Stripe customer cached on an account key still belongs to
 * `email` (#2460).
 *
 * `account_keys.stripe_customer_id` is cached on the first purchase and is not
 * touched when an admin moves the account to a new address
 * (`updateCustomerEmail`). Reusing it after a move bills the customer of the
 * OLD address: Stripe prefills that address, sends it the receipt, and opens
 * its payment history in the billing portal. So a cached id is reused only
 * while the Stripe customer's email is the account's current email. A deleted
 * or missing customer belongs to nobody.
 *
 * Any other Stripe error is thrown: the caller already maps a Stripe fault to
 * its own error response.
 */
export async function stripeCustomerBelongsTo(
  customerId: string,
  email: string,
): Promise<boolean> {
  let customer: Awaited<ReturnType<typeof stripe.customers.retrieve>>;
  try {
    customer = await stripe.customers.retrieve(customerId);
  } catch (error) {
    if (isRecord(error) && error.code === "resource_missing") return false;
    throw error;
  }
  if (customer.deleted) return false;
  return normalizeEmail(customer.email) === normalizeEmail(email);
}

function normalizeEmail(email: string | null | undefined): string {
  return (email ?? "").trim().toLowerCase();
}
