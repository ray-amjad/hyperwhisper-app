/**
 * Mutation proof for tests/trpc-http.test.ts.
 *
 * Each entry below does ONE exact string replace in one source file behind
 * the tRPC HTTP route, runs the one test file, then puts the source back. A
 * mutant that still PASSES means the tests are hollow there.
 *
 * This file is a tool, not a test. `npm test` globs `tests/*.test.ts`, so it
 * is never picked up by the suite.
 *
 *   node tests/mutate-trpc-http.mjs
 */
import { execFileSync } from "node:child_process";
import { readFileSync, writeFileSync } from "node:fs";

const TEST = "tests/trpc-http.test.ts";

const ROUTE = "app/api/trpc/[trpc]/route.ts";
const ROOT = "server/api/root.ts";
const ADMIN = "server/api/routers/admin/index.ts";
const DEVICES = "server/api/routers/admin/devices.ts";
const STATS = "server/api/routers/admin/stats.ts";
const TRPC = "server/api/trpc.ts";

const MUTANTS = [
  {
    file: ROUTE,
    name: "build the context from empty headers",
    from: "    headers: req.headers,",
    to: "    headers: new Headers(),",
  },
  {
    file: ROUTE,
    name: "serve the router under a different endpoint",
    from: '    endpoint: "/api/trpc",',
    to: '    endpoint: "/api/rpc",',
  },
  {
    file: ROUTE,
    name: "treat every error as a server error",
    from: "const isServerError = httpStatus >= 500;",
    to: "const isServerError = httpStatus >= 400;",
  },
  {
    file: ROUTE,
    name: "never log a server error",
    from: "const isServerError = httpStatus >= 500;",
    to: "const isServerError = false;",
  },
  {
    file: ROUTE,
    name: "log a dev 4xx as an error",
    from: "          console.debug(message);",
    to: "          console.error(message);",
  },
  {
    file: ROUTE,
    name: "print the stack in production too",
    from: "if (isDev && error.stack) {",
    to: "if (error.stack) {",
  },
  {
    file: ROUTE,
    name: "never print the stack",
    from: "if (isDev && error.stack) {",
    to: "if (false) {",
  },
  {
    file: ROUTE,
    name: "drop the path from the log line",
    from: "${path ?? \"<no-path>\"}",
    to: "<no-path>",
  },
  {
    file: ROOT,
    name: "unmount the admin namespace",
    from: "  admin: adminRouter,\n",
    to: "",
  },
  {
    file: ADMIN,
    name: "unmount admin devices",
    from: "  devices: devicesRouter,\n",
    to: "",
  },
  {
    file: DEVICES,
    name: "make devices.list public",
    from: "  list: adminProcedure",
    to: "  list: publicProcedure",
    prelude: {
      from: "import { createTRPCRouter, adminProcedure } from",
      to: "import { createTRPCRouter, adminProcedure, publicProcedure } from",
    },
  },
  {
    file: DEVICES,
    name: "make devices.forLicense signed-in only",
    from: "  forLicense: adminProcedure",
    to: "  forLicense: protectedProcedure",
    prelude: {
      from: "import { createTRPCRouter, adminProcedure } from",
      to: "import { createTRPCRouter, adminProcedure, protectedProcedure } from",
    },
  },
  {
    file: DEVICES,
    name: "default window of 7 days",
    from: "const days = input?.days ?? 30;",
    to: "const days = input?.days ?? 7;",
  },
  {
    file: DEVICES,
    name: "ignore the caller's window",
    from: "const rows = await getDeviceCountsPerLicense(days);",
    to: "const rows = await getDeviceCountsPerLicense(30);",
  },
  {
    file: DEVICES,
    name: "allow a zero or negative window",
    from: ".input(z.object({ days: z.number().positive().optional() }).optional())",
    to: ".input(z.object({ days: z.number().optional() }).optional())",
  },
  {
    file: DEVICES,
    name: "accept any string as a license id",
    from: "licenseKeyId: z.string().uuid(),",
    to: "licenseKeyId: z.string(),",
  },
  {
    file: DEVICES,
    name: "drop the forLicense window",
    from: "          input.days\n",
    to: "          undefined\n",
  },
  {
    file: DEVICES,
    name: "answer 400 instead of 500 on a db failure (list)",
    from: '      } catch (error) {\n        console.error("Device counts fetch error:", error);\n        throw new TRPCError({\n          code: "INTERNAL_SERVER_ERROR",',
    to: '      } catch (error) {\n        console.error("Device counts fetch error:", error);\n        throw new TRPCError({\n          code: "BAD_REQUEST",',
  },
  {
    file: DEVICES,
    name: "hide the Error message (list)",
    from: '              ? error.message\n              : "Failed to fetch device counts",',
    to: '              ? "Failed to fetch device counts"\n              : "Failed to fetch device counts",',
  },
  {
    file: DEVICES,
    name: "hide the Error message (forLicense)",
    from: '              ? error.message\n              : "Failed to fetch devices for license",',
    to: '              ? "Failed to fetch devices for license"\n              : "Failed to fetch devices for license",',
  },
  {
    file: STATS,
    name: "make stats.get signed-in only",
    from: "  get: adminProcedure.query(",
    to: "  get: protectedProcedure.query(",
    prelude: {
      from: "import { createTRPCRouter, adminProcedure } from",
      to: "import { createTRPCRouter, adminProcedure, protectedProcedure } from",
    },
  },
  {
    file: STATS,
    name: "page of 10, not 100",
    from: "stripe.customers.list({ limit: 100 })",
    to: "stripe.customers.list({ limit: 10 })",
  },
  {
    file: STATS,
    name: "report zero Stripe customers",
    from: "stripeCustomers = allCustomers.data.length;",
    to: "stripeCustomers = 0;",
  },
  {
    file: STATS,
    name: "let a Stripe failure escape",
    from: "      } catch {\n        // Stripe not configured\n      }",
    to: "      } finally {\n        // Stripe not configured\n      }",
  },
  {
    file: TRPC,
    name: "admin check matches role case-insensitively",
    from: 'const isAdmin = user?.role === "admin";',
    to: 'const isAdmin = user?.role?.toLowerCase() === "admin";',
  },
  {
    file: TRPC,
    name: "admin gate forgets the admin check",
    from: "  if (!ctx.isAdmin) {",
    to: "  if (false) {",
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
