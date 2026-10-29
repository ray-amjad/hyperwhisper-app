#!/usr/bin/env node
// @ts-check
/**
 * `pnpm run cf:build`: the Cloudflare Workers build (DEPLOY-CLOUDFLARE.md).
 *
 * The build has no runtime secrets: they are synced from Infisical to the
 * Worker and exist only at request time. But `next build` imports every route
 * to collect page data, and the Stripe SDK (and others) throw at
 * import without a key. So any server variable the build environment does not
 * set gets an obviously fake placeholder here, for the build process only.
 *
 * The placeholders never reach production: Next inlines only NEXT_PUBLIC_*
 * into the bundle, and on Workers process.env is filled from the Worker's own
 * bindings on every request (src/env/server.mjs then validates the real
 * values). tests/cf-build-placeholders.test.ts and the post-build grep in this
 * script hold that line.
 *
 * It also drops the prerendered ISR entries of the pages that read the
 * database (DB_RENDERED_ROUTES) from .open-next/cache, so
 * `opennextjs-cloudflare deploy` does not upload them to R2. The build has no
 * database, so those renders are empty (0 latency samples, only the code blog
 * posts, a sitemap without the stored posts), and uploaded under the new build
 * id they would count as fresh for a full revalidate window after every
 * deploy (`/sitemap.xml` has no window: it would stay short until the next
 * revalidatePath). With no entry, the first request after a deploy renders
 * the page through Hyperdrive and writes the real render to R2.
 */
import { spawnSync } from "node:child_process";
import { existsSync, readdirSync, readFileSync, rmSync, statSync } from "node:fs";
import { join, relative, sep } from "node:path";
import { fileURLToPath } from "node:url";

export const PLACEHOLDER_MARK = "cf-build-placeholder";

/** Variables some module needs at import time; never real values. */
export const BUILD_PLACEHOLDERS = Object.freeze({
  // Not a secret: the real public origin, so nothing rendered at build can
  // point anywhere else. Override in nextjs-deploy.yml if needed.
  BETTER_AUTH_URL: "https://www.hyperwhisper.com",
  STRIPE_SECRET_KEY: `sk_${PLACEHOLDER_MARK}`,
  STRIPE_WEBHOOK_SECRET: `whsec_${PLACEHOLDER_MARK}`,
  BETTER_AUTH_SECRET: `${PLACEHOLDER_MARK}-0000000000000000000000000000`,
  CRON_SECRET: PLACEHOLDER_MARK,
  OUTRANK_WEBHOOK_TOKEN: PLACEHOLDER_MARK,
});

/**
 * Cache keys (path after `/$/` in .open-next/cache, without `.cache`) of the
 * prerendered pages that read the database. Each must be found in every
 * build: a miss means OpenNext changed its cache layout and nothing would be
 * dropped. `en/blog/<slug>` (the code posts; a stored post can replace one)
 * is dropped too but not required.
 */
export const DB_RENDERED_ROUTES = Object.freeze([
  "en/blog",
  "en/latency",
  "en/choosing-a-model",
  "sitemap.xml",
]);
const DB_RENDERED_PREFIXES = Object.freeze(["en/blog/"]);

/**
 * @param {string} dir
 * @returns {string[]}
 */
function listFiles(dir) {
  if (!existsSync(dir)) return [];
  return readdirSync(dir, { withFileTypes: true }).flatMap((entry) =>
    entry.isDirectory()
      ? listFiles(join(dir, entry.name))
      : [join(dir, entry.name)],
  );
}

/**
 * The route a .open-next/cache file holds, e.g. `en/latency`, or null.
 * @param {string} cacheDir
 * @param {string} file
 * @returns {string | null}
 */
export function cacheFileRoute(cacheDir, file) {
  const rel = relative(cacheDir, file).split(sep).join("/");
  const match = /\/\$\/(.+)\.cache$/.exec(rel);
  return match ? match[1] : null;
}

/**
 * Removes the database-backed prerendered entries from the ISR cache output.
 * Throws if a DB_RENDERED_ROUTES entry is missing.
 * @param {string} cacheDir
 * @returns {string[]} the removed routes
 */
export function dropDbRenderedCache(cacheDir) {
  /** @type {string[]} */
  const removed = [];
  for (const file of listFiles(cacheDir)) {
    const route = cacheFileRoute(cacheDir, file);
    if (route === null) continue;
    if (
      DB_RENDERED_ROUTES.includes(route) ||
      DB_RENDERED_PREFIXES.some((prefix) => route.startsWith(prefix))
    ) {
      rmSync(file);
      removed.push(route);
    }
  }
  const missing = DB_RENDERED_ROUTES.filter((route) => !removed.includes(route));
  if (missing.length > 0) {
    throw new Error(
      `no prerendered cache entry for ${missing.join(", ")} in ${cacheDir}; ` +
        "has the OpenNext cache layout changed? (scripts/cf-build.mjs)",
    );
  }
  return removed;
}

// Files under `dir` (recursively) that contain `needle`.
/**
 * @param {string} dir
 * @param {string} needle
 * @returns {string[]}
 */
function filesContaining(dir, needle) {
  if (!existsSync(dir)) return [];
  const hits = [];
  for (const name of readdirSync(dir)) {
    const path = join(dir, name);
    const stat = statSync(path);
    if (stat.isDirectory()) {
      if (name === "cache" || name === "node_modules") continue;
      hits.push(...filesContaining(path, needle));
    } else if (stat.size < 20_000_000 && readFileSync(path, "latin1").includes(needle)) {
      hits.push(path);
    }
  }
  return hits;
}

function main() {
  /** @type {NodeJS.ProcessEnv} */
  const env = { ...process.env, SKIP_ENV_VALIDATION: "1" };
  for (const [name, value] of Object.entries(BUILD_PLACEHOLDERS)) {
    if (!env[name]) (/** @type {Record<string, string>} */ (env))[name] = value;
  }

  const result = spawnSync("opennextjs-cloudflare", ["build", ...process.argv.slice(2)], {
    env,
    stdio: "inherit",
    shell: process.platform === "win32",
  });
  if (result.status !== 0) process.exit(result.status ?? 1);

  // A placeholder in the output would mean one leaked into the Worker.
  const leaked = filesContaining(".open-next", PLACEHOLDER_MARK);
  if (leaked.length > 0) {
    console.error(`cf-build: a build placeholder reached the output:\n  ${leaked.join("\n  ")}`);
    process.exit(1);
  }

  try {
    const removed = dropDbRenderedCache(join(".open-next", "cache"));
    console.log(`cf-build: dropped build-time renders of ${removed.join(", ")}; the first request renders them from the database`);
  } catch (error) {
    console.error(`cf-build: ${error instanceof Error ? error.message : error}`);
    process.exit(1);
  }
}

if (process.argv[1] === fileURLToPath(import.meta.url)) main();
