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

import { languageSwitch } from "../components/language-switcher";
import proxy from "../proxy";
import { isEnglishOnlyRoute } from "../src/i18n/english-only";
import { routing } from "../src/i18n/routing";

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
 * driving the navbar links and the bare "/" redirect.
 *
 * A soft router.replace to /en is not enough either: next-intl's client only
 * writes NEXT_LOCALE when the locale changes (en -> en does not), and its
 * middleware only syncs the cookie on a document request. So that one pick is
 * a full page load of /en, and the proxy writes NEXT_LOCALE=en.
 */
test("#2939: picking English on an English-only page loads /en as a document", () => {
  for (const pathname of [
    "/latency",
    "/blog",
    "/blog/some-post",
    "/choosing-a-model",
  ]) {
    for (const hash of ["", "#results"]) {
      assert.deepEqual(
        languageSwitch("en", "en", pathname, hash),
        { navigation: "document", href: "/en" },
        `${pathname}${hash}`,
      );
    }
  }
});

test("the document href /en assumes every locale is prefixed", () => {
  assert.equal(routing.localePrefix, "always");
});

test("picking the current language on a translated page does not navigate", () => {
  assert.equal(languageSwitch("en", "en", "/support", ""), null);
  assert.equal(languageSwitch("de", "de", "/", "#cloud"), null);
});

test("another language from an English-only page goes to its homepage client-side", () => {
  assert.deepEqual(languageSwitch("ja", "en", "/latency", ""), {
    navigation: "client",
    href: "/",
  });
  assert.deepEqual(languageSwitch("de", "en", "/blog/some-post", "#x"), {
    navigation: "client",
    href: "/",
  });
});

test("elsewhere the switch keeps the path and #section", () => {
  assert.deepEqual(languageSwitch("de", "en", "/", "#cloud"), {
    navigation: "client",
    href: "/#cloud",
  });
  assert.deepEqual(languageSwitch("ja", "en", "/support", ""), {
    navigation: "client",
    href: "/support",
  });
});

test("#2939: the switcher hands every press to the handler and loads a document href", () => {
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
  assert.match(
    source,
    /next\.navigation === "document"\)\s*\{\s*window\.location\.assign\(next\.href\)/,
  );
});

function requestEn(headers: Record<string, string>) {
  return new NextRequest(new URL("/en", "https://www.hyperwhisper.com"), {
    headers: { cookie: "NEXT_LOCALE=de", ...headers },
  });
}

// The document request the switcher's full page load of /en makes.
test("#2939: a document request to /en rewrites a saved NEXT_LOCALE=de to en", async () => {
  const response = await proxy(requestEn({ "sec-fetch-dest": "document" }));

  assert.equal(response.cookies.get("NEXT_LOCALE")?.value, "en");
  assert.ok(
    response.headers
      .getSetCookie()
      .some((cookie) => cookie.startsWith("NEXT_LOCALE=en;")),
  );
});

// Why the pick is not a soft navigation: its RSC fetch is a fetch() (the
// browser sends Sec-Fetch-Dest: empty), not a document request,
// and the middleware leaves the saved cookie alone.
test("#2939: a soft-navigation fetch of /en leaves NEXT_LOCALE=de alone", async () => {
  const response = await proxy(
    requestEn({ "sec-fetch-dest": "empty", rsc: "1" }),
  );

  assert.equal(response.cookies.get("NEXT_LOCALE"), undefined);
  assert.equal(
    response.headers
      .getSetCookie()
      .some((cookie) => cookie.startsWith("NEXT_LOCALE=")),
    false,
  );
});
