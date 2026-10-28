/**
 * #1916: the hero's platform badges are Latin labels that END in a neutral
 * ("macOS 14.6+", "Windows 10+", "Linux (amd64)"). Inside an RTL run (/ar,
 * /he) the bidi algorithm moves that trailing "+" or ")" to the visual start,
 * so the badge read "+macOS 14.6". Each badge label is now a `<bdi>`, which
 * isolates it and resolves its direction from its own first strong letter.
 *
 * The test renders the REAL `HeroSection` with the real Arabic, Hebrew and
 * English messages and asserts the served markup: every badge label is the
 * whole content of a `<bdi>`. The bytes on the wire are what the browser
 * lays out, so a prop that never serialised would not satisfy it.
 */
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { NextIntlClientProvider } from "next-intl";

// A VARIABLE specifier: a literal `.tsx` specifier is a TS5097 error under this
// tsconfig. Same rule the other tests in this folder document.
const HERO_PATH = "../components/landing/HeroSection.tsx";
const CONTEXT_PATH = "../contexts/DownloadModalContext.tsx";

type Messages = { hero: Record<string, string> };

function messages(locale: string): Messages {
  return JSON.parse(
    readFileSync(
      new URL(`../messages/${locale}.json`, import.meta.url),
      "utf8",
    ),
  ) as Messages;
}

async function renderHero(locale: string, msgs: Messages): Promise<string> {
  const { default: HeroSection } = (await import(HERO_PATH)) as {
    default: () => React.ReactElement;
  };
  const { DownloadModalProvider } = (await import(CONTEXT_PATH)) as {
    DownloadModalProvider: (p: {
      children: React.ReactNode;
    }) => React.ReactElement;
  };

  return renderToStaticMarkup(
    createElement(NextIntlClientProvider, {
      locale,
      messages: msgs,
      children: createElement(DownloadModalProvider, {
        children: createElement(HeroSection),
      }),
    }),
  );
}

function escapeHtml(s: string): string {
  return s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
}

function escapeRegex(s: string): string {
  return s.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

for (const locale of ["ar", "he", "en"]) {
  test(`/${locale} hero: every platform badge label is isolated in a <bdi>`, async () => {
    const msgs = messages(locale);
    const html = await renderHero(locale, msgs);

    for (const key of ["macos", "windows", "linux"]) {
      const label = msgs.hero[key];

      assert.ok(label, `${locale}.json has hero.${key}`);
      assert.match(
        html,
        new RegExp(`<bdi\\b[^>]*>${escapeRegex(escapeHtml(label))}</bdi>`),
        `hero.${key} ("${label}") is not the whole content of a <bdi>`,
      );
    }
  });
}
