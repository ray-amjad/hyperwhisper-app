/**
 * The language switcher on /en/latency, /en/choosing-a-model or /en/blog sent
 * a visitor who picked 日本語 to /ja/latency, a 404: those pages exist only in
 * English. next-intl's usePathname() strips the locale, so the switcher tests
 * the bare path with isEnglishOnlyRoute and goes to the new locale's homepage.
 */
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

import { isEnglishOnlyRoute } from "../src/i18n/english-only";

test("isEnglishOnlyRoute matches the bare English-only paths", () => {
  for (const pathname of [
    "/latency",
    "/latency/",
    "/choosing-a-model",
    "/blog",
    "/blog/some-post",
  ]) {
    assert.equal(isEnglishOnlyRoute(pathname), true, pathname);
  }
});

test("isEnglishOnlyRoute leaves translated pages alone", () => {
  for (const pathname of [
    "/",
    "/support",
    "/open-source",
    "/legal/privacy-policy",
    "/latencyx",
    "/blogroll",
  ]) {
    assert.equal(isEnglishOnlyRoute(pathname), false, pathname);
  }
});

test("the language switcher routes English-only pages through isEnglishOnlyRoute", () => {
  const source = readFileSync(
    new URL("../components/language-switcher.tsx", import.meta.url),
    "utf8",
  );

  assert.match(source, /isEnglishOnlyRoute\(pathname\)/);
  assert.match(source, /window\.location\.hash/);
});
