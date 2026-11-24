/**
 * The docs are pages of this app now. #2636: the old /docs rewrites to
 * Mintlify were external, so a signed-in visitor's session cookie went to a
 * third party with every docs request. No rewrite or redirect may point off
 * this site again.
 */
import assert from "node:assert/strict";
import { existsSync } from "node:fs";
import test from "node:test";

// @ts-expect-error -- Next ships this compiled copy with no .d.ts.
import * as compiledPathToRegexp from "next/dist/compiled/path-to-regexp";

import { docsMarkdownRewrites } from "../config/docs.mjs";

const { pathToRegexp, compile } = compiledPathToRegexp as {
  pathToRegexp: (path: string, keys?: { name: string }[]) => RegExp;
  compile: (path: string) => (params: object) => string;
};

process.env.SKIP_ENV_VALIDATION = "1";

type Rule = { source: string; destination: string };

async function loadConfig() {
  // A computed specifier: tsc would otherwise type-check next.config.mjs,
  // whose top-level await the tsconfig target does not allow.
  const configUrl = new URL("../next.config.mjs", import.meta.url).href;
  const { default: config } = (await import(configUrl)) as {
    default: import("next").NextConfig;
  };
  const rewrites = await config.rewrites?.();
  const list = Array.isArray(rewrites)
    ? rewrites
    : [
        ...(rewrites?.beforeFiles ?? []),
        ...(rewrites?.afterFiles ?? []),
        ...(rewrites?.fallback ?? []),
      ];
  const redirects = (await config.redirects?.()) ?? [];

  return { rewrites: list as Rule[], redirects: redirects as Rule[] };
}

test("#2636: no rewrite sends a request to another origin", async () => {
  const { rewrites } = await loadConfig();

  for (const rule of rewrites) {
    assert.ok(
      rule.destination.startsWith("/"),
      `${rule.source} -> ${rule.destination} leaves the site`,
    );
  }
});

test("no redirect or rewrite names Mintlify", async () => {
  const { rewrites, redirects } = await loadConfig();

  for (const rule of [...rewrites, ...redirects]) {
    assert.doesNotMatch(rule.destination, /mintlify/i, rule.source);
  }
  assert.equal(
    existsSync(new URL("../config/mintlify-rewrites.mjs", import.meta.url)),
    false,
  );
});

function rewrite(pathname: string): string | undefined {
  for (const { source, destination } of docsMarkdownRewrites) {
    const keys: { name: string }[] = [];
    const match = pathToRegexp(source, keys).exec(pathname);

    if (!match) continue;
    const params: Record<string, string | string[]> = {};

    keys.forEach((key, i) => {
      if (match[i + 1] !== undefined) params[key.name] = match[i + 1].split("/");
    });

    return compile(destination)(params);
  }

  return undefined;
}

test("a page's .md URL reaches its Markdown twin", () => {
  assert.equal(rewrite("/docs.md"), "/docs-md");
  assert.equal(rewrite("/docs/models.md"), "/docs-md/models");
  assert.equal(rewrite("/docs/windows/faq.md"), "/docs-md/windows/faq");
  assert.equal(
    rewrite("/docs/api-reference/local-api/overview.md"),
    "/docs-md/api-reference/local-api/overview",
  );
});

test("control: pages and other files are not rewritten", () => {
  for (const path of [
    "/docs",
    "/docs/models",
    "/docs/llms.txt",
    "/docs/sitemap.xml",
    "/docs/api-reference/local-api/openapi.yaml",
    "/en/docs.md",
  ]) {
    assert.equal(rewrite(path), undefined, path);
  }
});
