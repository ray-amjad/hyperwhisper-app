/**
 * #849: a visitor who dismissed the open-source banner saw it again on every
 * page load until LayoutWrapper's effect read localStorage, then the header
 * jumped 48px. HtmlDocument now ships a blocking script that marks <html>
 * before first paint, and globals.css hides the banner from that mark.
 *
 * Renders the REAL HtmlDocument, takes the inline script out of its <head>, and
 * runs it against a fake document and localStorage: dismissed marks <html>,
 * anything else (and a localStorage that throws) leaves it alone. Then checks
 * the CSS rule hides the element LayoutWrapper renders the banner in.
 */
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { runInNewContext } from "node:vm";

import { createElement, type ComponentType } from "react";
import { renderToStaticMarkup } from "react-dom/server";

const ROOT = fileURLToPath(new URL("..", import.meta.url));
// Variable specifier: a literal `.tsx` specifier is a TS5097 error here.
const HTML_DOCUMENT_PATH = "../components/layout/HtmlDocument.tsx";

async function headScript(): Promise<string> {
  const mod = (await import(HTML_DOCUMENT_PATH)) as {
    default: ComponentType<{ locale: string; children: unknown }>;
  };
  const html = renderToStaticMarkup(
    createElement(mod.default, { locale: "en", children: null }),
  );
  const head = /<head>([\s\S]*?)<\/head>/.exec(html)?.[1] ?? "";
  const script = /<script>([\s\S]*?)<\/script>/.exec(head)?.[1];

  assert.ok(script, "HtmlDocument renders an inline script in <head>");

  return script;
}

function run(script: string, getItem: (key: string) => string | null) {
  const dataset: Record<string, string> = {};

  runInNewContext(script, {
    localStorage: { getItem },
    document: { documentElement: { dataset } },
  });

  return dataset;
}

test("a stored dismissal marks <html> before first paint", async () => {
  const script = await headScript();

  assert.deepEqual(
    run(script, (k) => (k === "os-banner-dismissed" ? "1" : null)),
    { osBanner: "dismissed" },
  );
});

test("no dismissal, or an unreadable localStorage, leaves <html> unmarked", async () => {
  const script = await headScript();

  assert.deepEqual(run(script, () => null), {});
  assert.deepEqual(run(script, () => "0"), {});
  assert.deepEqual(
    run(script, () => {
      throw new Error("SecurityError: storage disabled");
    }),
    {},
  );
});

test("the mark hides the element LayoutWrapper renders the banner in", () => {
  const css = readFileSync(`${ROOT}/styles/globals.css`, "utf8");
  const wrapper = readFileSync(
    `${ROOT}/components/layout/LayoutWrapper.tsx`,
    "utf8",
  );

  assert.match(
    css,
    /html\[data-os-banner="dismissed"\]\s+\[data-os-banner-root\]\s*\{\s*display:\s*none;/,
  );
  assert.match(wrapper, /data-os-banner-root/);
});
