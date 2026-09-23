/**
 * Mutation proof for tests/customer-portal.test.ts.
 *
 * Each entry below does ONE exact string replace in `server/api/trpc.ts` or
 * `server/api/routers/customer.ts`, runs the one test file, then puts the
 * source back. A mutant that still PASSES means the tests are hollow there.
 *
 * This file is a tool, not a test. `npm test` globs `tests/*.test.ts`, so it
 * is never picked up by the suite.
 *
 *   node tests/mutate-customer-portal.mjs
 */
import { execFileSync } from "node:child_process";
import { readFileSync, writeFileSync } from "node:fs";

const TEST = "tests/customer-portal.test.ts";

const TRPC = "server/api/trpc.ts";
const CUSTOMER = "server/api/routers/customer.ts";

const MUTANTS = [
  // --- server/api/trpc.ts ---------------------------------------------------
  {
    file: TRPC,
    name: "make every signed-in user an admin",
    from: 'const isAdmin = user?.role === "admin";',
    to: "const isAdmin = !!user;",
  },
  {
    file: TRPC,
    name: "treat any role containing admin as admin",
    from: 'const isAdmin = user?.role === "admin";',
    to: 'const isAdmin = !!user?.role?.includes("admin");',
  },
  {
    file: TRPC,
    name: "drop the request headers from the context",
    from: "return { user, isAdmin, headers: opts.headers };",
    to: "return { user, isAdmin, headers: new Headers() };",
  },
  {
    file: TRPC,
    name: "let a signed-out caller through protectedProcedure",
    from: "const isAuthed = t.middleware(({ ctx, next }) => {\n  if (!ctx.user) {",
    to: "const isAuthed = t.middleware(({ ctx, next }) => {\n  if (false) {",
  },
  {
    file: TRPC,
    name: "let a signed-out caller through adminProcedure",
    from: 'const isAdmin = t.middleware(({ ctx, next }) => {\n  if (!ctx.user) {\n    throw new TRPCError({\n      code: "UNAUTHORIZED",',
    to: 'const isAdmin = t.middleware(({ ctx, next }) => {\n  if (false) {\n    throw new TRPCError({\n      code: "UNAUTHORIZED",',
  },
  {
    file: TRPC,
    name: "let a signed-in non-admin through adminProcedure",
    from: "  if (!ctx.isAdmin) {",
    to: "  if (false) {",
  },
  {
    file: TRPC,
    name: "answer UNAUTHORIZED instead of FORBIDDEN to a non-admin",
    from: '  if (!ctx.isAdmin) {\n    throw new TRPCError({\n      code: "FORBIDDEN",',
    to: '  if (!ctx.isAdmin) {\n    throw new TRPCError({\n      code: "UNAUTHORIZED",',
  },
  {
    file: TRPC,
    name: "give adminProcedure no admin check at all",
    from: "export const adminProcedure = t.procedure.use(isAdmin);",
    to: "export const adminProcedure = t.procedure.use(isAuthed);",
  },

  // --- server/api/routers/customer.ts --------------------------------------
  {
    file: CUSTOMER,
    name: "bill the dashboard per key, double-counting a pooled balance",
    from: "    const totalCredits = userIds.reduce(\n      (sum, uid) => sum + (balanceMap.get(uid) || 0),\n      0\n    );",
    to: "    const totalCredits = licensesWithCredits.reduce(\n      (sum, l) => sum + l.credits,\n      0\n    );",
  },
  {
    file: CUSTOMER,
    name: "ask for the balance once per key instead of per owner",
    from: "    const userIds = Array.from(new Set(licenses.map((l) => l.userId)));\n    const balanceMap = await getCreditBalancesForUsers(userIds);\n\n    const licensesWithCredits",
    to: "    const userIds = licenses.map((l) => l.userId);\n    const balanceMap = await getCreditBalancesForUsers(userIds);\n\n    const licensesWithCredits",
  },
  {
    file: CUSTOMER,
    name: "count a pooled balance once per key in the credits total",
    from: "    const userIds = Array.from(new Set(licenses.map((l) => l.userId)));\n    const balanceMap = await getCreditBalancesForUsers(userIds);\n\n    let totalCredits = 0;",
    to: "    const userIds = licenses.map((l) => l.userId);\n    const balanceMap = await getCreditBalancesForUsers(userIds);\n\n    let totalCredits = 0;",
  },
  {
    file: CUSTOMER,
    name: "round the remaining minutes up instead of down",
    from: "    const minutesRemaining = Math.floor(totalCredits / CREDITS_PER_MINUTE);",
    to: "    const minutesRemaining = Math.ceil(totalCredits / CREDITS_PER_MINUTE);",
  },
  {
    file: CUSTOMER,
    name: "round a key's minutes up instead of down",
    from: "        minutesRemaining: Math.floor(credits / CREDITS_PER_MINUTE),",
    to: "        minutesRemaining: Math.ceil(credits / CREDITS_PER_MINUTE),",
  },
  {
    file: CUSTOMER,
    name: "change the credits-per-minute rate",
    from: "const CREDITS_PER_MINUTE = 1.67;",
    to: "const CREDITS_PER_MINUTE = 1.6667;",
  },
  {
    file: CUSTOMER,
    name: "look the email up without lowercasing it",
    from: "  licensesWithCredits: protectedProcedure.query(async ({ ctx }) => {\n    const userEmail = ctx.user.email?.toLowerCase();",
    to: "  licensesWithCredits: protectedProcedure.query(async ({ ctx }) => {\n    const userEmail = ctx.user.email;",
  },
  {
    file: CUSTOMER,
    name: "serve a zero balance instead of refusing an emailless user",
    from: '    if (!userEmail) {\n      throw new TRPCError({\n        code: "BAD_REQUEST",\n        message: "User email not found",\n      });\n    }\n\n    const licenses = await getAccountKeysByEmail(userEmail);\n\n    if (licenses.length === 0) {\n      return {\n        totalCredits: 0,',
    to: '    if (!userEmail) {\n      return {\n        totalCredits: 0,\n        minutesRemaining: 0,\n        creditsPerMinute: CREDITS_PER_MINUTE,\n      };\n    }\n\n    const licenses = await getAccountKeysByEmail(userEmail);\n\n    if (licenses.length === 0) {\n      return {\n        totalCredits: 0,',
  },
  {
    file: CUSTOMER,
    name: "read a key's credits as zero",
    from: "      const credits = balanceMap.get(license.userId) || 0;",
    to: "      const credits = 0;",
  },
  {
    file: CUSTOMER,
    name: "hide the Stripe customer id from the dashboard",
    from: "        stripeCustomerId: license.stripeCustomerId,",
    to: "        stripeCustomerId: null,",
  },
  {
    file: CUSTOMER,
    name: "report the epoch as the key's creation time",
    from: "        createdAt: license.createdAt.toISOString(),",
    to: "        createdAt: new Date(0).toISOString(),",
  },
  {
    file: CUSTOMER,
    name: "read the credit history for every key, not every owner",
    from: "      Array.from(new Set(licenses.map((l) => l.userId)))",
    to: "      licenses.map((l) => l.userId)",
  },
  {
    file: CUSTOMER,
    name: "read the credit history even when the account has no keys",
    from: "    if (licenses.length === 0) {\n      return { grants: [] };\n    }",
    to: "",
  },
  {
    file: CUSTOMER,
    name: "report Polar history as Stripe history",
    from: "      hasStripe: licenses.some((l) => !!l.stripeCustomerId),",
    to: "      hasStripe: licenses.some((l) => !!l.polarCustomerId),",
  },
  {
    file: CUSTOMER,
    name: "report Polar history for every account",
    from: "      hasPolar: licenses.some((l) => !!l.polarCustomerId),",
    to: "      hasPolar: true,",
  },
  {
    file: CUSTOMER,
    name: "open the portal for the wrong key when the first has no customer",
    from: "    const licenseWithStripe = licenses.find((l) => l.stripeCustomerId);",
    to: "    const licenseWithStripe = licenses[licenses.length - 1];",
  },
  {
    file: CUSTOMER,
    name: "open a portal session for an account with no Stripe history",
    from: "    if (!licenseWithStripe?.stripeCustomerId) {",
    to: "    if (false) {",
  },
  {
    file: CUSTOMER,
    name: "send the customer back to the wrong site after billing",
    from: '      process.env.NEXT_PUBLIC_SITE_URL || "https://hyperwhisper.com";',
    to: '      "https://hyperwhisper.com";',
  },
  {
    file: CUSTOMER,
    name: "return the customer to the home page instead of the account page",
    from: "      return_url: `${siteUrl}/user`,",
    to: "      return_url: siteUrl,",
  },
];

const results = [];

/**
 * Both sources are stored with CRLF line endings. A multi-line pattern written
 * with plain `\n` matches nothing there and the mutant is skipped silently, so
 * lift the pattern to the file's own line ending before the search.
 */
function forFile(pattern, source) {
  return source.includes("\r\n") ? pattern.replace(/\n/g, "\r\n") : pattern;
}

for (const mutant of MUTANTS) {
  const original = readFileSync(mutant.file, "utf8");
  const from = forFile(mutant.from, original);
  const to = forFile(mutant.to, original);
  const occurrences = original.split(from).length - 1;
  if (occurrences !== 1) {
    results.push({ ...mutant, verdict: `SKIPPED (${occurrences} matches)` });
    console.log(`SKIPPED   ${mutant.name}`);
    continue;
  }

  writeFileSync(mutant.file, original.replace(from, to));

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
for (const r of results)
  console.log(`| \`${r.file}\` | ${r.name} | ${r.verdict} |`);
console.log("");
const killedCount = results.filter((r) => r.verdict === "KILLED").length;
console.log(
  `${killedCount}/${results.length} killed, ` +
    `${results.length - killedCount - survivors.length} equivalent, ` +
    `${survivors.length} hollow`,
);
process.exit(survivors.length === 0 ? 0 : 1);
