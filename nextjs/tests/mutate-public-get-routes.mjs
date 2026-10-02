/**
 * Mutation proof for tests/public-get-routes.test.ts.
 *
 * Each entry below does ONE exact string replace in a route, runs the test
 * file, then puts the source back. A mutant that still PASSES means the
 * tests are hollow there.
 *
 * Two mutants are left out because they are equivalent, not because a test
 * misses them:
 *   - `raw.trim() === ""` → `raw === ""`: the Fetch `Headers` class strips
 *     leading and trailing whitespace from every value, so a route never sees
 *     "   ". The trim is a second guard with nothing left to catch.
 *   - dropping `candidates.length === 0` from the early exit: `nearestRegion`
 *     answers null for an empty list, so the response is the same.
 *
 * This file is a tool, not a test. `npm test` globs `tests/*.test.ts`, so it
 * is never picked up by the suite.
 *
 *   node tests/mutate-public-get-routes.mjs
 */
import { execFileSync } from "node:child_process";
import { readFileSync, writeFileSync } from "node:fs";

const TESTS = ["tests/public-get-routes.test.ts"];

const NEAREST = "app/api/geo/nearest-region/route.ts";
const MODELS = "app/api/internal/models/route.ts";

const MUTANTS = [
  {
    file: NEAREST,
    name: "read a blank header as 0 (Null Island)",
    from: "if (raw === null || raw.trim() === \"\") {",
    to: "if (raw === null) {",
  },
  {
    file: NEAREST,
    name: "accept a non-finite coordinate",
    from: "return Number.isFinite(value) ? value : null;",
    to: "return Number.isNaN(value) ? null : value;",
  },
  {
    file: NEAREST,
    name: "read the wrong latitude header",
    from: "coordinateHeader(request, \"x-vercel-ip-latitude\")",
    to: "coordinateHeader(request, \"x-vercel-ip-longitude\")",
  },
  {
    file: NEAREST,
    name: "skip the lower-casing",
    from: ".map((code) => code.trim().toLowerCase())",
    to: ".map((code) => code.trim())",
  },
  {
    file: NEAREST,
    name: "skip the trim",
    from: ".map((code) => code.trim().toLowerCase())",
    to: ".map((code) => code.toLowerCase())",
  },
  {
    file: NEAREST,
    name: "match a code prefix instead of the whole code",
    from: "/^[a-z]{3,12}$/",
    to: "/^[a-z]{3,12}/",
  },
  {
    file: NEAREST,
    name: "drop the candidate cap",
    from: ".slice(0, MAX_CANDIDATES)",
    to: ".slice(0)",
  },
  {
    file: NEAREST,
    name: "cap before filtering",
    from: "    .filter((code) => /^[a-z]{3,12}$/.test(code))\n    .slice(0, MAX_CANDIDATES);",
    to: "    .slice(0, MAX_CANDIDATES)\n    .filter((code) => /^[a-z]{3,12}$/.test(code));",
  },
  {
    file: NEAREST,
    name: "cap off by one",
    from: "const MAX_CANDIDATES = 60;",
    to: "const MAX_CANDIDATES = 59;",
  },
  {
    file: NEAREST,
    name: "answer with only one coordinate",
    from: "if (latitude === null || longitude === null || candidates.length === 0) {",
    to: "if (latitude === null || candidates.length === 0) {",
  },
  {
    file: NEAREST,
    name: "drop the city from the answer",
    from: "city: match?.city ?? null,",
    to: "city: null,",
  },
  {
    file: NEAREST,
    name: "let a CDN share the answer",
    from: "{ headers: { \"Cache-Control\": \"private, max-age=300\" } },\n  );\n}",
    to: "{ headers: { \"Cache-Control\": \"public, max-age=300\" } },\n  );\n}",
  },
  {
    file: NEAREST,
    name: "let a CDN share the null answer",
    from: "{ region: null, city: null },\n      { headers: { \"Cache-Control\": \"private, max-age=300\" } },",
    to: "{ region: null, city: null },\n      { headers: { \"Cache-Control\": \"public, max-age=300\" } },",
  },
  {
    file: MODELS,
    name: "bypass the in-memory cache",
    from: "import { fetchAvailableModels } from \"@/lib/services/model-list\";",
    to: "import { createModelList } from \"@/lib/services/model-list\";\nconst fetchAvailableModels = () => createModelList().fetchAvailableModelsUncached();",
  },
  {
    file: MODELS,
    name: "turn off the edge cache",
    from: "\"public, s-maxage=3600, stale-while-revalidate=86400\"",
    to: "\"no-store\"",
  },
  {
    file: MODELS,
    name: "edge-cache for 60 s, not 1 h",
    from: "s-maxage=3600",
    to: "s-maxage=60",
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
