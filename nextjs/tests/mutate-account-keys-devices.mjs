/**
 * Mutation proof for tests/account-keys-devices.test.ts.
 *
 * Each entry below does ONE exact string replace in `src/lib/db-layer.ts`,
 * runs the one test file, then puts the source back. A mutant that still
 * PASSES means the tests are hollow there.
 *
 * This file is a tool, not a test. `npm test` globs `tests/*.test.ts`, so it
 * is never picked up by the suite.
 *
 *   node tests/mutate-account-keys-devices.mjs
 */
import { execFileSync } from "node:child_process";
import { readFileSync, writeFileSync } from "node:fs";

const TEST = "tests/account-keys-devices.test.ts";
const FILE = "src/lib/db-layer.ts";

const MUTANTS = [
  {
    name: "insert a key with no status as revoked",
    from: '      status: data.status ?? "granted",',
    to: '      status: data.status ?? "revoked",',
  },
  {
    name: "drop the Stripe session id on insert",
    from: "      stripeSessionId: data.stripeSessionId ?? null,\n    })\n    .returning();",
    to: "      stripeSessionId: null,\n    })\n    .returning();",
  },
  {
    name: "updateAccountKey ignores the status",
    from: "  if (updates.status !== undefined) values.status = updates.status;\n",
    to: "",
  },
  {
    name: "updateAccountKey treats null as no change",
    from: "  if (updates.stripeCustomerId !== undefined) values.stripeCustomerId",
    to: "  if (updates.stripeCustomerId != null) values.stripeCustomerId",
  },
  {
    name: "updateAccountKey ignores the email",
    from: "  if (updates.email !== undefined) values.email = updates.email;\n",
    to: "",
  },
  {
    name: "updateAccountKey updates every key",
    from: "  await db.update(accountKeys).set(values).where(eq(accountKeys.id, id));",
    to: "  await db.update(accountKeys).set(values);",
  },
  {
    name: "revokeWebAccess never skips the acting user",
    from: "  if (options.actingUserId === userId) return;",
    to: "",
  },
  {
    name: "revokeWebAccess always skips",
    from: "  if (options.actingUserId === userId) return;",
    to: "  return;",
  },
  {
    name: "revokeWebAccess deletes every session",
    from: "  await runner.delete(session).where(eq(session.userId, userId));",
    to: "  await runner.delete(session);",
  },
  {
    name: "revokeWebAccess ignores the transaction",
    from: "  const runner = options.tx ?? db;",
    to: "  const runner = db;",
  },
  {
    name: "revokeAccountKey writes no revoked status",
    from: '      .set({ status: "revoked" })\n      .where(eq(accountKeys.id, id));',
    to: '      .set({ status: "granted" })\n      .where(eq(accountKeys.id, id));',
  },
  {
    name: "revokeAccountKey revokes every key",
    from: '      .set({ status: "revoked" })\n      .where(eq(accountKeys.id, id));',
    to: '      .set({ status: "revoked" });',
  },
  {
    name: "revokeAccountKey drops the acting user",
    from: "      .where(eq(accountKeys.id, id));\n\n    await revokeWebAccess(userId, { ...options, tx });",
    to: "      .where(eq(accountKeys.id, id));\n\n    await revokeWebAccess(userId, { tx });",
  },
  {
    name: "findAccountByKey does not trim",
    from: "    where: eq(accountKeys.key, key.trim()),",
    to: "    where: eq(accountKeys.key, key),",
  },
  {
    name: "findAccountByKey matches case-insensitively",
    from: "    where: eq(accountKeys.key, key.trim()),",
    to: "    where: ilike(accountKeys.key, key.trim()),",
  },
  {
    name: "findAccountById matches nothing",
    from: "    where: eq(accountKeys.id, id),\n  });\n  return row ? drizzleAccountKeyToRow(row) : null;",
    to: "    where: eq(accountKeys.id, sql`gen_random_uuid()`),\n  });\n  return row ? drizzleAccountKeyToRow(row) : null;",
  },
  {
    name: "findAccountByStripeSession ignores the session id",
    from: "    where: eq(accountKeys.stripeSessionId, sessionId),",
    to: "    where: undefined,",
  },
  {
    name: "getAccountKeysByEmail does not lowercase",
    from: "    where: eq(accountKeys.email, email.toLowerCase()),",
    to: "    where: eq(accountKeys.email, email),",
  },
  {
    name: "getAccountKeysByEmail lists the oldest first",
    from: "    orderBy: [desc(accountKeys.createdAt)],",
    to: "    orderBy: [accountKeys.createdAt],",
  },
  {
    name: "getGrantedEmails includes revoked keys",
    from: '    .where(eq(accountKeys.status, "granted"));',
    to: "    ;",
  },
  {
    name: "getGrantedEmails does not normalise",
    from: "    .map((r) => r.email.toLowerCase().trim())",
    to: "    .map((r) => r.email)",
  },
  {
    name: "getGrantedEmails keeps blank emails",
    from: "    .filter((email) => email.length > 0);",
    to: "    .filter((email) => email.length >= 0);",
  },
  {
    name: "a repeat validation keeps the old device name",
    from: "      set: {\n        deviceName: deviceName || null,\n        lastValidatedAt: new Date(),\n      },",
    to: "      set: {\n        lastValidatedAt: new Date(),\n      },",
  },
  {
    name: "a repeat validation keeps the old time",
    from: "      set: {\n        deviceName: deviceName || null,\n        lastValidatedAt: new Date(),\n      },",
    to: "      set: {\n        deviceName: deviceName || null,\n      },",
  },
  {
    name: "dedup a device on its id alone",
    from: "      target: [deviceValidations.licenseKeyId, deviceValidations.deviceId],",
    to: "      target: [deviceValidations.deviceId],",
  },
  {
    name: "getDevicesForLicense ignores the window",
    from: "  const conditions = [eq(deviceValidations.licenseKeyId, licenseKeyId)];\n  if (sinceDays) {",
    to: "  const conditions = [eq(deviceValidations.licenseKeyId, licenseKeyId)];\n  if (false) {",
  },
  {
    name: "getDevicesForLicense lists every key's devices",
    from: "  const conditions = [eq(deviceValidations.licenseKeyId, licenseKeyId)];",
    to: "  const conditions = [sql`true`];",
  },
  {
    name: "getDevicesForLicense lists the oldest first",
    from: "    .orderBy(desc(deviceValidations.lastValidatedAt));\n\n  return rows;",
    to: "    .orderBy(deviceValidations.lastValidatedAt);\n\n  return rows;",
  },
  {
    name: "getDevicesForLicense window is one day short",
    from: "    since.setDate(since.getDate() - sinceDays);\n    conditions.push(gte(deviceValidations.lastValidatedAt, since));\n  }\n\n  const rows = await db\n    .select({\n      deviceId",
    to: "    since.setDate(since.getDate() - sinceDays + 2);\n    conditions.push(gte(deviceValidations.lastValidatedAt, since));\n  }\n\n  const rows = await db\n    .select({\n      deviceId",
  },
  {
    name: "getDeviceCountsPerLicense ignores the window",
    from: "  const conditions = [];\n  if (sinceDays) {",
    to: "  const conditions = [];\n  if (false) {",
  },
  {
    name: "getDeviceCountsPerLicense lists the smallest first",
    from: "    .orderBy(sql`count(${deviceValidations.id}) desc`);",
    to: "    .orderBy(sql`count(${deviceValidations.id}) asc`);",
  },
  {
    name: "getDeviceCountsPerLicense returns the raw count",
    from: "  return rows.map((r) => ({ ...r, deviceCount: Number(r.deviceCount) }));",
    to: "  return rows.map((r) => ({ ...r, deviceCount: String(r.deviceCount) as unknown as number }));",
  },
  {
    name: "getDeviceCountsPerLicense keeps keys with no devices",
    from: "    .innerJoin(accountKeys, eq(deviceValidations.licenseKeyId, accountKeys.id))",
    to: "    .rightJoin(accountKeys, eq(deviceValidations.licenseKeyId, accountKeys.id))",
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
