/**
 * Mutation proof for the #1049 redaction in tests/license-routes.test.ts.
 *
 * Each entry below does ONE exact string replace in one license route, runs
 * the one test file, then puts the source back. A mutant that still PASSES
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

const VALIDATE = "app/api/license/validate/route.ts";
const ACTIVATE = "app/api/license/activate/route.ts";

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

  let mutated = original.replace(mutant.from, mutant.to);
  if (mutant.prelude) {
    mutated = mutated.replace(mutant.prelude.from, mutant.prelude.to);
  }
  writeFileSync(mutant.file, mutated);

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
    writeFileSync(mutant.file, original);
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
console.log(`| File | Mutation | Verdict |`);
console.log(`| --- | --- | --- |`);
for (const r of results) console.log(`| \`${r.file}\` | ${r.name} | ${r.verdict} |`);
console.log("");
const killedCount = results.filter((r) => r.verdict === "KILLED").length;
console.log(
  `${killedCount}/${results.length} killed, ` +
    `${results.length - killedCount - survivors.length} equivalent, ` +
    `${survivors.length} hollow`,
);
process.exit(survivors.length === 0 ? 0 : 1);
