/**
 * #2402: the footer "Open Source" link was hard-coded English in all 40
 * locales, while every other footer label went through `t("links.*")`.
 *
 * Renders the REAL `FooterSection` under every locale with its real messages
 * and checks that the /open-source link carries that locale's
 * `footer.links.openSource` label. On main this fails in all 40 locales: the
 * key does not exist.
 */
import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

import { NextIntlClientProvider } from "next-intl";
import { createElement, type ComponentType } from "react";
import { renderToStaticMarkup } from "react-dom/server";

import { DownloadModalProvider } from "@/contexts/DownloadModalContext";

const ROOT = fileURLToPath(new URL("..", import.meta.url));
// A variable specifier: a literal `.tsx` specifier is a TS5097 error here.
const FOOTER_PATH = "../components/landing/FooterSection.tsx";

type Messages = { footer: { links: { openSource?: string } } };

const LOCALES = readdirSync(join(ROOT, "messages"))
  .filter((f) => f.endsWith(".json"))
  .map((f) => f.replace(/\.json$/, ""));

/** The text of every <a> whose href is `href`, tags stripped. */
function labelsOfHref(html: string, href: string): string[] {
  return Array.from(html.matchAll(/<a\b([^>]*)>([\s\S]*?)<\/a>/g))
    .filter((m) => /\bhref="([^"]*)"/.exec(m[1])?.[1] === href)
    .map((m) => m[2].replace(/<[^>]+>/g, "").trim());
}

test("all 40 catalogues are covered", () => {
  assert.equal(LOCALES.length, 40);
});

for (const locale of LOCALES) {
  test(`under ${locale}, the footer Open Source link uses footer.links.openSource`, async () => {
    const { default: FooterSection } = (await import(FOOTER_PATH)) as {
      default: ComponentType;
    };
    const messages = JSON.parse(
      readFileSync(join(ROOT, `messages/${locale}.json`), "utf8"),
    ) as Messages;
    const label = messages.footer.links.openSource;
    assert.ok(label, `messages/${locale}.json has no footer.links.openSource`);

    const html = renderToStaticMarkup(
      createElement(NextIntlClientProvider, {
        locale,
        messages,
        timeZone: "UTC",
        children: createElement(DownloadModalProvider, {
          children: createElement(FooterSection),
        }),
      }),
    );

    assert.deepEqual(labelsOfHref(html, `/${locale}/open-source`), [label]);
  });
}
