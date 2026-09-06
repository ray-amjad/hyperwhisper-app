import assert from "node:assert/strict";
import test from "node:test";

const MODULE_PATH = "../server/api/routers/customer-credit-history.ts";
const load = () => import(MODULE_PATH);

test("an injected clock controls the exact credit expiry boundary", async () => {
  const { createCreditHistoryPresenter } = await load();
  let now = Date.parse("2026-09-06T12:00:00.000Z");
  const presentCreditHistory = createCreditHistoryPresenter({ now: () => now });
  const expiresAt = new Date(now + 1);
  const grant = {
    id: "grant-1",
    createdAt: new Date("2026-09-01T00:00:00.000Z"),
    expiresAt,
    originalAmount: 1_000,
    remainingAmount: 250,
    status: "active",
  };

  assert.equal(presentCreditHistory([grant]).grants[0]?.expired, false);

  now = expiresAt.getTime();
  assert.equal(presentCreditHistory([grant]).grants[0]?.expired, true);
});
