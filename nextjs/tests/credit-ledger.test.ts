/**
 * The credit ledger in `src/lib/db-layer.ts`, run against a real Postgres
 * (PGlite, see `credit-ledger-harness.ts`) with the real migrations.
 *
 * A credit is a unit of paid cloud transcription. Every rule below is money:
 * a double grant hands out free usage, a missed clawback pays a refund twice,
 * and a cache that drifts shows credits the account cannot spend.
 */
import { before, beforeEach, describe, test } from "node:test";
import assert from "node:assert/strict";

import {
  cachedBalance,
  db,
  grantsBySource,
  loadDbLayer,
  resetDatabase,
  seedGrant,
  seedUser,
  setCachedBalance,
  stripeEventCount,
} from "./credit-ledger-harness";
import * as schema from "../src/db/schema";

const DAY = 24 * 60 * 60 * 1000;
const inDays = (n: number) => new Date(Date.now() + n * DAY);

let L: Awaited<ReturnType<typeof loadDbLayer>>;

before(async () => {
  L = await loadDbLayer();
});

beforeEach(async () => {
  await resetDatabase();
});

describe("grantCreditLot", () => {
  test("a new lot is stored in full, expires in 365 days, and sets the cache", async () => {
    await seedUser("u1");
    const before = Date.now();

    const result = await L.grantCreditLot({
      userId: "u1",
      amount: 250,
      sourceType: "admin_manual",
      sourceId: "grant-1",
    });

    assert.deepEqual(result, { status: "processed", balance: 250 });
    const grant = (await grantsBySource("u1")).get("grant-1");
    assert.ok(grant);
    assert.equal(grant.sourceType, "admin_manual");
    assert.equal(grant.original, 250);
    assert.equal(grant.remaining, 250);
    assert.equal(grant.refunded, 0);
    assert.equal(grant.status, "active");
    assert.ok(grant.expiresAt);
    const ttl = grant.expiresAt.getTime() - before;
    assert.ok(ttl >= 365 * DAY && ttl < 365 * DAY + 60_000, `ttl was ${ttl} ms`);
    assert.equal(await cachedBalance("u1"), 250);
  });

  test("a second lot adds to the cached balance", async () => {
    await seedUser("u1");
    await L.grantCreditLot({ userId: "u1", amount: 100, sourceType: "admin_manual", sourceId: "a" });

    const result = await L.grantCreditLot({
      userId: "u1",
      amount: 40,
      sourceType: "admin_manual",
      sourceId: "b",
    });

    assert.deepEqual(result, { status: "processed", balance: 140 });
    assert.equal(await cachedBalance("u1"), 140);
  });

  test("the same source is a duplicate: no second row, and the balance is the grant total", async () => {
    await seedUser("u1");
    await L.grantCreditLot({ userId: "u1", amount: 100, sourceType: "license_bundle", sourceId: "k1" });
    await L.spendCreditGrantsByProvenance("u1", 30);

    const result = await L.grantCreditLot({
      userId: "u1",
      amount: 100,
      sourceType: "license_bundle",
      sourceId: "k1",
    });

    assert.deepEqual(result, { status: "duplicate", balance: 70 });
    assert.equal((await grantsBySource("u1")).size, 1);
    assert.equal(await cachedBalance("u1"), 70);
  });

  test("the same source id under a different source type is a separate lot", async () => {
    await seedUser("u1");
    await L.grantCreditLot({ userId: "u1", amount: 10, sourceType: "license_bundle", sourceId: "x" });

    const result = await L.grantCreditLot({
      userId: "u1",
      amount: 5,
      sourceType: "admin_manual",
      sourceId: "x",
    });

    assert.deepEqual(result, { status: "processed", balance: 15 });
  });
});

describe("grantCreditsForStripeEvent", () => {
  test("a paid event grants a stripe_credit_pack lot keyed by the Stripe object", async () => {
    await seedUser("u1");

    const status = await L.grantCreditsForStripeEvent({
      eventId: "evt_1",
      eventType: "checkout.session.completed",
      stripeObjectId: "cs_1",
      userId: "u1",
      creditAmount: 500,
    });

    assert.equal(status, "processed");
    const grant = (await grantsBySource("u1")).get("cs_1");
    assert.ok(grant, "grant keyed by the Stripe object id");
    assert.equal(grant.sourceType, "stripe_credit_pack");
    assert.equal(grant.original, 500);
    assert.equal(await cachedBalance("u1"), 500);
    assert.equal(await stripeEventCount(), 1);
  });

  test("a redelivered object under a NEW event id grants nothing", async () => {
    await seedUser("u1");
    const base = {
      eventType: "checkout.session.completed",
      stripeObjectId: "cs_1",
      userId: "u1",
      creditAmount: 500,
    };
    await L.grantCreditsForStripeEvent({ ...base, eventId: "evt_1" });

    const status = await L.grantCreditsForStripeEvent({ ...base, eventId: "evt_2" });

    assert.equal(status, "duplicate");
    assert.equal((await grantsBySource("u1")).size, 1);
    assert.equal(await cachedBalance("u1"), 500);
    assert.equal(await stripeEventCount(), 1);
  });

  test("a redelivered object grants nothing, even under a different source id", async () => {
    await seedUser("u1");
    const base = {
      eventType: "checkout.session.completed",
      stripeObjectId: "cs_1",
      userId: "u1",
      creditAmount: 500,
    };
    await L.grantCreditsForStripeEvent({ ...base, eventId: "evt_1", sourceId: "first" });

    const status = await L.grantCreditsForStripeEvent({ ...base, eventId: "evt_2", sourceId: "second" });

    assert.equal(status, "duplicate");
    assert.deepEqual([...(await grantsBySource("u1")).keys()], ["first"]);
    assert.equal(await cachedBalance("u1"), 500);
  });

  test("an explicit source type and id are kept", async () => {
    await seedUser("u1");

    await L.grantCreditsForStripeEvent({
      eventId: "evt_1",
      eventType: "checkout.session.completed",
      stripeObjectId: "cs_1",
      userId: "u1",
      creditAmount: 5000,
      sourceType: "license_bundle",
      sourceId: "key-row-1",
    });

    const grants = await grantsBySource("u1");
    assert.equal(grants.get("key-row-1")?.sourceType, "license_bundle");
    assert.equal(grants.has("cs_1"), false);
  });
});

describe("spendCreditGrantsByProvenance", () => {
  test("spends the soonest-to-expire grant first and never-expiring grants last", async () => {
    await seedUser("u1");
    await seedGrant({ userId: "u1", sourceId: "forever", amount: 100, expiresAt: null });
    await seedGrant({ userId: "u1", sourceId: "late", amount: 100, expiresAt: inDays(300) });
    await seedGrant({ userId: "u1", sourceId: "soon", amount: 100, expiresAt: inDays(10) });

    const result = await L.spendCreditGrantsByProvenance("u1", 150);

    assert.deepEqual(result, { balance: 150, deductedAmount: 150 });
    const g = await grantsBySource("u1");
    assert.equal(g.get("soon")?.remaining, 0);
    assert.equal(g.get("soon")?.status, "spent");
    assert.equal(g.get("late")?.remaining, 50);
    assert.equal(g.get("late")?.status, "active");
    assert.equal(g.get("forever")?.remaining, 100);
  });

  test("with the same expiry, the older grant is spent first", async () => {
    await seedUser("u1");
    const expiry = inDays(100);
    await seedGrant({ userId: "u1", sourceId: "new", amount: 50, expiresAt: expiry, createdAt: inDays(-1) });
    await seedGrant({ userId: "u1", sourceId: "old", amount: 50, expiresAt: expiry, createdAt: inDays(-5) });

    await L.spendCreditGrantsByProvenance("u1", 20);

    const g = await grantsBySource("u1");
    assert.equal(g.get("old")?.remaining, 30);
    assert.equal(g.get("new")?.remaining, 50);
  });

  test("expired and non-active grants are neither spent nor counted", async () => {
    await seedUser("u1");
    await seedGrant({ userId: "u1", sourceId: "expired", amount: 100, expiresAt: inDays(-1) });
    await seedGrant({ userId: "u1", sourceId: "refunded", amount: 100, status: "refunded" });
    await seedGrant({ userId: "u1", sourceId: "live", amount: 30, expiresAt: inDays(30) });

    const result = await L.spendCreditGrantsByProvenance("u1", 50);

    assert.deepEqual(result, { balance: 0, deductedAmount: 30 });
    const g = await grantsBySource("u1");
    assert.equal(g.get("expired")?.remaining, 100);
    assert.equal(g.get("refunded")?.remaining, 100);
    assert.equal(g.get("live")?.status, "spent");
  });

  test("an overspend floors at zero and reports only what it took", async () => {
    await seedUser("u1");
    await seedGrant({ userId: "u1", sourceId: "a", amount: 12.5 });

    const result = await L.spendCreditGrantsByProvenance("u1", 40);

    assert.deepEqual(result, { balance: 0, deductedAmount: 12.5 });
    assert.equal(await cachedBalance("u1"), 0);
  });

  test("the cache is reconciled to the grant total, healing drift", async () => {
    await seedUser("u1");
    await seedGrant({ userId: "u1", sourceId: "a", amount: 100 });
    await setCachedBalance("u1", 9999);

    const balance = await L.deductCreditBalance("u1", 25);

    assert.equal(balance, 75);
    assert.equal(await cachedBalance("u1"), 75);
  });
});

describe("refundCreditGrant", () => {
  test("an unknown grant is a duplicate that reclaims nothing", async () => {
    await seedUser("u1");
    await seedGrant({ userId: "u1", sourceId: "a", amount: 100 });

    const result = await L.refundCreditGrant({ sourceType: "stripe_credit_pack", sourceId: "nope" });

    assert.deepEqual(result, { status: "duplicate", refundedAmount: 0 });
    assert.equal((await grantsBySource("u1")).get("a")?.remaining, 100);
  });

  test("an unspent pack is removed in full and marked refunded", async () => {
    await seedUser("u1");
    await seedGrant({ userId: "u1", sourceType: "admin_manual", sourceId: "bundle", amount: 1000 });
    await seedGrant({ userId: "u1", sourceType: "stripe_credit_pack", sourceId: "cs_1", amount: 300 });

    const result = await L.refundCreditGrant({ sourceType: "stripe_credit_pack", sourceId: "cs_1" });

    assert.deepEqual(result, { status: "processed", refundedAmount: 300 });
    const g = await grantsBySource("u1");
    assert.equal(g.get("cs_1")?.remaining, 0);
    assert.equal(g.get("cs_1")?.refunded, 300);
    assert.equal(g.get("cs_1")?.status, "refunded");
    assert.equal(g.get("bundle")?.remaining, 1000);
    assert.equal(await cachedBalance("u1"), 1000);
  });

  test("a fully spent pack is clawed back from the account's other grants (#872)", async () => {
    await seedUser("u1");
    // The pack expires first, so spending drains it before the bundle.
    await seedGrant({ userId: "u1", sourceType: "stripe_credit_pack", sourceId: "cs_1", amount: 300, expiresAt: inDays(10) });
    await seedGrant({ userId: "u1", sourceType: "admin_manual", sourceId: "bundle", amount: 1000, expiresAt: inDays(200) });
    await L.spendCreditGrantsByProvenance("u1", 300);
    assert.equal((await grantsBySource("u1")).get("cs_1")?.status, "spent");

    const result = await L.refundCreditGrant({ sourceType: "stripe_credit_pack", sourceId: "cs_1" });

    assert.deepEqual(result, { status: "processed", refundedAmount: 300 });
    const g = await grantsBySource("u1");
    assert.equal(g.get("bundle")?.remaining, 700);
    assert.equal(g.get("cs_1")?.refunded, 300);
    assert.equal(g.get("cs_1")?.status, "refunded");
    assert.equal(await cachedBalance("u1"), 700);
  });

  test("the clawback draws from the refunded grant first, then soonest-to-expire, and skips expired grants", async () => {
    await seedUser("u1");
    await seedGrant({ userId: "u1", sourceId: "expired", amount: 500, expiresAt: inDays(-1) });
    await seedGrant({ userId: "u1", sourceId: "late", amount: 100, expiresAt: inDays(300) });
    await seedGrant({ userId: "u1", sourceId: "soon", amount: 100, expiresAt: inDays(20) });
    await seedGrant({ userId: "u1", sourceType: "stripe_credit_pack", sourceId: "cs_1", amount: 200, remaining: 50, expiresAt: inDays(250) });

    const result = await L.refundCreditGrant({ sourceType: "stripe_credit_pack", sourceId: "cs_1" });

    // 200 to reclaim: 50 from the pack itself, then 100 from "soon", then 50 from "late".
    assert.deepEqual(result, { status: "processed", refundedAmount: 200 });
    const g = await grantsBySource("u1");
    assert.equal(g.get("soon")?.remaining, 0);
    assert.equal(g.get("soon")?.status, "spent");
    assert.equal(g.get("late")?.remaining, 50);
    assert.equal(g.get("late")?.status, "active");
    assert.equal(g.get("expired")?.remaining, 500);
    assert.equal(await cachedBalance("u1"), 50);
  });

  test("the clawback is clamped at the active balance, so the account never goes negative", async () => {
    await seedUser("u1");
    await seedGrant({ userId: "u1", sourceType: "stripe_credit_pack", sourceId: "cs_1", amount: 300, remaining: 0, status: "spent" });
    await seedGrant({ userId: "u1", sourceId: "bundle", amount: 80 });

    const result = await L.refundCreditGrant({ sourceType: "stripe_credit_pack", sourceId: "cs_1" });

    assert.deepEqual(result, { status: "processed", refundedAmount: 300 });
    const g = await grantsBySource("u1");
    assert.equal(g.get("bundle")?.remaining, 0);
    assert.equal(g.get("cs_1")?.refunded, 300);
    assert.equal(await cachedBalance("u1"), 0);
  });

  test("a second refund of the same grant is a duplicate that reclaims nothing", async () => {
    await seedUser("u1");
    await seedGrant({ userId: "u1", sourceType: "stripe_credit_pack", sourceId: "cs_1", amount: 300 });
    await seedGrant({ userId: "u1", sourceId: "bundle", amount: 1000 });
    await L.refundCreditGrant({ sourceType: "stripe_credit_pack", sourceId: "cs_1" });

    const again = await L.refundCreditGrant({ sourceType: "stripe_credit_pack", sourceId: "cs_1" });

    assert.deepEqual(again, { status: "duplicate", refundedAmount: 0 });
    const g = await grantsBySource("u1");
    assert.equal(g.get("bundle")?.remaining, 1000);
    assert.equal(g.get("cs_1")?.refunded, 300);
  });

  test("a refund touches only the refunded grant's own account", async () => {
    await seedUser("u1");
    await seedUser("u2");
    await seedGrant({ userId: "u1", sourceType: "stripe_credit_pack", sourceId: "cs_1", amount: 300, remaining: 0, status: "spent" });
    await seedGrant({ userId: "u1", sourceId: "mine", amount: 100, expiresAt: inDays(300) });
    // Another account's grant expires sooner, so it would be drawn first.
    await seedGrant({ userId: "u2", sourceId: "other", amount: 500, expiresAt: inDays(5) });

    await L.refundCreditGrant({ sourceType: "stripe_credit_pack", sourceId: "cs_1" });

    assert.equal((await grantsBySource("u2")).get("other")?.remaining, 500);
    assert.equal((await grantsBySource("u1")).get("mine")?.remaining, 0);
  });
});

describe("balance reads", () => {
  test("getCreditBalance returns the grant total and heals a drifted cache", async () => {
    await seedUser("u1");
    await seedGrant({ userId: "u1", sourceId: "a", amount: 50 });
    await seedGrant({ userId: "u1", sourceId: "old", amount: 70, expiresAt: inDays(-1) });
    await setCachedBalance("u1", 999);

    assert.equal(await L.getCreditBalance("u1"), 50);
    assert.equal(await cachedBalance("u1"), 50);
  });

  test("getCreditBalance writes a zero cache row for an account with no grants", async () => {
    await seedUser("u1");

    assert.equal(await L.getCreditBalance("u1"), 0);
    assert.equal(await cachedBalance("u1"), 0);
  });

  test("getCreditBalance does not rewrite a cache that already matches", async () => {
    await seedUser("u1");
    await seedGrant({ userId: "u1", sourceId: "a", amount: 50 });
    const stamp = new Date("2020-01-01T00:00:00Z");
    await db.insert(schema.creditBalances).values({ userId: "u1", balance: "50", updatedAt: stamp });

    assert.equal(await L.getCreditBalance("u1"), 50);
    const row = await db.query.creditBalances.findFirst({ where: (t, { eq }) => eq(t.userId, "u1") });
    assert.equal(row?.updatedAt.getTime(), stamp.getTime());
  });

  test("getCreditBalancesForUsers sums only spendable grants and defaults unknown ids to 0", async () => {
    await seedUser("u1");
    await seedUser("u2");
    await seedGrant({ userId: "u1", sourceId: "a", amount: 10 });
    await seedGrant({ userId: "u1", sourceId: "b", amount: 15.5 });
    await seedGrant({ userId: "u1", sourceId: "expired", amount: 100, expiresAt: inDays(-1) });
    await seedGrant({ userId: "u2", sourceId: "spent", amount: 40, remaining: 0, status: "spent" });
    await seedGrant({ userId: "u2", sourceId: "refunded", amount: 40, status: "refunded" });
    await setCachedBalance("u2", 5000);

    const map = await L.getCreditBalancesForUsers(["u1", "u2", "ghost"]);

    assert.deepEqual(Object.fromEntries(map), { u1: 25.5, u2: 0, ghost: 0 });
    assert.equal((await L.getCreditBalancesForUsers([])).size, 0);
  });

  test("getPaidCreditGrantsForUsers lists only paid packs, newest first, expired ones included", async () => {
    await seedUser("u1");
    await seedUser("u2");
    await seedGrant({ userId: "u1", sourceType: "stripe_credit_pack", sourceId: "old", amount: 100, expiresAt: inDays(-1), createdAt: inDays(-400) });
    await seedGrant({ userId: "u1", sourceType: "stripe_credit_pack", sourceId: "new", amount: 200, remaining: 150, createdAt: inDays(-1) });
    await seedGrant({ userId: "u1", sourceType: "license_bundle", sourceId: "bundle", amount: 5000 });
    await seedGrant({ userId: "u2", sourceType: "stripe_credit_pack", sourceId: "u2pack", amount: 50 });

    const rows = await L.getPaidCreditGrantsForUsers(["u1"]);

    assert.deepEqual(
      rows.map((r) => [r.originalAmount, r.remainingAmount, r.status]),
      [
        [200, 150, "active"],
        [100, 100, "active"],
      ],
    );
    assert.ok(rows.every((r) => r.userId === "u1"));
    assert.deepEqual(await L.getPaidCreditGrantsForUsers([]), []);
  });
});

describe("provisionAccountKeyForEmail", () => {
  test("creates the user, a granted key and a 5000-credit internal bundle for the normalised email", async () => {
    const license = await L.provisionAccountKeyForEmail("  New.Person@Example.TEST ");

    assert.equal(license.email, "new.person@example.test");
    assert.equal(license.status, "granted");
    const owner = await db.query.user.findFirst({ where: (t, { eq }) => eq(t.id, license.userId) });
    assert.equal(owner?.email, "new.person@example.test");
    assert.equal(owner?.name, "new.person");
    assert.equal(owner?.emailVerified, false);
    const grant = (await grantsBySource(license.userId)).get(license.id);
    assert.equal(grant?.sourceType, "internal_bundle");
    assert.equal(grant?.original, 5000);
    assert.equal(await L.getCreditBalance(license.userId), 5000);
    const found = await L.findAccountByKey(license.key);
    assert.equal(found?.id, license.id);
  });

  test("a second key for the same email reuses the user and pools the credits", async () => {
    const first = await L.provisionAccountKeyForEmail("pooled@example.test");
    const second = await L.provisionAccountKeyForEmail("POOLED@example.test");

    assert.equal(second.userId, first.userId);
    assert.notEqual(second.key, first.key);
    assert.equal(await L.getCreditBalance(first.userId), 10000);
  });
});
