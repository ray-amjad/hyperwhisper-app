import assert from "node:assert/strict";
import test from "node:test";

const MODULE_PATH = "../server/api/routers/admin/customer-refund.ts";
const load = () => import(MODULE_PATH);

test("an injected Stripe source controls the license refund request", async () => {
  const { createCustomerPaymentRefunder } = await load();
  const sessionIds: string[] = [];
  const refunds: Array<{
    paymentIntentId: string;
    idempotencyKey: string;
  }> = [];
  let paymentIntent: string | { id: string } | null = { id: "pi_in_memory" };
  const refunder = createCustomerPaymentRefunder({
    retrievePaymentIntent: async (stripeSessionId: string) => {
      sessionIds.push(stripeSessionId);
      return paymentIntent;
    },
    createRefund: async (paymentIntentId: string, idempotencyKey: string) => {
      refunds.push({ paymentIntentId, idempotencyKey });
    },
  });

  await refunder.refundLicensePayment(
    "123e4567-e89b-12d3-a456-426614174000",
    "cs_in_memory",
  );
  paymentIntent = null;

  await assert.rejects(
    refunder.refundLicensePayment(
      "123e4567-e89b-12d3-a456-426614174001",
      "cs_without_payment",
    ),
    {
      name: "TRPCError",
      message: "No payment intent found for this session",
    },
  );

  assert.deepEqual(sessionIds, ["cs_in_memory", "cs_without_payment"]);
  assert.deepEqual(refunds, [
    {
      paymentIntentId: "pi_in_memory",
      idempotencyKey: "admin-refund-123e4567-e89b-12d3-a456-426614174000",
    },
  ]);
});
