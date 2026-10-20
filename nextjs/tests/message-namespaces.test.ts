/**
 * #734: every top-level namespace in `messages/en.json` must be bound by a
 * real translations call.
 *
 * next-intl reaches a key only through `useTranslations("<ns>")` or
 * `getTranslations("<ns>" | { namespace: "<ns>" })`. When the pricing section
 * moved onto `cloudSection`, the old `pricing` namespace was left behind and
 * shipped, translated, in all 40 catalogues for nothing. This file scans the
 * app's source for every namespace binding and fails on a catalogue namespace
 * no call names, so the next rewrite cannot leave one behind.
 *
 * It also checks each catalogue carries the same namespaces as English, so a
 * namespace removed from en.json is removed from every locale too.
 */
import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const ROOT = fileURLToPath(new URL("..", import.meta.url));
const MESSAGES = join(ROOT, "messages");
const SKIP_DIRS = new Set(["node_modules", ".next", ".git", "messages", "tests", "public", "drizzle"]);

type Catalogue = Record<string, unknown>;

function readCatalogue(file: string): Catalogue {
  return JSON.parse(readFileSync(join(MESSAGES, file), "utf8")) as Catalogue;
}

function sourceText(dir: string): string {
  let src = "";
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    if (SKIP_DIRS.has(entry.name)) continue;
    const path = join(dir, entry.name);
    if (entry.isDirectory()) src += sourceText(path);
    else if (/\.(ts|tsx|js|jsx|mjs)$/.test(entry.name)) src += readFileSync(path, "utf8") + "\n";
  }
  return src;
}

function boundNamespaces(src: string): Set<string> {
  const bound = new Set<string>();
  const patterns = [
    /\b(?:useTranslations|getTranslations)\(\s*["'`]([^"'`]+)["'`]/g,
    /\bnamespace:\s*["'`]([^"'`]+)["'`]/g,
  ];
  for (const re of patterns) {
    for (const m of Array.from(src.matchAll(re))) bound.add(m[1].split(".")[0]);
  }
  return bound;
}

const en = readCatalogue("en.json");
const locales = readdirSync(MESSAGES).filter((f) => f.endsWith(".json"));
const bound = boundNamespaces(sourceText(ROOT));

test("the scanner finds the bindings it should (positive control)", () => {
  // useTranslations, getTranslations({ namespace }) and getTranslations("ns").
  for (const ns of ["cloudSection", "openSource", "notFound"]) {
    assert.ok(bound.has(ns), `the scanner missed the live ${ns} binding`);
  }
});

test("every top-level namespace in messages/en.json is bound by a translations call", () => {
  const unbound = Object.keys(en).filter((ns) => !bound.has(ns));
  assert.deepEqual(unbound, [], `unbound namespaces: ${unbound.join(", ")}`);
});

test("every catalogue carries the same top-level namespaces as en.json", () => {
  assert.equal(locales.length, 40);
  const expected = Object.keys(en).sort();
  for (const file of locales) {
    assert.deepEqual(Object.keys(readCatalogue(file)).sort(), expected, file);
  }
});
