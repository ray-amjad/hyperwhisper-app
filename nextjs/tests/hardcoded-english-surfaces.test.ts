/**
 * #1059: four surfaces were hard-coded English JSX in all 40 locales — the
 * open-source banner (on every route), the /open-source page, the Linux card on
 * /download and the /older-versions page.
 *
 * 1. Renders the REAL banner and /open-source body under every locale with its
 *    real messages: the copy is that locale's, the issue's English sentences are
 *    gone outside /en, and every link and <strong> survives the translation
 *    (a dropped rich-text tag would silently lose a link).
 * 2. /download and /older-versions are client pages whose Linux card and error
 *    state do not render on first paint, so for them (and for every surface)
 *    the test reads the source: every key a surface asks for exists in all 40
 *    catalogues with the same placeholders and tags as en, and the English
 *    sentences the issue names are no longer in the source.
 */
import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

import { NextIntlClientProvider } from "next-intl";
import { createElement, type ComponentType } from "react";
import { renderToStaticMarkup } from "react-dom/server";

const ROOT = fileURLToPath(new URL("..", import.meta.url));
// Variable specifiers: a literal `.tsx` specifier is a TS5097 error here.
const BANNER_PATH = "../components/open-source-banner.tsx";
const CONTENT_PATH = "../components/open-source-content.tsx";

type Catalogue = Record<string, Record<string, unknown>>;

const LOCALES = readdirSync(join(ROOT, "messages"))
  .filter((f) => f.endsWith(".json"))
  .map((f) => f.replace(/\.json$/, ""));

function load(locale: string): Catalogue {
  return JSON.parse(
    readFileSync(join(ROOT, `messages/${locale}.json`), "utf8"),
  ) as Catalogue;
}

const EN = load("en");

// The surfaces' English sentences the issue's scan found, plus the rest of the
// /open-source copy and the Linux card.
const ENGLISH = [
  "HyperWhisper is now fully open source",
  "Now open source",
  "Learn more",
  "Dismiss announcement",
  "Nothing to hide",
  "Why it matters",
  "Verify, don't trust",
  "Fork and self-host",
  "Built in the open",
  "Already bought a license?",
  "Bought in the last 30 days",
  "Bought more than 30 days ago",
  "Want to contribute?",
  "send us a note",
  "Debian package for 64-bit Intel/AMD systems",
  "Checksums and release notes",
  "Install the Debian package",
  "Log out and back in",
  "Linux releases also include a static APT repository archive",
  "Read the Linux installation guide",
  "Self-host an APT repository",
  "Browse and download all releases of HyperWhisper",
  "Error loading versions:",
  "An error occurred",
  "Loading versions...",
];

const SURFACES = [
  "components/open-source-banner.tsx",
  "components/open-source-content.tsx",
  "app/[locale]/open-source/page.tsx",
  "app/[locale]/download/page.tsx",
  "app/[locale]/older-versions/page.tsx",
];

function text(html: string): string {
  return html
    .replace(/<[^>]+>/g, "")
    .replace(/&#x27;|&#39;/g, "'")
    .replace(/&quot;/g, '"')
    .replace(/&amp;/g, "&");
}

async function render(path: string, locale: string, props = {}) {
  const mod = (await import(path)) as Record<string, ComponentType<object>>;
  const Component = mod.default ?? mod.OpenSourceBanner;

  return renderToStaticMarkup(
    createElement(NextIntlClientProvider, {
      locale,
      messages: load(locale),
      timeZone: "UTC",
      children: createElement(Component, props),
    }),
  );
}

for (const locale of LOCALES) {
  test(`${locale}: the banner and /open-source read ${locale}.json`, async () => {
    const messages = load(locale);
    const banner = messages.openSourceBanner as Record<string, string>;
    const openSource = messages.openSource as Record<string, string>;

    const bannerHtml = await render(BANNER_PATH, locale, { onDismiss: () => {} });

    assert.ok(text(bannerHtml).includes(banner.text));
    assert.ok(text(bannerHtml).includes(banner.shortText));
    assert.ok(text(bannerHtml).includes(banner.learnMore));
    assert.ok(bannerHtml.includes(`href="/${locale}/open-source"`));
    assert.match(bannerHtml, /aria-label="([^"]+)"/);
    assert.equal(
      text(/aria-label="([^"]+)"/.exec(bannerHtml)![1]),
      banner.dismiss,
    );

    const html = await render(CONTENT_PATH, locale);
    const body = text(html);

    assert.ok(body.includes(openSource.heading), "H1 reads openSource.heading");
    for (const key of ["eyebrow", "lead", "whyHeading", "licenseHeading"]) {
      assert.ok(body.includes(openSource[key]), `openSource.${key} rendered`);
    }
    // Every rich-text tag rendered as its element, in every locale.
    const count = (needle: string) => html.split(needle).length - 1;

    assert.equal(
      count('href="https://github.com/ray-amjad/hyperwhisper-app"'),
      2,
    );
    assert.equal(
      count(
        'href="https://github.com/ray-amjad/hyperwhisper-app/blob/main/LICENSE"',
      ),
      1,
    );
    assert.equal(count('href="mailto:hi@support.hyperwhisper.com"'), 1);
    assert.ok(body.includes("hi@support.hyperwhisper.com"));
    assert.equal(count("subject=HyperWhisper%20%E2%80%94%20refund%20request"), 1);
    assert.equal(count("subject=HyperWhisper%20%E2%80%94%20convert%20my"), 1);
    assert.equal(count("<strong>"), 6);
    // An ICU parse slip would leave its syntax in the text.
    assert.doesNotMatch(body, /[<>{}]/);
    // The refund terms keep their numbers.
    assert.ok(body.includes("$5"), "the $5 credit is stated");
    assert.ok(body.split("30").length - 1 >= 2, "both 30-day terms are stated");

    if (locale !== "en") {
      for (const english of ENGLISH) {
        assert.ok(
          !`${text(bannerHtml + html)} ${bannerHtml}`.includes(english),
          `${locale} still serves "${english}"`,
        );
      }
    }
  });
}

/** Namespace + every key a surface asks for, read from its source. */
function keysOf(source: string): { namespace: string; keys: string[] } {
  const ns =
    /useTranslations\("([\w]+)"\)/.exec(source)?.[1] ??
    /namespace: "([\w]+)"/.exec(source)?.[1];

  assert.ok(ns, "the surface binds a namespace");
  const keys = Array.from(
    source.matchAll(/\bt(?:\.rich)?\(\s*"([\w.]+)"/g),
    (m) => m[1],
  );

  return { namespace: ns, keys: Array.from(new Set(keys)) };
}

const signature = (s: string) =>
  [...(s.match(/<\/?[a-z]+>|\{[a-z]+\}/g) ?? [])].sort().join(" ");

for (const surface of SURFACES) {
  test(`${surface}: every key it reads is in all ${LOCALES.length} catalogues, no English JSX left`, () => {
    const source = readFileSync(join(ROOT, surface), "utf8");

    for (const english of ENGLISH) {
      assert.ok(!source.includes(english), `"${english}" is hard-coded`);
    }
    if (surface === "app/[locale]/open-source/page.tsx") {
      assert.ok(!source.includes("export const metadata"));
      assert.match(source, /generateMetadata/);
    }

    const { namespace, keys } = keysOf(source);

    if (surface.endsWith("open-source/page.tsx")) {
      assert.deepEqual(keys.sort(), ["metaDescription", "metaTitle"]);
    } else {
      assert.ok(keys.length > 0);
    }
    assert.equal(LOCALES.length, 40);
    for (const locale of LOCALES) {
      const ns = load(locale)[namespace] ?? {};

      for (const key of keys) {
        const value = ns[key];
        const en = EN[namespace][key] as string;

        assert.equal(typeof value, "string", `${locale} ${namespace}.${key}`);
        assert.ok((value as string).trim(), `${locale} ${namespace}.${key} empty`);
        assert.equal(
          signature(value as string),
          signature(en),
          `${locale} ${namespace}.${key} placeholders/tags`,
        );
      }
    }
  });
}
