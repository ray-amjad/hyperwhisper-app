// @ts-check
import { fileURLToPath } from 'node:url';

import createNextIntlPlugin from 'next-intl/plugin';

import { docsMarkdownRewrites, docsRedirects, mdxRules } from './config/docs.mjs';
import { blogLinkRedirects, blogPostRedirects } from './config/redirects.mjs';
import { securityHeaderRoutes } from './config/security-headers.mjs';

/**
 * Run `build` or `dev` with `SKIP_ENV_VALIDATION` to skip env validation.
 * cf:build sets it: runtime secrets (Infisical -> the Worker) do not
 * exist at build time, and src/env/server.mjs validates again at runtime.
 */
!process.env.SKIP_ENV_VALIDATION && (await import("./src/env/server.mjs"));

const withNextIntl = createNextIntlPlugin();

/** @type {import('next').NextConfig} */
const nextConfig = {
    // Next.js 16 removed `eslint` (and lint entirely) from the build pipeline
    // (`next lint` is gone); this config key is no longer recognized. Prod
    // builds already never ran lint here, so there's no behavior change - the
    // `lint` npm script calls `eslint` directly instead of `next lint`.
    // Turbopack is the default bundler for `next dev`/`next build` as of Next.js 16.
    turbopack: {
        // This app lives in a monorepo alongside a sibling top-level
        // pnpm-lock.yaml; pin the workspace root to this directory so
        // Turbopack doesn't have to guess (and warn) about it.
        root: fileURLToPath(new URL('.', import.meta.url)),
        // The docs at /docs: .mdx compiles to a component, and `?raw` imports
        // of .mdx and .yaml are plain strings (config/docs.mjs).
        rules: mdxRules,
    },
    // #842: anti-framing and the 4 simple security headers on every route.
    async headers() {
        return securityHeaderRoutes;
    },
    // #1377: 3 URL families that blog posts link to and that 404 otherwise,
    // plus English blog slugs that 308 onto a canonical post.
    async redirects() {
        return [...blogLinkRedirects, ...blogPostRedirects, ...docsRedirects];
    },
    // The Markdown twin of each docs page (/docs/models.md). Every rewrite
    // here is to a path on this site: the docs used to be an external rewrite
    // to Mintlify, which forwarded visitors' cookies to it (#2636), and
    // tests/docs-routing.test.ts keeps any external rewrite out.
    async rewrites() {
        return { beforeFiles: docsMarkdownRewrites, afterFiles: [], fallback: [] };
    },
    // The one next/image (DownloadModal) is a static PNG. Optimisation on
    // Workers needs a billed Cloudflare Images binding, so it is off.
    images: { unoptimized: true },
    // pg loads pg-cloudflare (its Workers socket) only under the `workerd`
    // export condition, so Next's node-condition file tracing copies just its
    // empty stub and the OpenNext bundle fails to resolve it. Ship it whole.
    outputFileTracingIncludes: {
        '/*': ['./node_modules/pg-cloudflare/**/*'],
    },
    // Sentry release: nextjs-deploy.yml sets the commit at build time only.
    ...(process.env.SENTRY_RELEASE
        ? { env: { SENTRY_RELEASE: process.env.SENTRY_RELEASE } }
        : {}),
    experimental: {
        // app/global-not-found.tsx serves unmatched URLs (/de/zzz) on their own
        // route, so it can read the request locale. As app/not-found.tsx it ran
        // on every page and its headers() read made every route dynamic (#843).
        globalNotFound: true,
    },
};

export default withNextIntl(nextConfig);