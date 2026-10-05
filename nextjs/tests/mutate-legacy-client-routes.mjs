/**
 * Mutation proof for tests/legacy-client-routes.test.ts and the
 * /api/account/credits alias tests in tests/credit-routes.test.ts.
 *
 * Each entry below does ONE exact string replace in a route, runs the test
 * files, then puts the source back. A mutant that still PASSES means the
 * tests are hollow there.
 *
 * This file is a tool, not a test. `npm test` globs `tests/*.test.ts`, so it
 * is never picked up by the suite.
 *
 *   node tests/mutate-legacy-client-routes.mjs
 */
import { execFileSync } from "node:child_process";
import { readFileSync, writeFileSync } from "node:fs";

const TESTS = [
  "tests/legacy-client-routes.test.ts",
  "tests/credit-routes.test.ts",
];

const CONFIG = "app/api/config/route.ts";
const CHECKOUT = "app/api/checkout/route.ts";
const ACCOUNT_CREDITS = "app/api/account/credits/route.ts";

const MUTANTS = [
  {
    file: CONFIG,
    name: "overflow int32 on the daily cap",
    from: "trial_daily_limit_seconds: 2_000_000_000,",
    to: "trial_daily_limit_seconds: 3_000_000_000,",
  },
  {
    file: CONFIG,
    name: "restore the old 300 s daily cap",
    from: "trial_daily_limit_seconds: 2_000_000_000,",
    to: "trial_daily_limit_seconds: 300,",
  },
  {
    file: CONFIG,
    name: "block every model download",
    from: "trial_model_download_limit: 2_000_000_000,",
    to: "trial_model_download_limit: 0,",
  },
  {
    file: CONFIG,
    name: "send the cap as a string",
    from: "trial_model_download_limit: 2_000_000_000,",
    to: "trial_model_download_limit: \"2000000000\",",
  },
  {
    file: CONFIG,
    name: "drop the model download cap",
    from: "      trial_model_download_limit: 2_000_000_000,\n",
    to: "",
  },
  {
    file: CONFIG,
    name: "cache for 60 s, not 6 h",
    from: "\"public, max-age=21600\"",
    to: "\"public, max-age=60\"",
  },
  {
    file: CHECKOUT,
    name: "redirect to the retired /checkout page",
    from: "new URL(\"/credits\", request.url)",
    to: "new URL(\"/checkout\", request.url)",
  },
  {
    file: CHECKOUT,
    name: "redirect to the production host",
    from: "new URL(\"/credits\", request.url)",
    to: "new URL(\"/credits\", \"https://hyperwhisper.com\")",
  },
  {
    file: CHECKOUT,
    name: "drop the forwarded parameters",
    from: "  url.search = new URL(request.url).search;\n",
    to: "",
  },
  {
    file: CHECKOUT,
    name: "forward only the key",
    from: "url.search = new URL(request.url).search;",
    to: "url.search = new URL(request.url).searchParams.has(\"license_key\") ? `?license_key=${new URL(request.url).searchParams.get(\"license_key\")}` : \"\";",
  },
  {
    file: CHECKOUT,
    name: "re-encode the query through URLSearchParams",
    from: "url.search = new URL(request.url).search;",
    to: "url.search = new URLSearchParams(new URL(request.url).search).toString();",
  },
  {
    file: CHECKOUT,
    name: "answer a permanent 308",
    from: "return NextResponse.redirect(url);",
    to: "return NextResponse.redirect(url, 308);",
  },
  {
    file: ACCOUNT_CREDITS,
    name: "re-export POST only",
    from: "export { GET, POST } from \"../../license/credits/route\";",
    to: "export { POST } from \"../../license/credits/route\";\nexport const GET = async () => new Response(null);",
  },
  {
    file: ACCOUNT_CREDITS,
    name: "wrap POST, skipping the handler",
    from: "export { GET, POST } from \"../../license/credits/route\";",
    to: "export { GET } from \"../../license/credits/route\";\nexport const POST = async () => Response.json({ success: true, credits: 0 });",
  },
];

const results = [];
for (const mutant of MUTANTS) {
  const original = readFileSync(mutant.file, "utf8");
  const count = original.split(mutant.from).length - 1;
  if (count !== 1) {
    throw new Error(`${mutant.name}: expected 1 match, found ${count}`);
  }
  writeFileSync(mutant.file, original.replace(mutant.from, mutant.to));
  let killed = false;
  try {
    execFileSync(
      "node",
      ["--import", "tsx", "--experimental-test-module-mocks", "--test", ...TESTS],
      { stdio: "pipe" },
    );
  } catch {
    killed = true;
  } finally {
    writeFileSync(mutant.file, original);
  }
  results.push({ name: mutant.name, file: mutant.file, killed });
  console.log(`${killed ? "KILLED  " : "SURVIVED"}  ${mutant.file}  ${mutant.name}`);
}

const survivors = results.filter((r) => !r.killed);
console.log(`\n${results.length - survivors.length}/${results.length} killed`);
process.exit(survivors.length === 0 ? 0 : 1);
