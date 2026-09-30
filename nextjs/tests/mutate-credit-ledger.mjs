/**
 * Mutation proof for tests/credit-ledger.test.ts.
 *
 * Each entry below does ONE exact string replace in `src/lib/db-layer.ts`,
 * runs the one test file, then puts the source back. A mutant that still
 * PASSES means the tests are hollow there.
 *
 * This file is a tool, not a test. `npm test` globs `tests/*.test.ts`, so it
 * is never picked up by the suite.
 *
 *   node tests/mutate-credit-ledger.mjs
 */
import { execFileSync } from "node:child_process";
import { readFileSync, writeFileSync } from "node:fs";

const TEST = "tests/credit-ledger.test.ts";
const FILE = "src/lib/db-layer.ts";

const MUTANTS = [
  {
    name: "count expired grants as spendable",
    from: "const ACTIVE_GRANT_EXPIRY = sql`(expires_at IS NULL OR expires_at > now())`;",
    to: "const ACTIVE_GRANT_EXPIRY = sql`(true)`;",
  },
  {
    name: "count non-active grants in the balance",
    from: "    WHERE user_id = ${userId}\n      AND status = 'active'\n      AND remaining_amount > 0",
    to: "    WHERE user_id = ${userId}\n      AND remaining_amount > 0",
  },
  {
    name: "stamp grants with a 30-day expiry",
    from: "const CREDIT_GRANT_TTL_MS = 365 * 24 * 60 * 60 * 1000;",
    to: "const CREDIT_GRANT_TTL_MS = 30 * 24 * 60 * 60 * 1000;",
  },
  {
    name: "grant a lot with no expiry",
    from: "      status: \"active\",\n      expiresAt,\n",
    to: "      status: \"active\",\n      expiresAt: null,\n",
  },
  {
    name: "dedup a lot on source id alone",
    from: "      target: [creditGrants.sourceType, creditGrants.sourceId],",
    to: "      target: [creditGrants.sourceId],",
  },
  {
    name: "on a duplicate lot, report the stale cache instead of the grant total",
    from: "    const balance = await getActiveGrantsTotal(tx, data.userId);\n    return { status: \"duplicate\", balance };",
    to: "    return { status: \"duplicate\", balance: 0 };",
  },
  {
    name: "overwrite the cache instead of adding to it",
    from: "        balance: sql`${creditBalances.balance} + ${amount}`,",
    to: "        balance: amount.toString(),",
  },
  {
    name: "dedup Stripe events on event id, not the Stripe object",
    from: ".onConflictDoNothing({ target: stripeProcessedEvents.stripeObjectId })",
    to: ".onConflictDoNothing({ target: stripeProcessedEvents.eventId })",
  },
  {
    name: "grant again on a redelivered Stripe object",
    from: "    if (!eventRow) {\n      return null;\n    }",
    to: "    if (false) {\n      return null;\n    }",
  },
  {
    name: "default a Stripe grant to an admin source type",
    from: 'sourceType: data.sourceType ?? "stripe_credit_pack",',
    to: 'sourceType: data.sourceType ?? "admin_manual",',
  },
  {
    name: "ignore an explicit Stripe source id",
    from: "sourceId: data.sourceId ?? data.stripeObjectId,",
    to: "sourceId: data.stripeObjectId,",
  },
  {
    name: "spend the latest-to-expire grant first",
    from: "      ORDER BY\n        expires_at ASC,\n        created_at ASC,\n        id\n      FOR UPDATE\n    `);\n\n    let remainingToDeduct",
    to: "      ORDER BY\n        expires_at DESC NULLS LAST,\n        created_at ASC,\n        id\n      FOR UPDATE\n    `);\n\n    let remainingToDeduct",
  },
  {
    name: "spend the newest grant first on an expiry tie",
    from: "      ORDER BY\n        expires_at ASC,\n        created_at ASC,\n        id\n      FOR UPDATE\n    `);\n\n    let remainingToDeduct",
    to: "      ORDER BY\n        expires_at ASC,\n        created_at DESC,\n        id\n      FOR UPDATE\n    `);\n\n    let remainingToDeduct",
  },
  {
    name: "take the whole request from one grant (spend below zero)",
    from: "      const deduction = Math.min(grantRemaining, remainingToDeduct);",
    to: "      const deduction = remainingToDeduct;",
  },
  {
    name: "report the requested amount as deducted",
    from: "    return { balance, deductedAmount };",
    to: "    return { balance, deductedAmount: amount };",
  },
  {
    name: "leave a drained grant active",
    from: "          status: newRemaining === 0 ? \"spent\" : \"active\",\n          updatedAt: new Date(),\n        })\n        .where(eq(creditGrants.id, grant.id));\n\n      remainingToDeduct",
    to: "          status: \"active\",\n          updatedAt: new Date(),\n        })\n        .where(eq(creditGrants.id, grant.id));\n\n      remainingToDeduct",
  },
  {
    name: "spend returns the stale cache, not the reconciled total",
    from: "    const balance = await reconcileCreditBalance(tx, userId);\n\n    return { balance, deductedAmount };",
    to: "    await reconcileCreditBalance(tx, userId);\n    const balance = 0;\n\n    return { balance, deductedAmount };",
  },
  {
    name: "do not reconcile the cache after a spend",
    from: "    const balance = await reconcileCreditBalance(tx, userId);\n",
    to: "    const balance = await getActiveGrantsTotal(tx, userId);\n",
  },
  {
    name: "refund a grant a second time",
    from: "    if (clawback <= 0) {\n      return { status: \"duplicate\", refundedAmount: 0 };\n    }",
    to: "    if (clawback < 0) {\n      return { status: \"duplicate\", refundedAmount: 0 };\n    }",
  },
  {
    name: "claw back only what is left on the refunded grant",
    from: "    const clawback = originalAmount - alreadyRefunded;",
    to: "    const clawback = originalAmount - alreadyRefunded - (originalAmount - Number((await tx.execute<{ r: string }>(sql`SELECT remaining_amount AS r FROM credit_grants WHERE id = ${grant.id}`)).rows[0].r));",
  },
  {
    // EQUIVALENT MUTANT, kept on the record. The drawdown loop reads the same
    // rows the clamp sums (active, unexpired, remaining > 0, same account), and
    // it takes at most each row's remaining amount. So without the clamp the
    // loop still stops at the active balance. The clamp test below proves the
    // floor at zero holds; it cannot tell which of the two lines made it hold.
    name: "do not clamp the clawback at the active balance (equivalent — the loop is bounded by the same rows)",
    equivalent: true,
    from: "    let toClawback = Math.min(\n      clawback,\n      await getActiveGrantsTotal(tx, grant.user_id)\n    );",
    to: "    let toClawback = clawback;",
  },
  {
    name: "draw the clawback latest-to-expire first",
    from: "        CASE WHEN id = ${grant.id} THEN 0 ELSE 1 END,\n        expires_at ASC,",
    to: "        CASE WHEN id = ${grant.id} THEN 0 ELSE 1 END,\n        expires_at DESC NULLS LAST,",
  },
  {
    name: "draw the clawback without the refunded grant first",
    from: "        CASE WHEN id = ${grant.id} THEN 0 ELSE 1 END,\n",
    to: "        CASE WHEN id = ${grant.id} THEN 1 ELSE 0 END,\n",
  },
  {
    name: "claw back from expired grants too",
    from: "        AND status = 'active'\n        AND ${ACTIVE_GRANT_EXPIRY}\n      ORDER BY\n        CASE",
    to: "        AND status = 'active'\n      ORDER BY\n        CASE",
  },
  {
    name: "claw back from any account",
    from: "      WHERE user_id = ${grant.user_id}\n        AND remaining_amount > 0",
    to: "      WHERE remaining_amount > 0",
  },
  {
    name: "record only this refund, not the running refunded total",
    from: "        refundedAmount: (alreadyRefunded + clawback).toString(),",
    to: "        refundedAmount: alreadyRefunded.toString(),",
  },
  {
    name: "leave the refunded grant active",
    from: "        status: \"refunded\",",
    to: "        status: \"active\",",
  },
  {
    name: "do not reconcile the cache after a refund",
    from: "    await reconcileCreditBalance(tx, grant.user_id);\n    return {",
    to: "    return {",
  },
  {
    name: "never heal a drifted cache on a balance read",
    from: "  if (cached !== grantsTotal) {",
    to: "  if (false) {",
  },
  {
    name: "rewrite the cache on every balance read",
    from: "  if (cached !== grantsTotal) {",
    to: "  if (true) {",
  },
  {
    name: "return the cached balance instead of the grant total",
    from: "  return grantsTotal;\n}",
    to: "  return cached ?? grantsTotal;\n}",
  },
  {
    name: "the dashboard balance counts expired grants",
    from: "        sql`(${creditGrants.expiresAt} IS NULL OR ${creditGrants.expiresAt} > now())`",
    to: "        sql`true`",
  },
  {
    name: "the dashboard balance counts refunded grants",
    from: "        eq(creditGrants.status, \"active\"),\n        sql`${creditGrants.remainingAmount} > 0`,",
    to: "        sql`${creditGrants.remainingAmount} > 0`,",
  },
  {
    name: "the dashboard leaves out accounts with no grants",
    from: "  for (const id of userIds) {\n    map.set(id, 0);\n  }",
    to: "",
  },
  {
    name: "top-up history includes free bundles",
    from: "        eq(creditGrants.sourceType, \"stripe_credit_pack\")\n",
    to: "        sql`true`\n",
  },
  {
    name: "top-up history oldest first",
    from: "    .orderBy(desc(creditGrants.createdAt));\n\n  return rows.map((row) => ({",
    to: "    .orderBy(creditGrants.createdAt);\n\n  return rows.map((row) => ({",
  },
  {
    name: "provision grants 500 credits instead of 5000",
    from: "const INTERNAL_BUNDLE_CREDITS = 5000;",
    to: "const INTERNAL_BUNDLE_CREDITS = 500;",
  },
  {
    name: "provision keys the bundle by user, not by key",
    from: "    sourceType: \"internal_bundle\",\n    sourceId: license.id,",
    to: "    sourceType: \"internal_bundle\",\n    sourceId: license.userId,",
  },
  {
    name: "provision without normalising the email",
    from: "  const normalizedEmail = email.toLowerCase().trim();",
    to: "  const normalizedEmail = email;",
  },
  {
    name: "provision a key as pending, not granted",
    from: "    userId: userResult.id,\n    status: \"granted\",",
    to: "    userId: userResult.id,\n    status: \"pending\",",
  },
];

const results = [];

for (const mutant of MUTANTS) {
  const original = readFileSync(FILE, "utf8");
  const occurrences = original.split(mutant.from).length - 1;
  if (occurrences !== 1) {
    results.push({ ...mutant, verdict: `SKIPPED (${occurrences} matches)` });
    console.log(`SKIPPED (${occurrences})  ${mutant.name}`);
    continue;
  }

  writeFileSync(FILE, original.replace(mutant.from, mutant.to));

  let killed = false;
  try {
    execFileSync(
      "node",
      ["--import", "tsx", "--experimental-test-module-mocks", "--test", TEST],
      { stdio: "pipe" },
    );
  } catch {
    killed = true;
  } finally {
    writeFileSync(FILE, original);
  }

  const verdict = killed
    ? "KILLED"
    : mutant.equivalent
      ? "SURVIVED (equivalent)"
      : "SURVIVED";
  results.push({ ...mutant, verdict });
  console.log(`${verdict.padEnd(21)} ${mutant.name}`);
}

const survivors = results.filter(
  (r) => r.verdict !== "KILLED" && r.verdict !== "SURVIVED (equivalent)",
);
console.log("");
console.log(`| Mutation | Verdict |`);
console.log(`| --- | --- |`);
for (const r of results) console.log(`| ${r.name} | ${r.verdict} |`);
console.log("");
const killedCount = results.filter((r) => r.verdict === "KILLED").length;
console.log(
  `${killedCount}/${results.length} killed, ` +
    `${results.length - killedCount - survivors.length} equivalent, ` +
    `${survivors.length} hollow`,
);
process.exit(survivors.length === 0 ? 0 : 1);
