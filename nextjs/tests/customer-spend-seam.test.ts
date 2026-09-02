import assert from "node:assert/strict";
import test from "node:test";

const MODULE_PATH = "../server/api/routers/admin/customer-spend.ts";
const load = () => import(MODULE_PATH);

test("an injected source controls charge and dispute spend rules", async () => {
  const { createCustomerSpendReader } = await load();
  const disputeLookups: string[] = [];
  const disputes = new Map<string, "won" | "lost" | "under_review">([
    ["won-dispute", "won"],
    ["lost-dispute", "lost"],
    ["unresolved-dispute", "under_review"],
  ]);
  const reader = createCustomerSpendReader({
    listCharges: async () => [
      {
        id: "succeeded",
        status: "succeeded" as const,
        disputed: false,
        amount: 1_000,
        amount_refunded: 0,
      },
      {
        id: "failed",
        status: "failed" as const,
        disputed: true,
        amount: 900,
        amount_refunded: 0,
      },
      {
        id: "partially-refunded",
        status: "succeeded" as const,
        disputed: false,
        amount: 1_200,
        amount_refunded: 200,
      },
      {
        id: "won-dispute",
        status: "succeeded" as const,
        disputed: true,
        amount: 1_500,
        amount_refunded: 100,
      },
      {
        id: "lost-dispute",
        status: "succeeded" as const,
        disputed: true,
        amount: 2_000,
        amount_refunded: 0,
      },
      {
        id: "unresolved-dispute",
        status: "succeeded" as const,
        disputed: true,
        amount: 3_000,
        amount_refunded: 0,
      },
    ],
    getLatestDisputeStatus: async (chargeId: string) => {
      disputeLookups.push(chargeId);
      return disputes.get(chargeId) ?? null;
    },
  });

  assert.equal(
    await reader.getNetSpendCentsForStripeCustomer("cus_in_memory"),
    3_400,
  );
  assert.deepEqual(disputeLookups, [
    "won-dispute",
    "lost-dispute",
    "unresolved-dispute",
  ]);
});
