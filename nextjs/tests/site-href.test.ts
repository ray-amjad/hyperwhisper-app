/**
 * #1170: on an English-only page the navbar's and footer's site links
 * followed useLocale() ("en"), so a German visitor clicking "Features" on
 * /en/latency landed on /en#features, which wrote NEXT_LOCALE=en. After
 * hydration siteHref now follows a saved NEXT_LOCALE, and with none keeps
 * `/en...` (Done when 4). The browser check is the verify step.
 */
import assert from "node:assert/strict";
import test from "node:test";

import { isEnglishOnlyPath, siteHref } from "../src/i18n/english-only";

const ENGLISH_ONLY = [
  "/en/latency",
  "/en/latency/",
  "/en/choosing-a-model",
  "/en/blog",
  "/en/blog/some-post",
];

const PATHS = ["/", "/#features", "/#cloud", "/support", "/legal/privacy-policy"];

function prefixed(locale: string, path: string) {
  if (path === "/") return `/${locale}`;

  return path.startsWith("/#") ? `/${locale}${path.slice(1)}` : `/${locale}${path}`;
}

test("English-only pages keep /en in the server HTML", () => {
  for (const pathname of ENGLISH_ONLY) {
    assert.equal(isEnglishOnlyPath(pathname), true, pathname);
    for (const path of PATHS) {
      assert.equal(siteHref(pathname, "en", undefined, path), prefixed("en", path));
    }
  }
});

test("after hydration English-only pages follow a saved NEXT_LOCALE", () => {
  for (const pathname of ENGLISH_ONLY) {
    for (const path of PATHS) {
      assert.equal(siteHref(pathname, "en", "NEXT_LOCALE=de", path), prefixed("de", path));
      assert.equal(
        siteHref(pathname, "en", "a=1; NEXT_LOCALE=zh-Hant; b=2", path),
        prefixed("zh-Hant", path),
      );
      assert.equal(siteHref(pathname, "en", "NEXT_LOCALE=en", path), prefixed("en", path));
    }
  }
});

test("after hydration with no usable cookie the link keeps /en", () => {
  for (const cookie of ["", "NEXT_LOCALE=xx", "MY_NEXT_LOCALE=de", "NEXT_LOCALE=%E0"]) {
    for (const path of PATHS) {
      assert.equal(siteHref("/en/latency", "en", cookie, path), prefixed("en", path), cookie);
    }
  }
});

test("ordinary pages prefix the page locale whatever the cookie says", () => {
  for (const [pathname, locale] of [
    ["/en", "en"],
    ["/en/download", "en"],
    ["/en/latency-tips", "en"],
    ["/de", "de"],
    ["/de/latency", "de"],
    ["/ja/blog", "ja"],
  ] as const) {
    assert.equal(isEnglishOnlyPath(pathname), false, pathname);
    for (const cookie of [undefined, "", "NEXT_LOCALE=fr"]) {
      for (const path of PATHS) {
        assert.equal(siteHref(pathname, locale, cookie, path), prefixed(locale, path));
      }
    }
  }
});
