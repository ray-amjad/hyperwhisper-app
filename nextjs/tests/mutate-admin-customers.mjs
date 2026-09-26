/**
 * Mutation proof for tests/admin-customers.test.ts.
 *
 * Each entry below does ONE exact string replace in
 * `server/api/routers/admin/customers.ts`, runs the one test file, then puts
 * the source back. The access mutants swap `adminProcedure` for
 * `protectedProcedure`, so a signed-in customer who is not an admin gets in.
 * A mutant that still PASSES means the tests are hollow there.
 *
 * This file is a tool, not a test. `npm test` globs `tests/*.test.ts`, so it
 * is never picked up by the suite.
 *
 *   node tests/mutate-admin-customers.mjs
 */
import { execFileSync } from "node:child_process";
import { readFileSync, writeFileSync } from "node:fs";

const TEST = "tests/admin-customers.test.ts";
const FILE = "server/api/routers/admin/customers.ts";

const MUTANTS = [
  // --- auth -----------------------------------------------------------------
  { name: "grant: open to any signed-in user", from: "  grant: adminProcedure", to: "  grant: require(\"../../trpc\").protectedProcedure" },
  { name: "addCredits: open to any signed-in user", from: "  addCredits: adminProcedure", to: "  addCredits: require(\"../../trpc\").protectedProcedure" },
  { name: "refund: open to any signed-in user", from: "  refund: adminProcedure", to: "  refund: require(\"../../trpc\").protectedProcedure" },
  { name: "updateEmail: open to any signed-in user", from: "  updateEmail: adminProcedure", to: "  updateEmail: require(\"../../trpc\").protectedProcedure" },
  { name: "list: open to any signed-in user", from: "  list: adminProcedure", to: "  list: require(\"../../trpc\").protectedProcedure" },
  // --- grant ----------------------------------------------------------------
  { name: "grant: accept a colliding key", from: "        if (!existing) {\n          key = candidate;", to: "        if (true) {\n          key = candidate;" },
  { name: "grant: give up after 4 tries, not 5", from: "if (i === 4) throw", to: "if (i === 3) throw" },
  { name: "grant: insert an active key, not granted", from: '        status: "granted",', to: '        status: "active",' },
  { name: "grant: bundle of 500 credits, not 5000", from: "        amount: 5000,", to: "        amount: 500," },
  { name: "grant: wrong grant source type", from: 'sourceType: "admin_license_bundle"', to: 'sourceType: "admin_manual"' },
  { name: "grant: grant keyed on the user id, not the key id", from: "        sourceId: license.id,", to: "        sourceId: license.userId," },
  { name: "grant: carry on when the user is not created", from: "      if (!user) {", to: "      if (false) {" },
  { name: "grant: carry on when the key insert fails", from: "      if (!license) {\n        throw new TRPCError({ code: \"INTERNAL_SERVER_ERROR\", message: \"Failed to insert", to: "      if (false) {\n        throw new TRPCError({ code: \"INTERNAL_SERVER_ERROR\", message: \"Failed to insert" },
  { name: "grant: mail a different key than the one stored", from: "        licenseKey: key,", to: "        licenseKey: generateLicenseKey()," },
  // --- addCredits -----------------------------------------------------------
  { name: "addCredits: lift the 1,000,000 cap", from: ".max(MAX_ADMIN_CREDIT_GRANT)", to: "" },
  { name: "addCredits: allow zero or negative", from: "z.number().positive()", to: "z.number()" },
  { name: "addCredits: grant to the key id, not the owning user", from: "        userId: license.userId,\n        amount,", to: "        userId: license.id,\n        amount," },
  { name: "addCredits: read the balance of the key id", from: "getCreditBalance(license.userId)", to: "getCreditBalance(license.id)" },
  { name: "addCredits: reuse one source id for every grant", from: "        sourceId: crypto.randomUUID(),", to: "        sourceId: licenseKeyId," },
  { name: "addCredits: report the previous balance as the new one", from: "newBalance: grantResult.balance,", to: "newBalance: currentBalance," },
  { name: "addCredits: carry on for an unknown key", from: "      const license = await findAccountById(licenseKeyId);\n      if (!license) {\n        throw new TRPCError({\n", to: "      const license = (await findAccountById(licenseKeyId)) ?? { id: licenseKeyId, userId: \"\" };\n      if (false) {\n        throw new TRPCError({\n" },
  // --- refund ---------------------------------------------------------------
  { name: "refund: go ahead with no Stripe session", from: "      if (!license.stripeSessionId) {", to: "      if (false) {" },
  { name: "refund: keep the bundle credits", from: "      await refundCreditGrant({", to: "      if (false) await refundCreditGrant({" },
  { name: "refund: reverse the wrong grant type", from: '        sourceType: "license_bundle",', to: '        sourceType: "credit_pack",' },
  { name: "refund: always revoke", from: "      if (revokeLicense) {", to: "      if (true) {" },
  { name: "refund: never revoke", from: "      if (revokeLicense) {", to: "      if (false) {" },
  { name: "refund: sign the acting admin out too", from: "        await revokeAccountKey(licenseKeyId, license.userId, {\n          actingUserId: ctx.user.id,\n        });", to: "        await revokeAccountKey(licenseKeyId, license.userId, {});" },
  { name: "refund: pin the refund client to a newer API", from: 'apiVersion: "2025-02-24.acacia"', to: 'apiVersion: "2025-09-30.clover"' },
  // --- updateEmail ----------------------------------------------------------
  { name: "updateEmail: keep the case", from: "const email = input.newEmail.toLowerCase().trim();", to: "const email = input.newEmail.trim();" },
  { name: "updateEmail: skip the collision check", from: "      if (existing && existing.id !== input.userId) {", to: "      if (false) {" },
  { name: "updateEmail: block the same account too", from: "      if (existing && existing.id !== input.userId) {", to: "      if (existing) {" },
  { name: "updateEmail: case-sensitive no-op check", from: "if (target.email.toLowerCase() === email) {", to: "if (target.email === email) {" },
  { name: "updateEmail: never a no-op", from: "if (target.email.toLowerCase() === email) {", to: "if (false) {" },
  { name: "updateEmail: carry on for an unknown customer", from: "      if (!target) {", to: "      if (false) {" },
  { name: "updateEmail: drop the acting admin", from: "        await updateCustomerEmail(input.userId, email, {\n          actingUserId: ctx.user.id,\n        });", to: "        await updateCustomerEmail(input.userId, email, {});" },
  { name: "updateEmail: 23505 becomes a 500", from: 'if (dbErrorCode(error) === "23505") {', to: 'if (dbErrorCode(error) === "23503") {' },
  // --- list -----------------------------------------------------------------
  { name: "list: sum the pooled balance per key", from: "customer.totalCredits = l.credits;", to: "customer.totalCredits += l.credits;" },
  { name: "list: keep the latest date, not the earliest", from: "if (created < customer.created) customer.created = created;", to: "if (created > customer.created) customer.created = created;" },
  { name: "list: count only the first key", from: "customer.licenseCount += 1;", to: "customer.licenseCount = 1;" },
  { name: "list: show the key email over the user email", from: "email: (userMap.get(l.userId)?.email ?? l.email).toLowerCase(),", to: "email: (l.email ?? userMap.get(l.userId)?.email).toLowerCase()," },
  { name: "list: keep the email's case", from: "email: (userMap.get(l.userId)?.email ?? l.email).toLowerCase(),", to: "email: userMap.get(l.userId)?.email ?? l.email," },
  { name: "list: ignore a failed Stripe fetch", from: "              hadFailedFetch = true;", to: "              continue;" },
  { name: "list: report a partial total on failure", from: "customer.totalSpentCents = hadFailedFetch ? null : totalSpentCents;", to: "customer.totalSpentCents = totalSpentCents;" },
  { name: "list: count only the first Stripe id", from: "            totalSpentCents += cents;", to: "            totalSpentCents = cents;" },
  { name: "list: order by key order, not page order", from: "        const customers = userIds\n          .map", to: "        const customers = Array.from(customerMap.keys())\n          .map" },
  { name: "list: page size 50", from: "const CUSTOMERS_PAGE_SIZE = 100;", to: "const CUSTOMERS_PAGE_SIZE = 50;" },
  { name: "list: do not trim the search", from: "const search = input?.search?.trim();", to: "const search = input?.search;" },
  { name: "list: report the requested page, not the clamped one", from: "          page,\n          pageSize: CUSTOMERS_PAGE_SIZE,\n          totalPages", to: "          page: requestedPage,\n          pageSize: CUSTOMERS_PAGE_SIZE,\n          totalPages" },
  { name: "list: 0 pages for an empty result", from: "totalPages: Math.max(1, Math.ceil(", to: "totalPages: Math.max(0, Math.ceil(" },
  { name: "list: floor the page count", from: "Math.ceil(totalCustomers / CUSTOMERS_PAGE_SIZE)", to: "Math.floor(totalCustomers / CUSTOMERS_PAGE_SIZE)" },
  { name: "list: spend from dispute-lost charges too (shared reader wiring)", from: "    return disputes.data[0]?.status ?? null;", to: '    return "won";' },
  // --- #1039 redaction ------------------------------------------------------
  { name: "list: log the raw drizzle error", from: 'console.error("Customers fetch error:", describeDbError(error));', to: 'console.error("Customers fetch error:", error);' },
  { name: "updateEmail: log the raw drizzle error", from: 'console.error("Update customer email error:", describeDbError(error));', to: 'console.error("Update customer email error:", error);' },
  { name: "list: return a DB error's raw message", from: 'error instanceof Error && !isDbError(error)\n              ? error.message\n              : "Failed to fetch customers"', to: 'error instanceof Error\n              ? error.message\n              : "Failed to fetch customers"' },
  { name: "updateEmail: return a DB error's raw message", from: 'error instanceof Error && !isDbError(error)\n              ? error.message\n              : "Failed to update email"', to: 'error instanceof Error\n              ? error.message\n              : "Failed to update email"' },
  { name: "list: hide a non-DB error's message too", from: 'error instanceof Error && !isDbError(error)\n              ? error.message\n              : "Failed to fetch customers"', to: 'false\n              ? error.message\n              : "Failed to fetch customers"' },
  { name: "updateEmail: hide a non-DB error's message too", from: 'error instanceof Error && !isDbError(error)\n              ? error.message\n              : "Failed to update email"', to: 'false\n              ? error.message\n              : "Failed to update email"' },
];

const results = [];

for (const mutant of MUTANTS) {
  const original = readFileSync(FILE, "utf8");
  const from = mutant.from;
  const to = mutant.to;
  const occurrences = original.split(from).length - 1;
  if (occurrences !== 1) {
    results.push({ ...mutant, verdict: `SKIPPED (${occurrences} matches)` });
    console.log(`SKIPPED   ${mutant.name}`);
    continue;
  }

  writeFileSync(FILE, original.replace(from, to));

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
for (const r of results)
  console.log(`| ${r.name} | ${r.verdict} |`);
console.log("");
const killedCount = results.filter((r) => r.verdict === "KILLED").length;
console.log(
  `${killedCount}/${results.length} killed, ` +
    `${results.length - killedCount - survivors.length} equivalent, ` +
    `${survivors.length} hollow`,
);
process.exit(survivors.length === 0 ? 0 : 1);
