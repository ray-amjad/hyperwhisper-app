/**
 * #1045: LayoutWrapper already wraps every marketing page in <main>, so the
 * home page and /credits rendered a second <main> nested inside it (axe
 * landmark-no-duplicate-main, landmark-main-is-top-level). The /user routes
 * skip that wrapper, so /user/sign-in had no <main> at all (landmark-one-main).
 *
 * Pins the landmark owner for each shell: the marketing pages leave <main> to
 * LayoutWrapper, LayoutWrapper's /user branch stays a plain <div> (the
 * authenticated layout renders its own <main>), and the sign-in page renders
 * its own <main>.
 */
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { fileURLToPath } from "node:url";

const ROOT = fileURLToPath(new URL("..", import.meta.url));

function source(path: string): string {
  return readFileSync(`${ROOT}${path}`, "utf8");
}

function mainTags(path: string): number {
  return source(path).match(/<main\b/g)?.length ?? 0;
}

test("marketing pages leave the <main> landmark to LayoutWrapper", () => {
  assert.equal(mainTags("components/layout/LayoutWrapper.tsx"), 1);
  assert.equal(mainTags("app/[locale]/page.tsx"), 0);
  assert.equal(mainTags("app/[locale]/credits/page.tsx"), 0);
});

test("the sign-in page renders one <main> as its root", () => {
  const src = source("app/[locale]/user/sign-in/SignInClient.tsx");

  assert.equal(mainTags("app/[locale]/user/sign-in/SignInClient.tsx"), 1);
  assert.match(src, /return \(\s*<main\b/);
  assert.match(src, /<\/main>\s*\);\s*}\s*$/);
});

test("the authenticated /user layout keeps its own single <main>", () => {
  assert.equal(mainTags("app/[locale]/user/(authenticated)/layout.tsx"), 1);
});
