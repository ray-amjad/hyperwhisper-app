/**
 * #1132: the footer Blog link was a dead 404 in every translated locale.
 *
 * The blog is English-only — `app/[locale]/blog/page.tsx` calls `notFound()`
 * for every locale except `en` — but the footer built the link with the
 * locale-prefixing `Link`, so `/de` linked to `/de/blog`. This file renders the
 * REAL `FooterSection` under a real locale and its real messages, and checks
 * that the Blog anchor points at `/en/blog`, keeps its translated label, and
 * that no `/<locale>/blog` href is served.
 *
 * Positive control: the Older Versions link still goes through the
 * locale-prefixing `Link`, so `/de/older-versions` must appear. That proves the
 * render really ran under the locale and the Link wiring was exercised.
 */
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
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

type Messages = { footer: { links: { blog: string; olderVersions: string } } };

async function render(locale: string): Promise<{ html: string; messages: Messages }> {
  const { default: FooterSection } = (await import(FOOTER_PATH)) as {
    default: ComponentType;
  };
  const messages = JSON.parse(
    readFileSync(join(ROOT, `messages/${locale}.json`), "utf8"),
  ) as Messages;
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

  return { html, messages };
}

/** The href of every <a> whose text, tags stripped, equals `label`. */
function hrefsOfLabel(html: string, label: string): string[] {
  return Array.from(html.matchAll(/<a\b([^>]*)>([\s\S]*?)<\/a>/g))
    .filter((m) => m[2].replace(/<[^>]+>/g, "").trim() === label)
    .map((m) => /\bhref="([^"]*)"/.exec(m[1])?.[1] ?? "");
}

for (const locale of ["de", "ja", "ar", "en"]) {
  test(`under ${locale}, the footer Blog link is /en/blog with the translated label`, async () => {
    const { html, messages } = await render(locale);
    const label = messages.footer.links.blog;

    assert.deepEqual(hrefsOfLabel(html, label), ["/en/blog"]);
    if (locale !== "en") {
      assert.ok(
        !html.includes(`href="/${locale}/blog`),
        `a /${locale}/blog href is served, and that page 404s`,
      );
    }
    assert.ok(
      !html.includes(`href="/${locale}/en/blog`),
      "the /en/blog href was locale-prefixed a second time",
    );
    // Positive control: the locale-prefixing Link still ran under this locale.
    assert.deepEqual(hrefsOfLabel(html, messages.footer.links.olderVersions), [
      `/${locale}/older-versions`,
    ]);
  });
}
