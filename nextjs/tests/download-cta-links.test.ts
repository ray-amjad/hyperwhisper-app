/**
 * Primary download CTAs must be crawlable <a href="/{locale}/download">
 * links, not <button>s. A left-click still opens the email-capture modal.
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
import { moduleMock } from "./module-mock";

const ROOT = fileURLToPath(new URL("..", import.meta.url));
const FOOTER_PATH = "../components/landing/FooterSection.tsx";
const PLATFORM_PATH = "../components/landing/PlatformButtons.tsx";
const NAVBAR_PATH = "../components/navbar.tsx";

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

type FooterMessages = {
  footer: { links: { download: string; blog: string } };
};

function hrefsOfLabel(html: string, label: string): string[] {
  return Array.from(html.matchAll(/<a\b([^>]*)>([\s\S]*?)<\/a>/g))
    .filter((m) => m[2].replace(/<[^>]+>/g, "").trim() === label)
    .map((m) => /\bhref="([^"]*)"/.exec(m[1])?.[1] ?? "");
}

function loadMessages(locale: string) {
  return JSON.parse(
    readFileSync(join(ROOT, `messages/${locale}.json`), "utf8"),
  );
}

async function render(path: string, locale: string, extra?: object) {
  const mod = (await import(path)) as {
    default?: ComponentType;
    Navbar?: ComponentType;
  };
  const Component = mod.default ?? mod.Navbar;
  const messages = loadMessages(locale);

  return renderToStaticMarkup(
    createElement(NextIntlClientProvider, {
      locale,
      messages,
      timeZone: "UTC",
      children: createElement(DownloadModalProvider, {
        children: createElement(Component!, extra),
      }),
    }),
  );
}

for (const locale of ["en", "de", "ja"]) {
  test(`under ${locale}, the footer Download link is /${locale}/download`, async () => {
    const html = await render(FOOTER_PATH, locale);
    const messages = loadMessages(locale) as FooterMessages;
    const label = messages.footer.links.download;

    assert.deepEqual(hrefsOfLabel(html, label), [`/${locale}/download`]);
  });
}

test("the footer points at the blog index and key commercial posts", async () => {
  const html = await render(FOOTER_PATH, "en");

  assert.deepEqual(hrefsOfLabel(html, "Blog"), ["/en/blog"]);
  assert.deepEqual(hrefsOfLabel(html, "Best dictation software"), [
    "/en/blog/best-dictation-software",
  ]);
  assert.deepEqual(hrefsOfLabel(html, "Free speech-to-text app"), [
    "/en/blog/free-speech-to-text-app",
  ]);
  assert.deepEqual(hrefsOfLabel(html, "Free speech-to-text software"), [
    "/en/blog/free-speech-to-text-software",
  ]);
  assert.deepEqual(hrefsOfLabel(html, "Best voice to text app"), [
    "/en/blog/best-voice-to-text-app",
  ]);
  assert.deepEqual(hrefsOfLabel(html, "How to transcribe audio to text"), [
    "/en/blog/how-to-transcribe-audio-to-text",
  ]);
  assert.deepEqual(hrefsOfLabel(html, "Best dictation software for Windows"), [
    "/en/blog/best-dictation-software-for-windows",
  ]);
  assert.deepEqual(hrefsOfLabel(html, "Best dictation app for Mac"), [
    "/en/blog/mac-voice-dictation-software",
  ]);
  assert.deepEqual(hrefsOfLabel(html, "Best voice recognition software"), [
    "/en/blog/best-voice-recognition-software",
  ]);
  assert.deepEqual(hrefsOfLabel(html, "HyperWhisper vs Spokenly"), [
    "/en/blog/hyperwhisper-vs-spokenly",
  ]);
});

test("the footer docs and legal links are www, locale-prefixed 200 URLs", async () => {
  const html = await render(FOOTER_PATH, "de");

  assert.match(html, /href="https:\/\/www\.hyperwhisper\.com\/docs"/);
  assert.match(
    html,
    /href="https:\/\/www\.hyperwhisper\.com\/docs\/data-privacy"/,
  );
  assert.doesNotMatch(html, /href="https:\/\/hyperwhisper\.com\//);
  assert.match(html, /href="\/de\/legal\/privacy-policy"/);
  assert.match(html, /href="\/de\/legal\/terms-of-service"/);
  assert.match(html, /href="\/de\/legal\/refund-policy"/);
});

test("homepage platform CTAs are links to /en/download?platform=", async () => {
  const html = await render(PLATFORM_PATH, "en", { cta: "home_hero" });

  assert.match(html, /href="\/en\/download\?platform=mac"/);
  assert.match(html, /href="\/en\/download\?platform=windows"/);
  assert.match(html, /href="\/en\/download\?platform=linux"/);
  assert.doesNotMatch(html, /<button/);
});

test("the navbar Download control is a link to /en/download", async () => {
  const html = await render(NAVBAR_PATH, "en");
  const messages = loadMessages("en") as { navbar: { download: string } };

  assert.ok(
    hrefsOfLabel(html, messages.navbar.download).includes("/en/download"),
  );
});

test("the blog post template has a crawlable download CTA", () => {
  const page = readFileSync(
    join(ROOT, "app/[locale]/blog/[slug]/page.tsx"),
    "utf8",
  );
  const cta = readFileSync(
    join(ROOT, "components/landing/BlogDownloadCta.tsx"),
    "utf8",
  );
  assert.match(page, /BlogDownloadCta/);
  // Blog posts are English-only, so the href is the absolute /en/download.
  assert.match(cta, /href=\{`\/en\$\{downloadPath\(\)\}`\}/);
  assert.match(cta, /Download HyperWhisper/);
  assert.match(cta, /cta="blog_post"/);
  // A plain click opens the email-capture modal, like every other CTA.
  assert.match(cta, /onOpen=\{openModal\}/);
});
