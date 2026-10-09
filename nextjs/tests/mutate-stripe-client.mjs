/**
 * Mutation proof for tests/stripe-client.test.ts.
 *
 * Each entry below does ONE exact string replace in the Stripe client,
 * runs the one test file, then puts the source back. A mutant that still
 * PASSES means the tests are hollow there.
 *
 * This file is a tool, not a test. `npm test` globs `tests/*.test.ts`, so it
 * is never picked up by the suite.
 *
 *   node tests/mutate-stripe-client.mjs
 */
import { execFileSync } from "node:child_process";
import { readFileSync, writeFileSync } from "node:fs";

const TEST = "tests/stripe-client.test.ts";
const CLIENT = "lib/clients/stripe.ts";

const MUTANTS = [
  {
    name: "drop the Managed Payments preview suffix",
    from: 'apiVersion: "2025-12-15.clover; managed_payments_preview=v1",',
    to: 'apiVersion: "2025-12-15.clover",',
  },
  {
    name: "pin a different API version",
    from: 'apiVersion: "2025-12-15.clover; managed_payments_preview=v1",',
    to: 'apiVersion: "2025-11-17.clover; managed_payments_preview=v1",',
  },
  {
    name: "remove the version pin",
    from: '  apiVersion: "2025-12-15.clover; managed_payments_preview=v1",\n',
    to: "",
  },
  {
    name: "read the key from another env var",
    from: "new Stripe(process.env.STRIPE_SECRET_KEY!, {",
    to: "new Stripe(process.env.STRIPE_PUBLISHABLE_KEY!, {",
  },
  {
    name: "fall back to a placeholder key when none is set",
    from: "new Stripe(process.env.STRIPE_SECRET_KEY!, {",
    to: 'new Stripe(process.env.STRIPE_SECRET_KEY ?? "placeholder", {',
  },
];

const original = readFileSync(CLIENT, "utf8");
let survivors = 0;
for (const m of MUTANTS) {
  if (!original.includes(m.from)) throw new Error(`mutant "${m.name}": source text not found`);
  writeFileSync(CLIENT, original.replace(m.from, m.to));
  let killed = false;
  try {
    execFileSync(
      process.execPath,
      ["--import", "tsx", "--experimental-test-module-mocks", "--test", TEST],
      { stdio: "pipe" },
    );
  } catch {
    killed = true;
  } finally {
    writeFileSync(CLIENT, original);
  }
  if (!killed) survivors++;
  console.log(`${killed ? "KILLED  " : "SURVIVED"} ${m.name}`);
}
console.log(survivors === 0 ? "all mutants killed" : `${survivors} mutant(s) survived`);
process.exit(survivors === 0 ? 0 : 1);
