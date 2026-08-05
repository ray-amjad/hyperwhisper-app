// @ts-check
import { fileURLToPath } from 'node:url';

import createNextIntlPlugin from 'next-intl/plugin';

/**
 * Run `build` or `dev` with `SKIP_ENV_VALIDATION` to skip env validation.
 * This is especially useful for Docker builds.
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
    // This replaces the old `webpack((config) => { config.module.rules.push(...) })`
    // rule that made .txt imports resolve to their raw string contents
    // (webpack's `asset/source` type). `raw-loader` + `as: '*.js'` is the
    // Turbopack equivalent (per the Next.js docs "Configuring webpack loaders"
    // example): the loader wraps the file's raw text as a JS module with a
    // default export of the string, then `as: '*.js'` tells Turbopack to treat
    // that loader output as JavaScript.
    turbopack: {
        // This app lives in a monorepo alongside a sibling top-level
        // pnpm-lock.yaml; pin the workspace root to this directory so
        // Turbopack doesn't have to guess (and warn) about it.
        root: fileURLToPath(new URL('.', import.meta.url)),
        rules: {
            '*.txt': {
                loaders: ['raw-loader'],
                as: '*.js',
            },
        },
    },
};

export default withNextIntl(nextConfig);