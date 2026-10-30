/**
 * The language switcher on /en/latency, /en/choosing-a-model or /en/blog sent
 * a visitor who picked 日本語 to /ja/latency, a 404: those pages exist only in
 * English. next-intl's usePathname() strips the locale, so the switcher tests
 * the bare path with isEnglishOnlyRoute and goes to the new locale's homepage.
 */
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

import { NextRequest } from "next/server";

import { languageSwitchHref } from "../components/language-switcher";
import proxy from "../proxy";
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

test("the language switcher never styles <html> (WebKit closed the phone menu)", () => {
  const source = readFileSync(
    new URL("../components/language-switcher.tsx", import.meta.url),
    "utf8",
  );

  assert.doesNotMatch(source, /document\.documentElement/);
});

/*
 * #2939: on an English-only page useLocale() is always "en", so picking
 * English hit the `newLocale === locale` early return and did nothing, while
 * the visitor's saved NEXT_LOCALE=de (never rewritten there, #1135) kept
 * driving the navbar links and the bare "/" redirect. Picking English there now
 * goes to /en like any other language goes to its homepage, and /en is an
 * ordinary page whose middleware writes NEXT_LOCALE=en.
 */
test("#2939: picking English on an English-only page goes to the English homepage", () => {
  for (const pathname of [
    "/latency",
    "/blog",
    "/blog/some-post",
    "/choosing-a-model",
  ]) {
    assert.equal(languageSwitchHref("en", "en", pathname, ""), "/", pathname);
    assert.equal(
      languageSwitchHref("en", "en", pathname, "#results"),
      "/",
      `${pathname} with a hash`,
    );
  }
});

test("picking the current language on a translated page does not navigate", () => {
  assert.equal(languageSwitchHref("en", "en", "/support", ""), null);
  assert.equal(languageSwitchHref("de", "de", "/", "#cloud"), null);
});

test("another language from an English-only page still goes to its homepage", () => {
  assert.equal(languageSwitchHref("ja", "en", "/latency", ""), "/");
  assert.equal(languageSwitchHref("de", "en", "/blog/some-post", "#x"), "/");
});

test("elsewhere the switch keeps the path and #section", () => {
  assert.equal(languageSwitchHref("de", "en", "/", "#cloud"), "/#cloud");
  assert.equal(languageSwitchHref("ja", "en", "/support", ""), "/support");
});

test("#2939: the menu hands every press to the handler, even the selected language", () => {
  const source = readFileSync(
    new URL("../components/language-switcher.tsx", import.meta.url),
    "utf8",
  );

  // React Aria toggles off an already-selected item, so onSelectionChange
  // reported an empty selection for a pick of the current language.
  assert.match(
    source,
    /onAction=\{\(key\) => handleLanguageChange\(String\(key\)\)\}/,
  );
  assert.doesNotMatch(source, /onSelectionChange=/);
});

test("#2939: the /en the switcher goes to rewrites a saved NEXT_LOCALE=de to en", async () => {
  const response = await proxy(
    new NextRequest(new URL("/en", "https://www.hyperwhisper.com"), {
      headers: { cookie: "NEXT_LOCALE=de", "sec-fetch-dest": "document" },
    }),
  );

  assert.equal(response.cookies.get("NEXT_LOCALE")?.value, "en");
  assert.ok(
    response.headers
      .getSetCookie()
      .some((cookie) => cookie.startsWith("NEXT_LOCALE=en;")),
  );
});
