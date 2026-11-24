/**
 * The /linux landing page ("the Wispr Flow alternative for Linux").
 *
 * Renders the REAL page body under each locale and its real catalogue, and
 * checks that every catalogue carries every `linuxPage` key English has, that
 * the download button points at the .deb in public/linux-latest.json, and that
 * the real sitemap() lists /linux on every locale.
 */
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

import { NextIntlClientProvider } from "next-intl";
import { createElement, type ComponentType } from "react";
import { renderToStaticMarkup } from "react-dom/server";

import { locales } from "../src/i18n/locales";

import { moduleMock } from "./module-mock";

// The blog loader imports "server-only" and the database; only the page list
// is under test here.
moduleMock.module(new URL("../src/content/blog.ts", import.meta.url).href, {
  namedExports: { getAllBlogPosts: async () => [] },
});

const ROOT = fileURLToPath(new URL("..", import.meta.url));
// A variable specifier: a literal `.tsx` specifier is a TS5097 error here.
const CONTENT_PATH = "../components/linux/linux-page-content.tsx";

type Catalogue = Record<string, unknown> & {
  linuxPage: Record<string, string>;
};

function readCatalogue(locale: string): Catalogue {
  return JSON.parse(
    readFileSync(join(ROOT, `messages/${locale}.json`), "utf8"),
  ) as Catalogue;
}

const latest = JSON.parse(
  readFileSync(join(ROOT, "public/linux-latest.json"), "utf8"),
) as { version: string; deb: string; releasePage: string };

const enKeys = Object.keys(readCatalogue("en").linuxPage).sort();

async function render(locale: string, messages: Catalogue): Promise<string> {
  const { default: LinuxPageContent } = (await import(CONTENT_PATH)) as {
    default: ComponentType;
  };

  return renderToStaticMarkup(
    createElement(NextIntlClientProvider, {
      locale,
      messages,
      timeZone: "UTC",
      children: createElement(LinuxPageContent),
    }),
  );
}

const unescape = (html: string) =>
  html
    .replace(/&quot;/g, '"')
    .replace(/&#x27;/g, "'")
    .replace(/&amp;/g, "&");

for (const locale of locales) {
  test(`${locale}: the Linux page renders with every linuxPage key`, async () => {
    const messages = readCatalogue(locale);

    assert.deepEqual(Object.keys(messages.linuxPage ?? {}).sort(), enKeys);
    for (const [key, value] of Object.entries(messages.linuxPage)) {
      assert.ok(value.trim().length > 0, `${locale} linuxPage.${key} is empty`);
    }
    assert.ok(
      messages.linuxPage.downloadCta.includes("{version}"),
      `${locale} downloadCta lost its {version} placeholder`,
    );

    const html = unescape(await render(locale, messages));

    assert.match(html, /<h1[^>]*>[^<]*Wispr Flow[^<]*<\/h1>/);
    assert.ok(html.includes(`href="${latest.deb}"`), "the .deb download link");
    assert.ok(html.includes(`href="${latest.releasePage}"`), "release notes");
    assert.ok(
      html.includes(`sudo apt install ./hyperwhisper_${latest.version}_amd64.deb`),
      "the install command names the current version",
    );
    assert.ok(html.includes('sudo usermod -aG hyperwhisper-input "$USER"'));
    assert.ok(
      html.includes("https://www.hyperwhisper.com/docs/linux-installation"),
    );
    // No AppImage is shipped; the page must never promise one.
    assert.doesNotMatch(html, /appimage/i);
  });
}

test("the navbar, footer and download page labels exist in every catalogue", () => {
  for (const locale of locales) {
    const m = readCatalogue(locale) as unknown as {
      navbar: { linux?: string };
      footer: { links: { linux?: string } };
      downloadPage: { learnMoreLinux?: string };
    };

    assert.ok(m.navbar.linux, `${locale} navbar.linux`);
    assert.ok(m.footer.links.linux, `${locale} footer.links.linux`);
    assert.ok(m.downloadPage.learnMoreLinux, `${locale} learnMoreLinux`);
  }
});

test("the sitemap lists /linux on every locale with alternates", async () => {
  const { default: sitemap } = await import("../app/sitemap");
  const entries = await sitemap();

  for (const locale of locales) {
    const entry = entries.find(
      (e) => e.url === `https://www.hyperwhisper.com/${locale}/linux`,
    );

    assert.ok(entry, `${locale}/linux missing from the sitemap`);
    assert.ok(entry.alternates?.languages, `${locale}/linux has no alternates`);
  }
});
