// @ts-check
import createNextIntlPlugin from 'next-intl/plugin';

/**
 * Run `build` or `dev` with `SKIP_ENV_VALIDATION` to skip env validation.
 * This is especially useful for Docker builds.
 */
!process.env.SKIP_ENV_VALIDATION && (await import("./src/env/server.mjs"));

const withNextIntl = createNextIntlPlugin();

/** @type {import('next').NextConfig} */
const nextConfig = {
    eslint: {
        // Disable ESLint during production builds
        // ESLint will still run during development (npm run lint)
        ignoreDuringBuilds: true,
    },
    // Webpack configuration
    webpack: (config) => {
        // Allow importing .txt files as raw strings (asset/source)
        config.module.rules.push({
            test: /\.txt$/i,
            type: 'asset/source',
        });
        return config;
    },
};

export default withNextIntl(nextConfig);