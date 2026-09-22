/**
 * Mutation proof for tests/license-read-routes.test.ts.
 *
 * Each entry below does ONE exact string replace in one of the four route
 * sources, runs the one test file, then puts the source back. A mutant that
 * still PASSES means the tests are hollow there.
 *
 * This file is a tool, not a test. `npm test` globs `tests/*.test.ts`, so it
 * is never picked up by the suite.
 *
 *   node tests/mutate-license-read-routes.mjs
 */
import { execFileSync } from "node:child_process";
import { readFileSync, writeFileSync } from "node:fs";

const TEST = "tests/license-read-routes.test.ts";

const GRANT = "app/api/internal/grant-license/route.ts";
const EMAILS = "app/api/internal/granted-emails/route.ts";
const LIST = "app/api/internal/licenses-for-email/route.ts";
const PROFILE = "app/api/customer/profile/route.ts";

const MUTANTS = [
  {
    file: GRANT,
    name: "hand back a revoked key instead of minting",
    from: 'const granted = existing.find((l) => l.status === "granted");',
    to: "const granted = existing[0];",
  },
  {
    file: GRANT,
    name: "take the OLDEST granted key",
    from: 'const granted = existing.find((l) => l.status === "granted");',
    to: 'const granted = [...existing].reverse().find((l) => l.status === "granted");',
  },
  {
    file: GRANT,
    name: "mint even when a granted key exists",
    from: "    if (granted) {",
    to: "    if (false) {",
  },
  {
    file: GRANT,
    name: "skip the secret and email gate",
    from: 'if ("response" in parsed) return parsed.response;',
    to: 'if (false && "response" in parsed) return parsed.response;',
  },
  {
    file: GRANT,
    name: "answer 200 instead of 500 on a database fault",
    from: 'return NextResponse.json({ error: "Internal server error" }, { status: 500 });',
    to: 'return NextResponse.json({ error: "Internal server error" }, { status: 200 });',
  },
  {
    file: EMAILS,
    name: "accept any x-internal-secret",
    from: "if (!timingSafeEqualSecret(secret, process.env.HYPERWHISPER_INTERNAL_SECRET)) {",
    to: "if (false) {",
  },
  {
    file: EMAILS,
    name: "answer 200 instead of 401 on a bad secret",
    from: 'return NextResponse.json({ error: "Unauthorized" }, { status: 401 });',
    to: 'return NextResponse.json({ error: "Unauthorized" }, { status: 200 });',
  },
  {
    file: EMAILS,
    name: "swallow a read fault and answer an empty list",
    from: "    const emails = await getGrantedEmails();",
    to: "    const emails = await getGrantedEmails().catch(() => []);",
  },
  {
    file: LIST,
    name: "show revoked keys in the list",
    from: 'const granted = all.filter((l) => l.status === "granted");',
    to: "const granted = all;",
  },
  {
    file: LIST,
    name: "ask for the balance once per key instead of per account",
    from: "Array.from(new Set(granted.map((l) => l.userId)))",
    to: "granted.map((l) => l.userId)",
  },
  {
    file: LIST,
    name: "report no credits when the account has a balance",
    from: "credits: balances.get(license.userId) ?? 0,",
    to: "credits: 0,",
  },
  {
    // EQUIVALENT MUTANT, kept on the record. `NextResponse.json` serialises a
    // `Date` through `JSON.stringify`, which emits the same ISO-8601 string
    // `toISOString()` does. The wire bytes do not change, so no test can kill
    // it. The mutant below it changes the value a caller reads, and dies.
    file: LIST,
    name: "send a Date instead of an ISO string (equivalent — same JSON on the wire)",
    from: "createdAt: license.createdAt.toISOString(),",
    to: "createdAt: license.createdAt,",
    equivalent: true,
  },
  {
    file: LIST,
    name: "report the epoch as the key's creation time",
    from: "createdAt: license.createdAt.toISOString(),",
    to: "createdAt: new Date(0).toISOString(),",
  },
  {
    file: LIST,
    name: "mint from the read-only route",
    from: "    const all = await getAccountKeysByEmail(email);",
    to: "    const all = await getAccountKeysByEmail(email);\n    if (all.length === 0) await provisionAccountKeyForEmail(email);",
    prelude: {
      from: '  getCreditBalancesForUsers,\n} from "@/src/lib/db-layer";',
      to: '  getCreditBalancesForUsers,\n  provisionAccountKeyForEmail,\n} from "@/src/lib/db-layer";',
    },
  },
  {
    file: PROFILE,
    name: "serve the profile to a signed-out caller",
    from: "if (!session?.user) {",
    to: "if (false) {",
  },
  {
    file: PROFILE,
    name: "sum the credits per license, double-counting a pooled balance",
    from: "    const totalCredits = userIds.reduce(\n      (sum, uid) => sum + (creditMap.get(uid) || 0),\n      0\n    );",
    to: "    const totalCredits = licensesWithCredits.reduce(\n      (sum, l) => sum + l.credits,\n      0\n    );",
  },
  {
    file: PROFILE,
    name: "look the email up without lowercasing it",
    from: 'const userEmail = user.email?.toLowerCase() ?? "";',
    to: 'const userEmail = user.email ?? "";',
  },
  {
    file: PROFILE,
    name: "drop stripe_customer_id from the reply",
    from: "      stripe_customer_id: license.stripeCustomerId,",
    to: "",
  },
  {
    file: PROFILE,
    name: "answer 200 instead of 500 on a database fault",
    from: '      { error: "Internal server error" },\n      { status: 500 }',
    to: '      { error: "Internal server error" },\n      { status: 200 }',
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
