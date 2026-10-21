/**
 * #1140: the navbar's mobile menu toggle was a 24x2 px tap target.
 *
 * HeroUI's toggle slot is `w-6 h-full`, and the navbar wrapper is `h-auto`
 * (so the bar can grow at a 200% text size), so `h-full` resolved against a
 * list with no content of its own: the button was 2 px tall, and a tap 8 px
 * above or below the hamburger icon missed it (WCAG 2.2 SC 2.5.8, 24x24 min).
 *
 * This file renders the REAL `Navbar` and checks the toggle button's class
 * list after HeroUI's tailwind-merge: a fixed 48 px box (`size-12`), with
 * HeroUI's `w-6` and `h-full` merged away. The 48x48 box itself was measured
 * in Chromium at 390, 800 and 1000 px wide (PR body, `## Verified`).
 */
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import test, { mock } from "node:test";
import { fileURLToPath } from "node:url";

import { NextIntlClientProvider } from "next-intl";
import { createElement, type ComponentType } from "react";
import { renderToStaticMarkup } from "react-dom/server";

import { DownloadModalProvider } from "@/contexts/DownloadModalContext";

/** `@types/node@20` has no declaration for `mock.module` (see cloud-credits-card-error-state.test.ts). */
interface ModuleMocker {
  module(
    specifier: string,
    options: { namedExports?: Record<string, unknown> },
  ): void;
}

const moduleMock = mock as unknown as ModuleMocker;

// The navbar reads the pathname (useSiteHref, the language switcher) and a
// static render has no app router mounted, so stand one in for /en.
moduleMock.module("next/navigation", {
  namedExports: {
    usePathname: () => "/en",
    useRouter: () => ({ push() {}, replace() {}, prefetch() {} }),
    useSearchParams: () => new URLSearchParams(),
    useParams: () => ({ locale: "en" }),
    redirect() {},
    permanentRedirect() {},
    notFound() {},
  },
});

const ROOT = fileURLToPath(new URL("..", import.meta.url));
// A variable specifier: a literal `.tsx` specifier is a TS5097 error here.
const NAVBAR_PATH = "../components/navbar.tsx";

async function renderNavbar(): Promise<string> {
  const { Navbar } = (await import(NAVBAR_PATH)) as { Navbar: ComponentType };
  const messages = JSON.parse(
    readFileSync(join(ROOT, "messages/en.json"), "utf8"),
  ) as Record<string, unknown>;

  return renderToStaticMarkup(
    createElement(NextIntlClientProvider, {
      locale: "en",
      messages,
      timeZone: "UTC",
      children: createElement(DownloadModalProvider, {
        children: createElement(Navbar),
      }),
    }),
  );
}

test("the menu toggle is a 48x48 box, not HeroUI's w-6 h-full", async () => {
  const html = await renderNavbar();
  const buttons = Array.from(html.matchAll(/<button\b[^>]*>/g)).map(
    (m) => m[0],
  );
  const toggle = buttons.find((tag) => /\baria-pressed=/.test(tag));

  assert.ok(toggle, "the navbar renders a menu toggle button");
  const classes = (/\bclass="([^"]*)"/.exec(toggle)?.[1] ?? "").split(/\s+/);

  assert.ok(
    classes.includes("size-12"),
    `toggle classes: ${classes.join(" ")}`,
  );
  assert.ok(
    !classes.includes("h-full"),
    `h-full still on the toggle: ${classes.join(" ")}`,
  );
  assert.ok(
    !classes.includes("w-6"),
    `w-6 still on the toggle: ${classes.join(" ")}`,
  );
});
