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
});
