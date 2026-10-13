/**
 * #1170: on an English-only page the navbar's home and section links followed
 * useLocale() ("en"), so a German visitor clicking "Features" on /en/latency
 * landed on /en#features, which wrote NEXT_LOCALE=en. The navbar now resolves
 * those links with navbarHomeLocale(pathname, locale, document.cookie) after
 * hydration. This covers the decision; the browser check is the verify step.
 */
import assert from "node:assert/strict";
import test from "node:test";

import {
  isEnglishOnlyPath,
  navbarHomeLocale,
} from "../src/i18n/english-only";

const ENGLISH_ONLY = [
  "/en/latency",
  "/en/latency/",
  "/en/choosing-a-model",
  "/en/blog",
  "/en/blog/some-post",
];

test("English-only paths follow a saved NEXT_LOCALE", () => {
  for (const path of ENGLISH_ONLY) {
    assert.equal(isEnglishOnlyPath(path), true, path);
    assert.equal(navbarHomeLocale(path, "en", "NEXT_LOCALE=de"), "de", path);
    assert.equal(
      navbarHomeLocale(path, "en", "a=1; NEXT_LOCALE=zh-Hant; b=2"),
      "zh-Hant",
      path,
    );
  }
});

test("English-only paths stay en with no cookie, an en cookie or a bad one", () => {
  for (const cookie of ["", "NEXT_LOCALE=en", "NEXT_LOCALE=xx", "MY_NEXT_LOCALE=de", "NEXT_LOCALE=%E0"]) {
    assert.equal(navbarHomeLocale("/en/latency", "en", cookie), "en", cookie);
  }
});

test("ordinary pages keep the page locale whatever the cookie says", () => {
  for (const [path, locale] of [
    ["/en", "en"],
    ["/en/download", "en"],
    ["/en/latency-tips", "en"],
    ["/de", "de"],
    ["/de/latency", "de"],
    ["/ja/blog", "ja"],
  ] as const) {
    assert.equal(isEnglishOnlyPath(path), false, path);
    assert.equal(navbarHomeLocale(path, locale, "NEXT_LOCALE=fr"), locale, path);
  }
});
