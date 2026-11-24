import "server-only";

import type { ComponentType } from "react";

import YAML from "yaml";

import { splitFrontmatter, type DocFrontmatter } from "./frontmatter";
import { DOCS_ENTRIES, type DocLocated, type DocPage } from "./nav";
import { type OpenApiSpec, listOperations, plainText } from "./openapi";

import { SITE_URL } from "@/lib/site-metadata";

// The docs sources, bundled at build time by Turbopack's import.meta.glob, so
// the Worker (which has no filesystem) never reads a file. Each .mdx is
// imported twice: compiled to a component by the MDX rule in next.config.mjs,
// and as raw text (`?raw`) for the frontmatter, the .md twins and llms.txt.

type MdxModule = {
  default: ComponentType<{ components?: Record<string, unknown> }>;
};

const MODULES = import.meta.glob<MdxModule>("/src/content/docs/**/*.mdx");
const RAW_MODULES = import.meta.glob<unknown>("/src/content/docs/**/*.mdx", {
  query: "?raw",
  eager: true,
});
const SOURCES: Record<string, string> = Object.fromEntries(
  Object.entries(RAW_MODULES).map(([k, m]) => [k, rawText(m)]),
);
const SPEC_SOURCES = import.meta.glob<unknown>(
  "/public/docs/api-reference/local-api/openapi.yaml",
  { query: "?raw", eager: true },
);

function rawText(mod: unknown): string {
  if (typeof mod === "string") return mod;
  const value = (mod as { default?: unknown } | null)?.default;

  if (typeof value === "string") return value;
  throw new Error(`docs: a ?raw import is not a string (${typeof mod})`);
}

function keyFor(slug: string, table: Record<string, unknown>): string {
  const suffix = `/src/content/docs/${slug}.mdx`;
  const key = Object.keys(table).find((k) => k.endsWith(suffix));

  if (!key) throw new Error(`docs: no src/content/docs/${slug}.mdx`);

  return key;
}

export type DocSource = DocFrontmatter & {
  entry: DocLocated;
  canonical: string;
  /** Markdown (MDX) with the frontmatter removed. */
  body: string;
};

let spec: OpenApiSpec | undefined;

export function loadSpec(): OpenApiSpec {
  if (!spec) {
    const raw = rawText(Object.values(SPEC_SOURCES)[0]);

    if (!raw) throw new Error("docs: openapi.yaml was not bundled");
    spec = YAML.parse(raw) as OpenApiSpec;
  }

  return spec;
}

export function loadPageComponent(page: DocPage): Promise<MdxModule> {
  return MODULES[keyFor(page.slug, MODULES)]();
}

/** Title, description and Markdown of any docs entry, page or endpoint. */
export function loadDoc(entry: DocLocated): DocSource {
  const canonical = `${SITE_URL}${entry.href}`;

  if (entry.kind === "page") {
    const raw = SOURCES[keyFor(entry.slug, SOURCES)];
    const { data, body } = splitFrontmatter(raw, entry.slug);

    return { ...data, entry, canonical, body };
  }

  const operation = listOperations(loadSpec()).find(
    (op) => op.slug === entry.slug,
  );

  if (!operation) throw new Error(`docs: no operation for ${entry.slug}`);

  return {
    title: entry.title,
    description: endpointDescription(
      entry.method,
      entry.path,
      operation.description,
    ),
    entry,
    canonical,
    body: endpointMarkdown(
      operation.method,
      operation.path,
      operation.description,
    ),
  };
}

export function endpointDescription(
  method: string,
  path: string,
  description?: string,
): string {
  return (
    plainText(description) ??
    `${method} ${path} on the HyperWhisper Local API, the HTTP server the app runs on 127.0.0.1.`
  );
}

function endpointMarkdown(method: string, path: string, description?: string) {
  return [
    `\`${method} ${path}\``,
    description?.trim(),
    `Full schema: ${SITE_URL}/docs/api-reference/local-api/openapi.yaml`,
  ]
    .filter(Boolean)
    .join("\n\n")
    .concat("\n");
}

export function loadAllDocs(): DocSource[] {
  return DOCS_ENTRIES.map((entry) => loadDoc(entry));
}
