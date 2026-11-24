/**
 * The docs content (src/content/docs) against the page tree (lib/docs/nav.ts),
 * the OpenAPI spec and the MDX components, all offline. This replaces the
 * lychee gate that ran over mintlify-help: every internal link must reach a
 * docs page, a docs redirect or a file in public/, and every #anchor a
 * heading on the target page.
 */
import assert from "node:assert/strict";
import { existsSync, readdirSync, readFileSync, statSync } from "node:fs";
import { join, relative } from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";

import GithubSlugger from "github-slugger";
import YAML from "yaml";

import { docsRedirects } from "../config/docs.mjs";
import { splitFrontmatter } from "../lib/docs/frontmatter";
import { DOCS_ENTRIES, DOCS_NAV, findDocByPath } from "../lib/docs/nav";
import { type OpenApiSpec, listOperations } from "../lib/docs/openapi";

const CONTENT = fileURLToPath(new URL("../src/content/docs/", import.meta.url));
const PUBLIC = fileURLToPath(new URL("../public/", import.meta.url));

function walk(dir: string): string[] {
  return readdirSync(dir).flatMap((name) => {
    const path = join(dir, name);

    return statSync(path).isDirectory() ? walk(path) : [path];
  });
}

const files = walk(CONTENT).filter((f) => f.endsWith(".mdx"));
const slugOf = (file: string) => relative(CONTENT, file).replace(/\.mdx$/, "");
const sources = new Map(files.map((f) => [slugOf(f), readFileSync(f, "utf8")]));
const pages = DOCS_ENTRIES.filter((e) => e.kind === "page");
const spec = YAML.parse(
  readFileSync(join(PUBLIC, "docs/api-reference/local-api/openapi.yaml"), "utf8"),
) as OpenApiSpec;

/** Markdown with fenced code blocks and inline code removed. */
function prose(body: string): string {
  return body.replace(/^\s*(```|~~~)[\s\S]*?^\s*\1[^\n]*$/gm, "").replace(/`[^`\n]*`/g, "");
}

test("every .mdx file is a page in the nav, and every nav page has a file", () => {
  assert.deepEqual(
    Array.from(sources.keys()).sort(),
    pages.map((p) => p.slug).sort(),
  );
});

test("nav hrefs are unique", () => {
  const hrefs = DOCS_ENTRIES.map((e) => e.href);

  assert.equal(new Set(hrefs).size, hrefs.length);
});

test("every page has a title and description, and the nav uses its title", () => {
  for (const page of pages) {
    const { data } = splitFrontmatter(sources.get(page.slug) ?? "", page.slug);

    assert.equal(page.title, data.title, page.slug);
    assert.ok(data.description.length >= 20, `${page.slug}: description`);
  }
});

test("the nav has the old docs.json groups, nested ones included", () => {
  assert.deepEqual(
    DOCS_NAV.map((g) => g.title),
    [
      "Getting Started",
      "Plans & Billing",
      "Transcription & Models",
      "The Interface",
      "Features",
      "Settings",
      "Privacy & Security",
      "Developer & API",
      "Troubleshooting",
      "Windows",
    ],
  );
  const features = DOCS_NAV.find((g) => g.title === "Features");
  const nested = features?.items.filter((n) => n.kind === "group").map((n) => n.title);

  assert.deepEqual(nested, ["Audio Input", "Sounds & Media", "Vocabulary"]);
});

test("the endpoint pages are the spec's operations, in order", () => {
  const endpoints = DOCS_ENTRIES.filter((e) => e.kind === "endpoint");
  const operations = listOperations(spec);

  assert.deepEqual(
    endpoints.map((e) => e.kind === "endpoint" && [e.slug, e.method, e.path, e.title]),
    operations.map((op) => [op.slug, op.method, op.path, op.summary]),
  );
  for (const e of endpoints) {
    assert.deepEqual(e.groups, ["Developer & API", "Local API Reference"], e.slug);
  }
});

test("every MDX component a page uses exists", () => {
  const source = readFileSync(
    new URL("../components/docs/mdx-components.tsx", import.meta.url),
    "utf8",
  );
  const map = source.slice(source.indexOf("export const docsMdxComponents"));
  const known = new Set(
    Array.from(map.matchAll(/^\s{2}([A-Z][A-Za-z]*)[:,]/gm), (m) => m[1]),
  );
  const icons = new Set(
    Array.from(
      source
        .slice(0, source.indexOf("export const CARD_ICON_NAMES"))
        .matchAll(/^\s{2}"?([a-z-]+)"?: [A-Z]/gm),
      (m) => m[1],
    ),
  );

  assert.ok(known.has("Note") && known.has("Tabs"), "parsed no components");
  for (const [slug, raw] of Array.from(sources)) {
    const body = prose(raw);

    for (const [, tag] of Array.from(body.matchAll(/<([A-Z][A-Za-z]*)/g))) {
      assert.ok(known.has(tag), `${slug}: <${tag}> has no component`);
    }
    for (const [, icon] of Array.from(body.matchAll(/<Card[^>]*\sicon="([^"]+)"/g))) {
      assert.ok(icons.has(icon), `${slug}: no icon "${icon}"`);
    }
  }
});

function headingIds(raw: string): Set<string> {
  const slugger = new GithubSlugger();
  const ids = new Set<string>();

  for (const [, text] of Array.from(prose(raw).matchAll(/^#{1,6}\s+(.+?)\s*#*$/gm))) {
    // rehype-slug slugs the heading's text content, without Markdown marks.
    ids.add(slugger.slug(text.replace(/[*_`]/g, "").replace(/\[([^\]]*)\]\([^)]*\)/g, "$1")));
  }

  return ids;
}

test("every internal link and image resolves", () => {
  const redirects = new Map(docsRedirects.map((r) => [r.source, r.destination]));
  let checked = 0;

  for (const [slug, raw] of Array.from(sources)) {
    const body = prose(raw);
    const links = [
      ...Array.from(body.matchAll(/\]\((\/[^)\s]*)/g), (m) => m[1]),
      ...Array.from(body.matchAll(/(?:href|src)="(\/[^"]*)"/g), (m) => m[1]),
    ];

    for (const link of links) {
      checked += 1;
      const [path, anchor] = link.split("#");
      const target = findDocByPath(redirects.get(path) ?? path);

      assert.ok(path.startsWith("/docs"), `${slug}: ${link} is not under /docs`);
      if (target) {
        if (anchor && target.kind === "page") {
          assert.ok(
            headingIds(sources.get(target.slug) ?? "").has(anchor),
            `${slug}: ${link} has no #${anchor} heading`,
          );
        }
        continue;
      }
      assert.ok(existsSync(join(PUBLIC, path)), `${slug}: ${link} does not resolve`);
    }
  }
  assert.ok(checked > 50, `only ${checked} links found`);
});
