/**
 * #759 — `preload=true` on a Bunny Stream embed downloads ~640 KB of HLS
 * segments and starts a never-ending RUM edge probe on every page view, before
 * any click. Bunny's own embed snippet defaults to `preload=true`, so a re-paste
 * restores it silently.
 *
 * Two guards:
 * 1. The home demo: the REAL `VideoDemo` is rendered with react-dom/server and
 *    the SERVED iframe `src` is parsed, so source spelling (`&amp;`, a `{"…"}`
 *    expression) cannot turn it red and a commented-out iframe cannot keep it
 *    green.
 * 2. Every other embed: `components/` and `app/` are walked, comments blanked,
 *    and every `mediadelivery.net/embed/` URL must carry `preload=false`.
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
// A variable specifier: a literal `.tsx` specifier is a TS5097 error here.
const VIDEO_DEMO_PATH = "../components/landing/VideoDemo.tsx";
const EMBED_URL =
  /(?:https?:)?\/\/[\w.-]*mediadelivery\.net\/embed\/[^"'`\s<>{}\\]*/g;
const WHY =
  "preload must be false — true downloads ~640 KB of HLS segments and starts a RUM edge probe before any click (#759)";

const decode = (raw: string) => raw.replace(/&amp;/g, "&");

test("the rendered home demo serves one Bunny embed with preload=false and autoplay=false", async () => {
  const { default: VideoDemo } = (await import(VIDEO_DEMO_PATH)) as {
    default: ComponentType;
  };
  const messages = JSON.parse(
    readFileSync(join(ROOT, "messages/en.json"), "utf8"),
  );
  const html = renderToStaticMarkup(
    createElement(NextIntlClientProvider, {
      locale: "en",
      messages,
      timeZone: "UTC",
      children: createElement(VideoDemo),
    }),
  );

  const srcs = Array.from(
    html.matchAll(/<iframe\b[^>]*?\ssrc="([^"]*)"/g),
    (m) => new URL(decode(m[1])),
  ).filter((url) => url.hostname.endsWith("mediadelivery.net"));

  assert.equal(srcs.length, 1, `expected one rendered Bunny iframe, got ${srcs.length}`);
  assert.equal(srcs[0].searchParams.get("preload"), "false", WHY);
  assert.equal(srcs[0].searchParams.get("autoplay"), "false");
});

function sourceFiles(dir: string): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const path = join(dir, entry.name);
    if (entry.isDirectory()) {
      return entry.name === "node_modules" ? [] : sourceFiles(path);
    }
    return /\.tsx?$/.test(entry.name) ? [path] : [];
  });
}

test("every Bunny embed in components/ and app/ pins preload=false", () => {
  const files = ["components", "app"].flatMap((d) => sourceFiles(join(ROOT, d)));
  assert.ok(files.length > 0, "the walk found no .ts/.tsx files");

  const embeds = files.flatMap((file) => {
    // Blank block comments and `//` line comments (not the `//` in `https://`).
    const source = readFileSync(file, "utf8")
      .replace(/\/\*[\s\S]*?\*\//g, " ")
      .replace(/(^|[^:])\/\/.*$/gm, "$1");
    return Array.from(source.matchAll(EMBED_URL), (m) => ({
      file: file.slice(ROOT.length),
      url: new URL(decode(m[0]), "https://x"),
    }));
  });

  assert.ok(embeds.length > 0, "no mediadelivery.net embed found — the scan is blind");
  for (const { file, url } of embeds) {
    assert.equal(url.searchParams.get("preload"), "false", `${file}: ${url.href} — ${WHY}`);
  }
});
