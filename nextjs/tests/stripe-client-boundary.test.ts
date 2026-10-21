/**
 * `lib/clients/stripe.ts` is the app's Stripe boundary: it owns the secret
 * key and every pinned API version. Before #755 the admin customers router
 * built a second client inline, so the version live refunds ran on was set in
 * a tRPC router, and only a comment asked new code not to do the same.
 *
 * This test enforces the boundary (Ray, inbox ask #94): no file under
 * `nextjs/` other than `lib/clients/stripe.ts` may construct a Stripe client.
 * It reads every file, whatever its extension, so a constructor cannot hide
 * in a `.mjs` script or a route handler either.
 *
 * The SDK builds a client without `new` too (it calls itself with `new` when
 * `this` is not an instance), and a default import can take any name. So
 * (#2287) only the three files that need the SDK's runtime value may import
 * the `stripe` package as a value; every other file uses `import type`. In
 * those three, the imported name is never constructed or called, except in
 * the boundary itself.
 *
 * Skipped: `node_modules` and the generated build and tool output that
 * `.gitignore` already keeps out of the repo, plus one file named below.
 */
import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import { join, relative } from "node:path";
import { fileURLToPath } from "node:url";
import { describe, test } from "node:test";

const ROOT = fileURLToPath(new URL("..", import.meta.url));
const BOUNDARY = "lib/clients/stripe.ts";

/** A Stripe client is built here; the pattern is spelled so this file does not match it. */
const CONSTRUCTS = /\bnew\s+Stripe\s*\(/;

/**
 * The files that may import the `stripe` package as a value: the boundary,
 * and the webhook route and service, which verify and read Stripe events.
 */
const VALUE_IMPORTERS: readonly string[] = [
  BOUNDARY,
  "lib/services/stripe-webhook.ts",
  "app/api/webhooks/stripe/route.ts",
];

/** `stripe`, or a path inside the package, in quotes. No capture group, so the patterns below can share it. */
const PACKAGE = String.raw`['"\`]stripe(?:/[^'"\`]*)?['"\`]`;

/**
 * Every way a file can load the package at runtime: a static `import` or
 * `export … from` that is not `import type` / `export type`, a bare side-effect
 * import, `require(…)` and a dynamic `import(…)`. Static forms are anchored to
 * the start of a line so a comment that quotes one does not count.
 */
const VALUE_IMPORT = new RegExp(
  [
    String.raw`^\s*(?:import|export)\s+(?!type\b)[^;'"\`]*?\bfrom\s*${PACKAGE}`,
    String.raw`^\s*import\s*${PACKAGE}`,
    String.raw`\brequire\s*\(\s*${PACKAGE}\s*\)`,
    String.raw`\bimport\s*\(\s*${PACKAGE}`,
  ].join("|"),
  "m",
);

/** The binding list of a static value import of the package: `X`, `* as X`, `{ a as X }`, or a mix. */
const IMPORT_CLAUSE = new RegExp(
  String.raw`^\s*import\s+(?!type\b)([^;'"\`]*?)\s*\bfrom\s*${PACKAGE}`,
  "gm",
);

/** Every local name a file binds to the package's runtime value (`import type` binds none). */
function boundNames(text: string): string[] {
  return Array.from(text.matchAll(IMPORT_CLAUSE)).flatMap(([, clause]) => {
    const braces = /\{([^}]*)\}/.exec(clause);
    const named = (braces?.[1] ?? "")
      .split(",")
      .map((spec) => spec.trim())
      .filter((spec) => spec && !/^type\s/.test(spec))
      .map((spec) =>
        spec
          .split(/\s+as\s+/)
          .pop()!
          .trim(),
      );
    const outside = clause.replace(/\{[^}]*\}/, "").replace(/\*\s*as\s+/, "");
    const plain = outside
      .split(",")
      .map((part: string) => part.trim())
      .filter(Boolean);
    return [...plain, ...named];
  });
}

/** `name(` or `new name(`, where `name` is not a property (`x.name(`) or part of a longer word. */
function callsOrConstructs(name: string): RegExp {
  return new RegExp(String.raw`(?<![\w$.])${name.replace(/\$/g, "\\$")}\s*\(`);
}

/** Generated or installed, never authored: the walk does not enter them. */
const SKIP_DIRS = new Set(["node_modules", ".next", ".vercel", "out", "build", "coverage"]);

/**
 * `tests/mutate-stripe-client.mjs` is the mutation tool for the boundary
 * itself. Its mutants are exact-string replacements of the constructor line
 * in `lib/clients/stripe.ts`, so that line's text appears in its string
 * literals. It builds no client.
 */
const SKIP_FILES = new Set(["tests/mutate-stripe-client.mjs"]);

/** The test's own samples name the package as `PKG`, so this file does not match them. */
const sample = (code: string) => code.split("PKG").join("stripe");

function walk(dir: string): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    if (entry.isDirectory()) {
      return SKIP_DIRS.has(entry.name) ? [] : walk(join(dir, entry.name));
    }
    return entry.isFile() ? [join(dir, entry.name)] : [];
  });
}

describe("the Stripe client boundary", () => {
  test(`only ${BOUNDARY} constructs a Stripe client`, () => {
    const files = walk(ROOT).map((path) => relative(ROOT, path).split("\\").join("/"));

    // Not vacuous: the walk reached the routers, the API routes and the
    // boundary, and the pattern matches the constructors that are there.
    for (const expected of [
      BOUNDARY,
      "server/api/routers/admin/customers.ts",
      "app/api/webhooks/stripe/route.ts",
    ]) {
      assert.ok(files.includes(expected), `the scan must cover ${expected}`);
    }
    assert.match(readFileSync(join(ROOT, BOUNDARY), "utf8"), CONSTRUCTS);

    const offenders = files.filter(
      (file) =>
        file !== BOUNDARY &&
        !SKIP_FILES.has(file) &&
        CONSTRUCTS.test(readFileSync(join(ROOT, file), "utf8")),
    );
    assert.deepEqual(
      offenders,
      [],
      `build Stripe clients in ${BOUNDARY} and import them from "@/lib/clients/stripe"`,
    );
  });

  test("the import patterns catch every runtime load of the package and no type import", () => {
    for (const code of [
      "import S from 'PKG';\nS('x');",
      'import StripeSDK from "PKG";',
      "import { Stripe as S } from 'PKG';",
      "import {\n  Stripe,\n} from 'PKG';",
      "import * as S from 'PKG';",
      "import S, { type Stripe as T } from 'PKG';",
      "import 'PKG';",
      "import S from 'PKG/cjs/stripe.cjs.node.js';",
      "export { default } from 'PKG';",
      "export * from 'PKG';",
      "const S = require('PKG');",
      "require('PKG')('x');",
      "import S = require('PKG');",
      "const { default: S } = await import('PKG');",
    ].map(sample)) {
      assert.match(code, VALUE_IMPORT, code);
    }
    for (const code of [
      "import type Stripe from 'PKG';",
      "import type { Checkout } from 'PKG';",
      "export type { Charge } from 'PKG';",
      "// its `import Stripe from 'PKG'` requires the package's CJS entry.",
      "createRequire(import.meta.url).resolve('PKG');",
      "import S from 'PKG-js';",
    ].map(sample)) {
      assert.doesNotMatch(code, VALUE_IMPORT, code);
    }

    assert.deepEqual(boundNames(sample("import S, * as NS from 'PKG';")), [
      "S",
      "NS",
    ]);
    assert.deepEqual(
      boundNames(sample("import { Stripe as S, type Charge, X } from 'PKG';")),
      ["S", "X"],
    );
    assert.deepEqual(boundNames(sample("import type Stripe from 'PKG';")), []);
    assert.match("S('x')", callsOrConstructs("S"));
    assert.match("new S('x')", callsOrConstructs("S"));
    assert.doesNotMatch("S.Checkout.Session", callsOrConstructs("S"));
    assert.doesNotMatch(
      "stripe.webhooks.constructEvent(",
      callsOrConstructs("constructEvent"),
    );
  });

  test(`only ${VALUE_IMPORTERS.join(", ")} import the stripe package as a value`, () => {
    const files = walk(ROOT).map((path) => relative(ROOT, path).split("\\").join("/"));

    // Not vacuous: each allowed file holds a value import the pattern sees,
    // and a type-only import elsewhere is left alone.
    for (const file of VALUE_IMPORTERS) {
      assert.match(readFileSync(join(ROOT, file), "utf8"), VALUE_IMPORT, file);
    }
    const typeOnly = "server/api/routers/admin/customer-spend.ts";
    assert.ok(files.includes(typeOnly), `the scan must cover ${typeOnly}`);
    assert.doesNotMatch(
      readFileSync(join(ROOT, typeOnly), "utf8"),
      VALUE_IMPORT,
    );

    const offenders = files.filter(
      (file) =>
        !VALUE_IMPORTERS.includes(file) &&
        VALUE_IMPORT.test(readFileSync(join(ROOT, file), "utf8")),
    );
    assert.deepEqual(
      offenders,
      [],
      `import Stripe types with "import type", and the client from "@/lib/clients/stripe"`,
    );
  });

  test(`the webhook files never call or construct the stripe import; ${BOUNDARY} does`, () => {
    const builds = (file: string) => {
      const text = readFileSync(join(ROOT, file), "utf8");
      const names = boundNames(text);
      assert.ok(
        names.length > 0,
        `${file} must bind the stripe import to a name`,
      );
      return names.filter((name) => callsOrConstructs(name).test(text));
    };

    // Not vacuous: the check sees the boundary's own constructors.
    assert.ok(
      builds(BOUNDARY).length > 0,
      `${BOUNDARY} constructs its clients`,
    );

    for (const file of VALUE_IMPORTERS) {
      if (file === BOUNDARY) continue;
      assert.deepEqual(
        builds(file),
        [],
        `${file} builds a Stripe client; import it from "@/lib/clients/stripe"`,
      );
    }
  });
});
