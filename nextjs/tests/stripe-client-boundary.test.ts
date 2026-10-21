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
 * (#2287) only three files may load the `stripe` package at runtime; every
 * other file uses `import type`. Two of those three may use what they import
 * only as a type. Code files are read with the TypeScript parser (already a
 * dev dependency), so no spelling, comment or alias changes what counts.
 *
 * Skipped: `node_modules` and the generated build and tool output that
 * `.gitignore` already keeps out of the repo, plus one file named below.
 */
import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import { join, relative } from "node:path";
import { fileURLToPath } from "node:url";
import { describe, test } from "node:test";
import ts from "typescript";

const ROOT = fileURLToPath(new URL("..", import.meta.url));
const BOUNDARY = "lib/clients/stripe.ts";

/** A Stripe client is built here; the pattern is spelled so this file does not match it. */
const CONSTRUCTS = /\bnew\s+Stripe\s*\(/;

/**
 * The files that may load the `stripe` package at runtime: the boundary, and
 * the webhook route and service, which held a default import when #2287 set
 * this list. Those two use it only in type positions today, so either can move
 * to `import type` and leave this list.
 */
const VALUE_IMPORTERS: readonly string[] = [
  BOUNDARY,
  "lib/services/stripe-webhook.ts",
  "app/api/webhooks/stripe/route.ts",
];

/** Files the TypeScript parser reads: TypeScript and JavaScript of every module flavour. */
const CODE = /\.[cm]?[jt]sx?$/;

/** `stripe`, or a path inside the package. */
const isStripe = (specifier: string) => /^stripe(?:\/|$)/.test(specifier);

const isStripeLiteral = (node: ts.Node | undefined) =>
  node !== undefined && ts.isStringLiteralLike(node) && isStripe(node.text);

function parse(file: string, text: string): ts.SourceFile {
  return ts.createSourceFile(file, text, ts.ScriptTarget.Latest, true);
}

function visit(node: ts.Node, each: (node: ts.Node) => void): void {
  each(node);
  ts.forEachChild(node, (child) => visit(child, each));
}

/** True when an `import { … }` or `export { … }` list names types only. */
function onlyTypes(elements: ts.NodeArray<ts.ImportSpecifier | ts.ExportSpecifier>): boolean {
  return elements.length > 0 && elements.every((element) => element.isTypeOnly);
}

/**
 * Every node that loads the package at runtime: an `import` or `export … from`
 * that is not type-only, `import x = require(…)`, and any call that is handed
 * the package name (`require`, a `createRequire` result, a dynamic `import()`),
 * except `….resolve(…)`, which returns a path and loads nothing.
 */
function stripeLoads(source: ts.SourceFile): ts.Node[] {
  const loads: ts.Node[] = [];
  visit(source, (node) => {
    if (ts.isImportDeclaration(node) && isStripeLiteral(node.moduleSpecifier)) {
      const clause = node.importClause;
      const typeOnly =
        clause !== undefined &&
        (clause.isTypeOnly ||
          (clause.name === undefined &&
            clause.namedBindings !== undefined &&
            ts.isNamedImports(clause.namedBindings) &&
            onlyTypes(clause.namedBindings.elements)));
      if (!typeOnly) loads.push(node);
    } else if (ts.isExportDeclaration(node) && isStripeLiteral(node.moduleSpecifier)) {
      const clause = node.exportClause;
      const typeOnly =
        node.isTypeOnly || (clause !== undefined && ts.isNamedExports(clause) && onlyTypes(clause.elements));
      if (!typeOnly) loads.push(node);
    } else if (
      ts.isImportEqualsDeclaration(node) &&
      !node.isTypeOnly &&
      ts.isExternalModuleReference(node.moduleReference) &&
      isStripeLiteral(node.moduleReference.expression)
    ) {
      loads.push(node);
    } else if (ts.isCallExpression(node) && node.arguments.some(isStripeLiteral)) {
      const resolves =
        ts.isPropertyAccessExpression(node.expression) && node.expression.name.text === "resolve";
      if (!resolves) loads.push(node);
    }
  });
  return loads;
}

/** The local names a static import of the package binds to its runtime value. */
function boundNames(source: ts.SourceFile): string[] {
  return stripeLoads(source).flatMap((load) => {
    if (!ts.isImportDeclaration(load) || load.importClause === undefined) return [];
    const { name, namedBindings } = load.importClause;
    const names = name ? [name.text] : [];
    if (namedBindings && ts.isNamespaceImport(namedBindings)) names.push(namedBindings.name.text);
    if (namedBindings && ts.isNamedImports(namedBindings)) {
      names.push(...namedBindings.elements.filter((e) => !e.isTypeOnly).map((e) => e.name.text));
    }
    return names;
  });
}

/** Is this node inside a type: an annotation, `Stripe.Event`, `typeof Stripe` in a type, … */
function inType(node: ts.Node): boolean {
  for (let at: ts.Node | undefined = node.parent; at; at = at.parent) {
    if (ts.isTypeNode(at)) return true;
  }
  return false;
}

/**
 * Every use of a runtime name the file's `stripe` imports bind, outside a type
 * and outside the import itself: a call, `new`, `X.Stripe`, `X.call`, an alias.
 * A property named the same (`a.Stripe`, `{ Stripe: … }`) is not a use.
 */
function valueUses(source: ts.SourceFile): string[] {
  const names = new Set(boundNames(source));
  const uses: string[] = [];
  visit(source, (node) => {
    if (!ts.isIdentifier(node) || !names.has(node.text)) return;
    const parent = node.parent;
    if (ts.isImportClause(parent) || ts.isNamespaceImport(parent) || ts.isImportSpecifier(parent)) return;
    if (ts.isPropertyAccessExpression(parent) && parent.name === node) return;
    if (ts.isPropertyAssignment(parent) && parent.name === node) return;
    if (inType(node)) return;
    const { line } = source.getLineAndCharacterOfPosition(node.getStart());
    uses.push(`${source.fileName}:${line + 1} ${node.parent.getText()}`);
  });
  return uses;
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

  test("the parser finds every runtime load of the package and no type import", () => {
    const loads = (code: string) => stripeLoads(parse("sample.ts", code)).length;
    for (const code of [
      "import S from 'stripe';\nS('x');",
      'import StripeSDK from "stripe";',
      "import{default as S}from'stripe'",
      "import { Stripe as S } from 'stripe';",
      "import * as S from 'stripe';",
      "import S, { type Charge } from 'stripe';",
      "import { type Charge, Stripe } from 'stripe';",
      "import 'stripe';",
      "x(); import S from 'stripe';",
      "import S from 'stripe/cjs/stripe.cjs.node.js';",
      "export { default } from 'stripe';",
      "export * from 'stripe';",
      "const S = require('stripe');",
      "require('stripe')('x');",
      "createRequire(import.meta.url)('stripe');",
      "import S = require('stripe');",
      "const { default: S } = await import(/* webpackChunkName: 'x' */ 'stripe');",
      "await import(`stripe`);",
    ]) {
      assert.equal(loads(code), 1, code);
    }
    for (const code of [
      "import type Stripe from 'stripe';",
      "import  type Stripe from 'stripe';",
      "import\n  type Stripe from 'stripe';",
      "import type { Checkout } from 'stripe';",
      "import { type Checkout, type Charge } from 'stripe';",
      "export type { Charge } from 'stripe';",
      "export { type Charge } from 'stripe';",
      "type T = import('stripe').Stripe;",
      "// its `import Stripe from 'stripe'` requires the package's CJS entry.",
      "/* require('stripe') */",
      "createRequire(import.meta.url).resolve('stripe');",
      "import S from 'stripe-js';",
      "const name = 'stripe';",
    ]) {
      assert.equal(loads(code), 0, code);
    }
  });

  test("the parser finds every runtime use of the imported name and no type use", () => {
    const uses = (code: string) => valueUses(parse("sample.ts", code)).length;
    for (const code of [
      "import S from 'stripe';\nS('x');",
      "import S from 'stripe';\nnew S('x');",
      "import S from 'stripe';\nnew S.Stripe('x');",
      "import S from 'stripe';\nS.default('x');",
      "import S from 'stripe';\nS.call(null, 'x');",
      "import S from 'stripe';\nReflect.construct(S, ['x']);",
      "import S from 'stripe';\nconst T = S;",
      "import S from 'stripe';\nnew (S as any)('x');",
      "import * as NS from 'stripe';\nNS.default('x');",
      "import { Stripe as S } from 'stripe';\nS('x');",
    ]) {
      assert.equal(uses(code), 1, code);
    }
    for (const code of [
      "import S from 'stripe';\nlet e: S.Event;",
      "import S from 'stripe';\nfunction f(s: S.Checkout.Session): S.Charge['id'] { return ''; }",
      "import S from 'stripe';\ntype C = typeof S;",
      "import S from 'stripe';\n// S('x') in a comment, S (the SDK) in prose",
      "import S from 'stripe';\nconst o = { S: 1 }; o.S;",
      "import type S from 'stripe';\nS;",
    ]) {
      assert.equal(uses(code), 0, code);
    }
  });

  test(`only ${VALUE_IMPORTERS.join(", ")} load the stripe package at runtime`, () => {
    const files = walk(ROOT).map((path) => relative(ROOT, path).split("\\").join("/"));
    const code = files.filter((file) => CODE.test(file));

    // Not vacuous: each allowed file loads the package, and a type-only
    // import elsewhere is left alone.
    for (const file of VALUE_IMPORTERS) {
      const source = parse(file, readFileSync(join(ROOT, file), "utf8"));
      assert.equal(stripeLoads(source).length, 1, `${file} must load stripe once, through its import`);
    }
    const typeOnly = "server/api/routers/admin/customer-spend.ts";
    assert.ok(code.includes(typeOnly), `the scan must cover ${typeOnly}`);
    assert.deepEqual(stripeLoads(parse(typeOnly, readFileSync(join(ROOT, typeOnly), "utf8"))), []);

    const offenders = code.filter(
      (file) =>
        !VALUE_IMPORTERS.includes(file) &&
        stripeLoads(parse(file, readFileSync(join(ROOT, file), "utf8"))).length > 0,
    );
    assert.deepEqual(
      offenders,
      [],
      `import Stripe types with "import type", and the client from "@/lib/clients/stripe"`,
    );
  });

  test(`the webhook files use their stripe import only as a type; ${BOUNDARY} constructs with it`, () => {
    const read = (file: string) => parse(file, readFileSync(join(ROOT, file), "utf8"));

    // Not vacuous: the check sees the boundary's own constructors.
    assert.ok(valueUses(read(BOUNDARY)).length > 0, `${BOUNDARY} constructs its clients`);

    for (const file of VALUE_IMPORTERS) {
      if (file === BOUNDARY) continue;
      const source = read(file);
      assert.ok(boundNames(source).length > 0, `${file} must bind the stripe import to a name`);
      assert.deepEqual(
        valueUses(source),
        [],
        `${file} uses the Stripe SDK as a value; import the client from "@/lib/clients/stripe"`,
      );
    }
  });
});
