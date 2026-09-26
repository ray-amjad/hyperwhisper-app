/**
 * Mutation proof for tests/stripe-webhook-route.test.ts.
 *
 * Each entry below does ONE exact string replace in
 * app/api/webhooks/stripe/route.ts, runs the one test file, then puts the
 * source back. A mutant that still PASSES means the tests are hollow there.
 *
 * This file is a tool, not a test. `npm test` globs `tests/*.test.ts`, so it
 * is never picked up by the suite.
 *
 *   node tests/mutate-stripe-webhook-route.mjs
 */
import { execFileSync } from "node:child_process";
import { readFileSync, writeFileSync } from "node:fs";

const SOURCE = "app/api/webhooks/stripe/route.ts";
const TEST = "tests/stripe-webhook-route.test.ts";

const MUTANTS = [
  {
    name: "accept a request with no signature header",
    from: "  if (!signature) {",
    to: "  if (false) {",
  },
  {
    name: "treat a missing STRIPE_WEBHOOK_SECRET as fine",
    from: "  if (!webhookSecret) {",
    to: "  if (false) {",
  },
  {
    name: "answer 200 instead of 500 when the secret is missing",
    from: '      { error: "Webhook secret not configured" },\n      { status: 500 }',
    to: '      { error: "Webhook secret not configured" },\n      { status: 200 }',
  },
  {
    name: "verify with a different secret",
    from: "constructEvent(body, signature, webhookSecret)",
    to: 'constructEvent(body, signature, "other-secret")',
  },
  {
    name: "verify a re-serialized body instead of the raw bytes",
    from: "constructEvent(body, signature, webhookSecret)",
    to: "constructEvent(JSON.stringify(JSON.parse(body)), signature, webhookSecret)",
  },
  {
    name: "accept a body whose signature did not verify",
    from: '      { error: "Webhook signature verification failed" },\n      { status: 400 }',
    to: '      { error: "Webhook signature verification failed" },\n      { status: 200 }',
  },
  {
    name: "invert the payment gate",
    from: 'if (session.payment_status !== "paid") {',
    to: 'if (session.payment_status === "paid") {',
  },
  {
    name: "drop the payment gate entirely",
    from: 'if (session.payment_status !== "paid") {',
    to: "if (false) {",
  },
  {
    name: "mis-spell the license purchase type",
    from: 'if (purchaseType === "license") {',
    to: 'if (purchaseType === "licence") {',
  },
  {
    name: "route credits to the license handler",
    from: '} else if (purchaseType === "credits") {',
    to: '} else if (purchaseType === "license") {',
  },
  {
    name: "swap the credit handler's event id and event type",
    from: "await handleCreditPurchase(session, event.id, event.type);",
    to: "await handleCreditPurchase(session, event.type, event.id);",
  },
  {
    name: "ignore checkout.session.async_payment_succeeded",
    from: '    event.type === "checkout.session.async_payment_succeeded"',
    to: '    event.type === "checkout.session.never_emitted"',
  },
  {
    name: "answer 200 when the license handler throws",
    from: '          { error: "Failed to process license purchase" },\n          { status: 500 }',
    to: '          { error: "Failed to process license purchase" },\n          { status: 200 }',
  },
  {
    name: "answer 200 when the credit handler throws",
    from: '          { error: "Failed to process credit purchase" },\n          { status: 500 }',
    to: '          { error: "Failed to process credit purchase" },\n          { status: 200 }',
  },
  {
    name: "ignore charge.refunded",
    from: 'if (event.type === "charge.refunded") {',
    to: 'if (event.type === "charge.refunded.never") {',
  },
  {
    name: "answer 500 when the refund handler throws",
    from: '        describeDbError(error),\n      );\n      // Don\'t return error status',
    to: '        describeDbError(error),\n      );\n      return NextResponse.json({ error: "refund failed" }, { status: 500 });\n      // Don\'t return error status',
  },
  {
    name: "drop the async_payment_failed log line",
    from: "        `Stripe webhook: async credit payment failed for session ${session.id}`,",
    to: "        `Stripe webhook: async credit payment failed`,",
  },
];

const original = readFileSync(SOURCE, "utf8");
const results = [];

for (const mutant of MUTANTS) {
  const occurrences = original.split(mutant.from).length - 1;
  if (occurrences !== 1) {
    results.push({ ...mutant, verdict: `SKIPPED (${occurrences} matches)` });
    continue;
  }

  writeFileSync(SOURCE, original.replace(mutant.from, mutant.to));

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
    writeFileSync(SOURCE, original);
  }

  results.push({ ...mutant, verdict: killed ? "KILLED" : "SURVIVED" });
  console.log(`${killed ? "KILLED  " : "SURVIVED"}  ${mutant.name}`);
}

const survivors = results.filter((r) => r.verdict !== "KILLED");
console.log("");
console.log(`| Mutation | Verdict |`);
console.log(`| --- | --- |`);
for (const r of results) console.log(`| ${r.name} | ${r.verdict} |`);
console.log("");
console.log(`${results.length - survivors.length}/${results.length} killed`);
process.exit(survivors.length === 0 ? 0 : 1);
