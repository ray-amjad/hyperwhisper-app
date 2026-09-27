/**
 * Mutation proof for the #1049 redaction in tests/license-routes.test.ts.
 *
 * Each entry below does ONE exact string replace in one route, runs that
 * route's test file (`test`, else license-routes.test.ts), then puts the
 * source back. A mutant that still PASSES
 * means the tests are hollow there.
 *
 * This file is a tool, not a test. `npm test` globs `tests/*.test.ts`, so it
 * is never picked up by the suite.
 *
 *   node tests/mutate-license-routes.mjs
 */
import { execFileSync } from "node:child_process";
import { readFileSync, writeFileSync } from "node:fs";

const TEST = "tests/license-routes.test.ts";
const CREDIT_TEST = "tests/credit-routes.test.ts";

const VALIDATE = "app/api/license/validate/route.ts";
const ACTIVATE = "app/api/license/activate/route.ts";
const CHECKOUT = "app/api/checkout/credits/route.ts";
const CREDITS = "app/api/license/credits/route.ts";

const MUTANTS = [
  {
    file: VALIDATE,
    name: "log the raw device-tracking error",
    from: 'console.error("Device tracking error:", describeDbError(error));',
    to: 'console.error("Device tracking error:", error);',
  },
  {
    file: VALIDATE,
    name: "log the raw validation error",
    from: 'console.error("License validation error:", describeDbError(error));',
    to: 'console.error("License validation error:", error);',
  },
  {
    file: ACTIVATE,
    name: "log the raw activation error",
    from: 'console.error("License activation error:", describeDbError(error));',
    to: 'console.error("License activation error:", error);',
  },
  {
    file: CHECKOUT,
    test: CREDIT_TEST,
    name: "log the raw checkout error",
    from: 'console.error("Credit checkout error:", describeDbError(error));',
    to: 'console.error("Credit checkout error:", error);',
  },
  {
    file: CHECKOUT,
    test: CREDIT_TEST,
    name: "send a DB error's message as details",
    from: 'details: safeErrorMessage(error, "Unknown error"),',
    to: 'details: error instanceof Error ? error.message : "Unknown error",',
  },
  {
    file: CREDITS,
    test: CREDIT_TEST,
    name: "log the raw balance error",
    from: 'console.error("Credits balance error:", describeDbError(error));',
    to: 'console.error("Credits balance error:", error);',
  },
  {
    file: CREDITS,
    test: CREDIT_TEST,
    name: "log the raw decrement error",
    from: 'console.error("Credit deduction failed:", describeDbError(updateError));',
    to: 'console.error("Credit deduction failed:", updateError);',
  },
  {
    file: CREDITS,
    test: CREDIT_TEST,
    name: "log the raw deduction error",
    from: 'console.error("Credits deduction error:", describeDbError(error));',
    to: 'console.error("Credits deduction error:", error);',
  },
];

const results = [];

for (const mutant of MUTANTS) {
  const original = readFileSync(mutant.file, "utf8");
  const occurrences = original.split(mutant.from).length - 1;
  if (occurrences !== 1) {
    results.push({ ...mutant, verdict: `SKIPPED (${occurrences} matches)` });
    console.log(`SKIPPED   ${mutant.name}`);
    continue;
  }

  writeFileSync(mutant.file, original.replace(mutant.from, mutant.to));

  let killed = false;
  try {
    execFileSync(
      "node",
      ["--import", "tsx", "--experimental-test-module-mocks", "--test", mutant.test ?? TEST],
      { stdio: "pipe" },
    );
  } catch {
    killed = true;
  } finally {
    writeFileSync(mutant.file, original);
  }

  const verdict = killed ? "KILLED" : "SURVIVED";
  results.push({ ...mutant, verdict });
  console.log(`${verdict.padEnd(9)} ${mutant.name}`);
}

const survivors = results.filter((r) => r.verdict !== "KILLED");
console.log("");
console.log(`| File | Mutation | Verdict |`);
console.log(`| --- | --- | --- |`);
for (const r of results) console.log(`| \`${r.file}\` | ${r.name} | ${r.verdict} |`);
console.log("");
console.log(`${results.length - survivors.length}/${results.length} killed, ${survivors.length} hollow or skipped`);
process.exit(survivors.length === 0 ? 0 : 1);
