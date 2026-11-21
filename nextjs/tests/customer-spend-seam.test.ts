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

test("the breakdown lists every charge, and the net spend is its total", async () => {
  const { createCustomerSpendReader } = await load();
  const reader = createCustomerSpendReader({
    listCharges: async () => [
      { id: "kept", status: "succeeded" as const, disputed: false, amount: 1_500, amount_refunded: 200, created: 1_735_776_000, description: "License" },
      { id: "failed", status: "failed" as const, disputed: false, amount: 900, amount_refunded: 0 },
      { id: "lost", status: "succeeded" as const, disputed: true, amount: 700, amount_refunded: 0 },
    ],
    getLatestDisputeStatus: async () => "lost" as const,
  });

  const breakdown = await reader.getSpendBreakdownForStripeCustomer("cus");

  assert.deepEqual(breakdown, {
    netCents: 1_300,
    charges: [
      { id: "kept", createdAt: "2025-01-02T00:00:00.000Z", description: "License", amountCents: 1_500, refundedCents: 200, countedCents: 1_300 },
      { id: "failed", createdAt: null, description: null, amountCents: 900, refundedCents: 0, countedCents: 0, excluded: "not_succeeded" },
      { id: "lost", createdAt: null, description: null, amountCents: 700, refundedCents: 0, countedCents: 0, excluded: "dispute_not_won" },
    ],
  });
  assert.equal(await reader.getNetSpendCentsForStripeCustomer("cus"), 1_300);
});

test("an account's total sums its Stripe customers and is null if any failed", async () => {
  const { accountNetSpendCents } = await load();
  const spend = new Map<string, number | null>([
    ["a", 1_000],
    ["b", 535],
    ["down", null],
  ]);

  assert.equal(accountNetSpendCents(["a", "b"], spend), 1_535);
  assert.equal(accountNetSpendCents(new Set(["a", "down"]), spend), null);
  assert.equal(accountNetSpendCents(["a", "missing"], spend), null);
  assert.equal(accountNetSpendCents([], spend), 0);
});
