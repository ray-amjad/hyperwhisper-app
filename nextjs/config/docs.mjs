// @ts-check
import { fileURLToPath } from "node:url";

/**
 * Build and routing config for the docs at /docs (app/docs). They used to be
 * Mintlify, proxied through an external rewrite; they are now pages of this
 * app, rendered at build time from src/content/docs.
 */

/**
 * Turbopack rules. A `.mdx` import compiles to a React component with
 * @next/mdx's loader (it resolves plugin names, since Turbopack options must
 * be plain data). A `?raw` import of a `.mdx` or `.yaml` is the file as a
 * string: lib/docs/content.ts reads the frontmatter, the Markdown twins and
 * openapi.yaml that way, so the Worker never needs a filesystem.
 *
 * Plugins: frontmatter is stripped (lib/docs/frontmatter.ts reads it), GFM
 * for tables, heading ids for #anchors (the github-slugger ids Mintlify used),
 * and Shiki highlighting at build time, so no highlighter ships to the client.
 */
/** @type {NonNullable<NonNullable<import('next').NextConfig['turbopack']>['rules']>} */
export const mdxRules = {
  "*.mdx": [
    { condition: { query: "?raw" }, type: "raw" },
    {
      condition: { not: { query: "?raw" } },
      loaders: [
        {
          loader: fileURLToPath(
            new URL("../node_modules/@next/mdx/mdx-js-loader.js", import.meta.url),
          ),
          options: {
            remarkPlugins: ["remark-frontmatter", "remark-gfm"],
            rehypePlugins: [
              "rehype-slug",
              [
                "@shikijs/rehype",
                {
                  theme: "github-dark-default",
                  defaultLanguage: "text",
                  fallbackLanguage: "text",
                },
              ],
            ],
          },
        },
      ],
      as: "*.tsx",
    },
  ],
  "*.yaml": { condition: { query: "?raw" }, type: "raw" },
};

/**
 * Old Mintlify URLs that moved (docs.json `redirects`). Permanent, so search
 * engines carry the old URL's ranking over. tests/docs-old-urls.test.ts checks
 * every one lands on a page that exists.
 */
/** @type {{ source: string; destination: string; statusCode: 301 }[]} */
export const docsRedirects = [
  { source: "/docs/model-management", destination: "/docs/models", statusCode: 301 },
];

/**
 * `/docs/<slug>.md` -> the Markdown twin route (app/docs-md). Mintlify served
 * these, and agents fetch them. A route cannot end a segment in `.md` next to
 * the page that owns the rest of it, so the twin lives under its own prefix.
 * Dotted paths skip the proxy (proxy.ts matcher), so next-intl never sees
 * them.
 */
export const docsMarkdownRewrites = [
  { source: "/docs.md", destination: "/docs-md" },
  { source: "/docs/:path*.md", destination: "/docs-md/:path*" },
];
