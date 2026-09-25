/**
 * Mutation proof for tests/latency-routes.test.ts.
 *
 * Each entry below does ONE exact string replace in one of the two latency
 * route sources, runs the one test file, then puts the source back. A mutant
 * that still PASSES means the tests are hollow there.
 *
 * This file is a tool, not a test. `npm test` globs `tests/*.test.ts`, so it
 * is never picked up by the suite.
 *
 *   node tests/mutate-latency-routes.mjs
 */
import { execFileSync } from "node:child_process";
import { readFileSync, writeFileSync } from "node:fs";

const TEST = "tests/latency-routes.test.ts";

const INGEST = "app/api/internal/latency/route.ts";
const PRUNE = "app/api/internal/latency/prune/route.ts";

const MUTANTS = [
  {
    file: INGEST,
    name: "accept any x-internal-secret",
    from: "if (!timingSafeEqualSecret(secret, process.env.HYPERWHISPER_INTERNAL_SECRET)) {",
    to: "if (false) {",
  },
  {
    file: INGEST,
    name: "answer 200 instead of 401 on a bad secret",
    from: 'return NextResponse.json({ error: "Unauthorized" }, { status: 401 });',
    to: 'return NextResponse.json({ error: "Unauthorized" }, { status: 200 });',
  },
  {
    file: INGEST,
    name: "raise the body cap to 64 KB",
    from: "const MAX_BODY_BYTES = 32 * 1024;",
    to: "const MAX_BODY_BYTES = 64 * 1024;",
  },
  {
    file: INGEST,
    name: "ignore the declared content-length",
    from: "if (declaredLength > MAX_BODY_BYTES) {",
    to: "if (false) {",
  },
  {
    file: INGEST,
    name: "reject a body of exactly the cap",
    from: "if (text.length > MAX_BODY_BYTES) {",
    to: "if (text.length >= MAX_BODY_BYTES) {",
  },
  {
    file: INGEST,
    name: "answer 200 to a bad envelope",
    from: "return NextResponse.json({ error: result.error }, { status: 400 });",
    to: "return NextResponse.json({ error: result.error }, { status: 200 });",
  },
  {
    file: INGEST,
    name: "stop logging dropped samples",
    from: "if (result.skipped.length > 0) {",
    to: "if (false) {",
  },
  {
    file: INGEST,
    name: "log every skipped reason, not the distinct ones",
    from: "reasons: Array.from(new Set(result.skipped.map((entry) => entry.reason))),",
    to: "reasons: result.skipped.map((entry) => entry.reason),",
  },
  {
    file: INGEST,
    name: "report the received count without the skipped rows",
    from: "received: result.skipped.length + result.samples.length,",
    to: "received: result.samples.length,",
  },
  {
    file: INGEST,
    name: "rate-limit on the last sample's region",
    from: "const region = result.samples[0]?.flyRegion ?? \"unknown\";",
    to: "const region = result.samples[result.samples.length - 1]?.flyRegion ?? \"unknown\";",
  },
  {
    file: INGEST,
    name: "ignore the rate limiter",
    from: "  if (!success) {\n    return NextResponse.json({ error: \"Rate limit exceeded\" }, { status: 429 });",
    to: "  if (false) {\n    return NextResponse.json({ error: \"Rate limit exceeded\" }, { status: 429 });",
  },
  {
    file: INGEST,
    name: "issue an empty insert when every sample was dropped",
    from: "if (result.samples.length === 0) {",
    to: "if (false) {",
  },
  {
    file: INGEST,
    name: "stamp the exact instant instead of the hour",
    from: "const createdAt = coarseCreatedAt();",
    to: "const createdAt = new Date();",
  },
  {
    file: INGEST,
    name: "store the exact clip length",
    from: "        audioSeconds: null,",
    to: "        audioSeconds: sample.audioSeconds,",
  },
  {
    file: INGEST,
    name: "bucket every row as short",
    from: "durationBucket: bucketForSeconds(sample.audioSeconds ?? 0),",
    to: "durationBucket: bucketForSeconds(0),",
  },
  {
    file: INGEST,
    name: "store the failure kind on a success",
    from: "        failureKind: sample.failureKind,",
    to: "        failureKind: sample.failureKind ?? \"unknown\",",
  },
  {
    file: INGEST,
    name: "answer 400 instead of 500 on a database fault",
    from: "return NextResponse.json({ error: \"Internal server error\" }, { status: 500 });",
    to: "return NextResponse.json({ error: \"Internal server error\" }, { status: 400 });",
  },
  {
    file: INGEST,
    name: "report the batch size instead of the stored count",
    from: "    inserted: result.samples.length,\n    skipped: result.skipped,\n    max",
    to: "    inserted: result.samples.length + result.skipped.length,\n    skipped: result.skipped,\n    max",
  },
  {
    file: PRUNE,
    name: "accept the scheme case-insensitively",
    from: 'if (!header || !header.startsWith("Bearer ")) return null;',
    to: 'if (!header || !header.toLowerCase().startsWith("bearer ")) return null;',
  },
  {
    file: PRUNE,
    name: "accept a bare secret with no scheme",
    from: 'if (!header || !header.startsWith("Bearer ")) return null;',
    to: 'if (!header) return null;\n  if (!header.startsWith("Bearer ")) return header;',
  },
  {
    file: PRUNE,
    name: "stop trimming the bearer",
    from: 'return header.slice("Bearer ".length).trim();',
    to: 'return header.slice("Bearer ".length);',
  },
  {
    file: PRUNE,
    name: "drop the x-internal-secret branch",
    from: "    timingSafeEqualSecret(headerSecret, expected) ||\n",
    to: "",
  },
  {
    file: PRUNE,
    name: "drop the internal-secret bearer branch",
    from: "    timingSafeEqualSecret(bearer, expected) ||\n",
    to: "",
  },
  {
    file: PRUNE,
    name: "drop the cron-secret bearer branch",
    from: "    timingSafeEqualSecret(bearer, expected) ||\n    timingSafeEqualSecret(bearer, cronSecret);",
    to: "    timingSafeEqualSecret(bearer, expected);",
  },
  {
    file: PRUNE,
    name: "accept the cron secret in x-internal-secret too",
    from: "    timingSafeEqualSecret(bearer, cronSecret);",
    to: "    timingSafeEqualSecret(bearer, cronSecret) ||\n    timingSafeEqualSecret(headerSecret, cronSecret);",
  },
  {
    file: PRUNE,
    name: "log the bearer itself on a refusal",
    from: "      hasBearer: bearer !== null,",
    to: "      hasBearer: bearer,",
  },
  {
    file: PRUNE,
    name: "report the cron secret as always configured",
    from: "      cronSecretConfigured: Boolean(cronSecret),",
    to: "      cronSecretConfigured: true,",
  },
  {
    file: PRUNE,
    name: "keep rows for 30 days, not 365",
    from: "const RETENTION_DAYS = 365;",
    to: "const RETENTION_DAYS = 30;",
  },
  {
    file: PRUNE,
    name: "compute the cutoff in the future",
    from: "const cutoff = new Date(Date.now() - RETENTION_DAYS * 24 * 60 * 60 * 1000);",
    to: "const cutoff = new Date(Date.now() + RETENTION_DAYS * 24 * 60 * 60 * 1000);",
  },
  {
    file: PRUNE,
    name: "delete in batches of 50,000",
    from: "const BATCH_SIZE = 5_000;",
    to: "const BATCH_SIZE = 50_000;",
  },
  {
    file: PRUNE,
    name: "run unbounded batches per night",
    from: "const MAX_BATCHES = 20;",
    to: "const MAX_BATCHES = 1_000;",
  },
  {
    file: PRUNE,
    name: "delete rows NEWER than the cutoff",
    from: "where ${lt(sttLatencySamples.createdAt, cutoff)}",
    to: "where ${gte(sttLatencySamples.createdAt, cutoff)}",
    prelude: { from: 'import { lt, sql } from "drizzle-orm";', to: 'import { gte, lt, sql } from "drizzle-orm";' },
  },
  {
    file: PRUNE,
    name: "stop after one batch always",
    from: "if (count < BATCH_SIZE) break;",
    to: "break;",
  },
  {
    file: PRUNE,
    name: "stop only on an empty batch",
    from: "if (count < BATCH_SIZE) break;",
    to: "if (count === 0) break;",
  },
  {
    file: PRUNE,
    name: "report the last batch instead of the total",
    from: "      deleted += count;",
    to: "      deleted = count;",
  },
  {
    file: PRUNE,
    name: "answer 200 on a database fault",
    from: 'return NextResponse.json({ error: "Internal server error" }, { status: 500 });',
    to: 'return NextResponse.json({ error: "Internal server error" }, { status: 200 });',
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
