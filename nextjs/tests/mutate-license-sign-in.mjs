/**
 * Mutation proof for tests/license-sign-in.test.ts and
 * tests/license-key-generation.test.ts.
 *
 * Each entry below does ONE exact string replace in its source file, runs the
 * one test file that covers it, then puts the source back. A mutant that still
 * PASSES means the tests are hollow there.
 *
 * This file is a tool, not a test. `npm test` globs `tests/*.test.ts`, so it
 * is never picked up by the suite.
 *
 *   node tests/mutate-license-sign-in.mjs
 */
import { execFileSync } from "node:child_process";
import { readFileSync, writeFileSync } from "node:fs";

const PLUGIN = "src/lib/auth-license-key-plugin.ts";
const SERVICE = "lib/services/license-key.ts";
const PLUGIN_TEST = "tests/license-sign-in.test.ts";
const SERVICE_TEST = "tests/license-key-generation.test.ts";

const MUTANTS = [
  // --- the entitlement decision -------------------------------------------
  {
    source: PLUGIN,
    test: PLUGIN_TEST,
    name: "sign in on any key the database holds, whatever its status",
    from: 'if (!license || license.status !== "granted") {\n          return ctx.json(\n            { error: "Invalid or inactive license key." },\n            { status: 400 },\n          );\n        }\n\n        if (!license.userId) {',
    to: 'if (!license) {\n          return ctx.json(\n            { error: "Invalid or inactive license key." },\n            { status: 400 },\n          );\n        }\n\n        if (!license.userId) {',
  },
  {
    source: PLUGIN,
    test: PLUGIN_TEST,
    name: "answer 200 instead of 400 for an invalid key",
    from: '            { error: "Invalid or inactive license key." },\n            { status: 400 },\n          );\n        }\n\n        if (!license.userId) {',
    to: '            { error: "Invalid or inactive license key." },\n            { status: 200 },\n          );\n        }\n\n        if (!license.userId) {',
  },
  {
    source: PLUGIN,
    test: PLUGIN_TEST,
    name: "skip the missing-user check on the key row",
    from: "if (!license.userId) {",
    to: "if (false) {",
  },
  {
    source: PLUGIN,
    test: PLUGIN_TEST,
    name: "accept a key whose user row is gone",
    from: "if (!foundUser) {",
    to: "if (false) {",
  },
  {
    source: PLUGIN,
    test: PLUGIN_TEST,
    name: "look the user up by the key id instead of the user id",
    from: ".where(eq(user.id, license.userId))",
    to: ".where(eq(user.id, license.id))",
  },
  {
    source: PLUGIN,
    test: PLUGIN_TEST,
    name: "mint the session for the key row instead of the user",
    from: "await ctx.context.internalAdapter.createSession(\n          foundUser.id,\n        );",
    to: "await ctx.context.internalAdapter.createSession(\n          license.id,\n        );",
  },
  {
    source: PLUGIN,
    test: PLUGIN_TEST,
    name: "ignore a session the adapter failed to create",
    from: "if (!session) {",
    to: "if (false) {",
  },
  {
    source: PLUGIN,
    test: PLUGIN_TEST,
    name: "answer 200 instead of 500 when the session cannot be created",
    from: '            { error: "Failed to create session." },\n            { status: 500 },',
    to: '            { error: "Failed to create session." },\n            { status: 200 },',
  },

  // --- the revocation race -------------------------------------------------
  {
    source: PLUGIN,
    test: PLUGIN_TEST,
    name: "drop the re-read and trust the first lookup",
    from: "const current = await findAccountByKey(licenseKey);",
    to: "const current = license;",
  },
  {
    source: PLUGIN,
    test: PLUGIN_TEST,
    name: "skip the re-read verdict entirely",
    from: 'if (!current || current.status !== "granted") {',
    to: "if (false) {",
  },
  {
    source: PLUGIN,
    test: PLUGIN_TEST,
    name: "leave the minted session alive when the re-read says revoked",
    from: "await ctx.context.internalAdapter.deleteSession(session.token);",
    to: "void session.token;",
  },
  {
    source: PLUGIN,
    test: PLUGIN_TEST,
    name: "re-read BEFORE the session exists, reopening the race",
    from: "const session = await ctx.context.internalAdapter.createSession(\n          foundUser.id,\n        );",
    to: "await findAccountByKey(licenseKey);\n        const session = await ctx.context.internalAdapter.createSession(\n          foundUser.id,\n        );",
  },
  {
    source: PLUGIN,
    test: PLUGIN_TEST,
    name: "set the cookie before the re-read decides",
    from: "await setSessionCookie(ctx, { session, user: foundUser });",
    to: "",
    extra: {
      from: "const current = await findAccountByKey(licenseKey);",
      to: "await setSessionCookie(ctx, { session, user: foundUser });\n        const current = await findAccountByKey(licenseKey);",
    },
  },

  // --- the redirect target -------------------------------------------------
  {
    source: PLUGIN,
    test: PLUGIN_TEST,
    name: "return the caller's callbackURL without sanitising it",
    from: "ctx.json({ redirect: sanitizeLicenseKeyRedirect(callbackURL) })",
    to: 'ctx.json({ redirect: callbackURL ?? "/en/user/dashboard" })',
  },
  {
    source: PLUGIN,
    test: PLUGIN_TEST,
    name: "always redirect to the default, ignoring callbackURL",
    from: "ctx.json({ redirect: sanitizeLicenseKeyRedirect(callbackURL) })",
    to: "ctx.json({ redirect: sanitizeLicenseKeyRedirect(undefined) })",
  },

  // --- the request contract and the audit line -----------------------------
  {
    source: PLUGIN,
    test: PLUGIN_TEST,
    name: "let the endpoint run with no headers",
    from: "requireHeaders: true,",
    to: "requireHeaders: false,",
  },
  {
    source: PLUGIN,
    test: PLUGIN_TEST,
    name: "make licenseKey optional in the body schema",
    from: "licenseKey: z.string(),",
    to: "licenseKey: z.string().optional(),",
  },
  {
    source: PLUGIN,
    test: PLUGIN_TEST,
    name: "log the whole forwarded-for chain rather than the client ip",
    from: 'ctx.headers?.get("x-forwarded-for")?.split(",")[0]?.trim() ??',
    to: 'ctx.headers?.get("x-forwarded-for") ??',
  },
  {
    source: PLUGIN,
    test: PLUGIN_TEST,
    name: "log the last proxy hop instead of the client ip",
    from: '?.split(",")[0]?.trim() ??',
    to: '?.split(",").at(-1)?.trim() ??',
  },
  {
    source: PLUGIN,
    test: PLUGIN_TEST,
    name: "drop the license id from the audit line",
    from: "license=${license.id}",
    to: "license=redacted",
  },
  {
    source: PLUGIN,
    test: PLUGIN_TEST,
    name: "throttle every sign-in path, not just the license one",
    from: 'return path === "/sign-in/license-key";',
    to: 'return path.includes("/sign-in/license-key");',
  },
  {
    source: PLUGIN,
    test: PLUGIN_TEST,
    name: "raise the sign-in attempt budget from 5 to 50",
    from: "max: 5,",
    to: "max: 50,",
  },

  // --- the key generator ---------------------------------------------------
  {
    source: SERVICE,
    test: SERVICE_TEST,
    name: "put the ambiguous characters back in the alphabet",
    from: 'const ALPHABET = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";',
    to: 'const ALPHABET = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";',
  },
  {
    source: SERVICE,
    test: SERVICE_TEST,
    name: "draw with a modulo instead of a bounded randomInt",
    from: "const charIndex = crypto.randomInt(ALPHABET.length);",
    to: "const charIndex = crypto.randomInt(256) % ALPHABET.length;",
  },
  {
    source: SERVICE,
    test: SERVICE_TEST,
    name: "emit 3 segments instead of 4",
    from: "for (let segment = 0; segment < 4; segment++) {",
    to: "for (let segment = 0; segment < 3; segment++) {",
  },
  {
    source: SERVICE,
    test: SERVICE_TEST,
    name: "emit 3 characters per segment",
    from: "for (let char = 0; char < 4; char++) {",
    to: "for (let char = 0; char < 3; char++) {",
  },
  {
    source: SERVICE,
    test: SERVICE_TEST,
    name: "drop the HW prefix",
    from: 'let key = "HW";',
    to: 'let key = "";',
  },
  {
    source: SERVICE,
    test: SERVICE_TEST,
    name: "skip the lower bounds check on a drawn index",
    from: "if (charIndex < 0 || charIndex >= ALPHABET.length) {",
    to: "if (charIndex >= ALPHABET.length) {",
  },
  {
    source: SERVICE,
    test: SERVICE_TEST,
    name: "skip the upper bounds check on a drawn index",
    from: "if (charIndex < 0 || charIndex >= ALPHABET.length) {",
    to: "if (charIndex < 0) {",
  },
  {
    source: SERVICE,
    test: SERVICE_TEST,
    name: "accept an undefined alphabet character",
    from: 'if (!alphabetChar || typeof alphabetChar !== "string" || alphabetChar.length !== 1) {',
    to: "if (false) {",
  },
  {
    source: SERVICE,
    test: SERVICE_TEST,
    name: "give up after the first failed draw",
    from: "const MAX_RETRIES = 3;",
    to: "const MAX_RETRIES = 1;",
  },
  {
    source: SERVICE,
    test: SERVICE_TEST,
    name: "retry 5 times instead of 3",
    from: "const MAX_RETRIES = 3;",
    to: "const MAX_RETRIES = 5;",
  },
  {
    source: SERVICE,
    test: SERVICE_TEST,
    name: "swallow the cause of the final failure",
    from: "`Failed to generate valid license key after ${MAX_RETRIES} attempts: ${error instanceof Error ? error.message : String(error)}`",
    to: "`Failed to generate valid license key after ${MAX_RETRIES} attempts`",
  },
  {
    source: SERVICE,
    test: SERVICE_TEST,
    name: "accept any string as a well-formed key",
    from: "return pattern.test(key.toUpperCase());",
    to: "return true;",
  },
  {
    source: SERVICE,
    test: SERVICE_TEST,
    name: "stop accepting a lower-case key",
    from: "return pattern.test(key.toUpperCase());",
    to: "return pattern.test(key);",
  },
  {
    source: SERVICE,
    test: SERVICE_TEST,
    name: "anchor the format check loosely, so a padded key passes",
    from: "const pattern = /^HW-[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}$/;",
    to: "const pattern = /HW-[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}/;",
  },
  {
    source: SERVICE,
    test: SERVICE_TEST,
    name: "let a non-string value through the format check",
    from: 'if (!key || typeof key !== "string") {',
    to: "if (!key) {",
  },
];

const originals = new Map();
for (const path of new Set(MUTANTS.map((m) => m.source))) {
  originals.set(path, readFileSync(path, "utf8"));
}

const results = [];

for (const mutant of MUTANTS) {
  const original = originals.get(mutant.source);
  const edits = [{ from: mutant.from, to: mutant.to }];
  if (mutant.extra) edits.push(mutant.extra);

  let mutated = original;
  let applied = true;
  for (const edit of edits) {
    const occurrences = mutated.split(edit.from).length - 1;
    if (occurrences !== 1) {
      results.push({ ...mutant, verdict: `SKIPPED (${occurrences} matches)` });
      applied = false;
      break;
    }
    mutated = mutated.replace(edit.from, edit.to);
  }
  if (!applied) continue;

  writeFileSync(mutant.source, mutated);

  let killed = false;
  try {
    execFileSync(
      "node",
      [
        "--import",
        "tsx",
        "--experimental-test-module-mocks",
        "--test",
        mutant.test,
      ],
      { stdio: "pipe" },
    );
  } catch {
    killed = true;
  } finally {
    writeFileSync(mutant.source, original);
  }

  results.push({ ...mutant, verdict: killed ? "KILLED" : "SURVIVED" });
  console.log(`${killed ? "KILLED  " : "SURVIVED"}  ${mutant.name}`);
}

const survivors = results.filter((r) => r.verdict !== "KILLED");
console.log("");
console.log(`| # | Source | Mutation | Verdict |`);
console.log(`| --- | --- | --- | --- |`);
results.forEach((r, i) => {
  console.log(`| ${i + 1} | \`${r.source}\` | ${r.name} | ${r.verdict} |`);
});
console.log("");
console.log(`${results.length - survivors.length}/${results.length} killed`);
process.exit(survivors.length === 0 ? 0 : 1);
