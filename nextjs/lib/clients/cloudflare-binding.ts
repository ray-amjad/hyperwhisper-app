import { getCloudflareContext } from "@opennextjs/cloudflare";

/**
 * One binding off the Worker's `env` (declared in `wrangler.jsonc`), or
 * `undefined` when there is no Cloudflare request context: node tests,
 * scripts, a `next build` prerender. Callers decide what a missing binding
 * means; the rate limiters fail open, email reports a send failure.
 */
export function cloudflareBinding<T>(name: string): T | undefined {
  try {
    const env = getCloudflareContext().env as unknown as Record<string, unknown>;

    return env[name] as T | undefined;
  } catch {
    return undefined;
  }
}
