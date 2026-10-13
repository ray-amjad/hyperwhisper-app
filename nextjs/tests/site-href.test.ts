/**
 * #1170: on an English-only page the navbar's and footer's site links
 * followed useLocale() ("en"), so a German visitor clicking "Features" on
 * /en/latency landed on /en#features, which wrote NEXT_LOCALE=en. After
 * hydration siteHref now follows a saved NEXT_LOCALE, and with none leaves the
 * link unprefixed, so next-intl picks the locale as on a first visit
 * (Accept-Language) and writes no cookie. The middleware half is checked here
 * against next-intl itself; the browser check is the verify step.
 */
import assert from "node:assert/strict";
import test from "node:test";

import createMiddleware from "next-intl/middleware";
import { NextRequest } from "next/server";

import { isEnglishOnlyPath, siteHref } from "../src/i18n/english-only";
import { routing } from "../src/i18n/routing";

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

test("after hydration with no usable cookie the link is unprefixed", () => {
  for (const cookie of ["", "NEXT_LOCALE=xx", "MY_NEXT_LOCALE=de", "NEXT_LOCALE=%E0"]) {
    for (const path of PATHS) {
      assert.equal(siteHref("/en/latency", "en", cookie, path), path, cookie);
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

const intl = createMiddleware(routing);

function visit(path: string, headers: Record<string, string>) {
  const response = intl(
    new NextRequest(new URL(path, "https://hyperwhisper.com"), {
      headers: { "sec-fetch-dest": "document", ...headers },
    }),
  );

  return {
    location: response.headers.get("location"),
    cookie: response.cookies.get("NEXT_LOCALE")?.value,
  };
}

test("an unprefixed link keeps the visitor's language and its cookie", () => {
  // Accept-Language only, no cookie (R1-1): /de, and no cookie written.
  assert.deepEqual(visit("/", { "accept-language": "de" }), {
    location: "https://hyperwhisper.com/de",
    cookie: undefined,
  });
  assert.deepEqual(visit("/support", { "accept-language": "de" }), {
    location: "https://hyperwhisper.com/de/support",
    cookie: undefined,
  });
  // A saved choice wins over Accept-Language and is left as it is.
  assert.deepEqual(
    visit("/", { "accept-language": "en", cookie: "NEXT_LOCALE=fr" }),
    { location: "https://hyperwhisper.com/fr", cookie: undefined },
  );
});

test("the old /en link is what overwrote the cookie", () => {
  assert.equal(visit("/en", { "accept-language": "de" }).cookie, "en");
});
