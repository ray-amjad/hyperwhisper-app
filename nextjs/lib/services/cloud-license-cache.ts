/**
 * Tell HyperWhisper Cloud that an Account Key string is dead.
 *
 * The cloud caches each key's licence verdict for an hour (`license:<key>` in
 * ITS OWN Upstash database — `lib/clients/redis.ts` explains why the site's
 * database is a different one). After a rotation the site already refuses the
 * old string, but the cloud would keep admitting it until that hour ran out.
 * `POST /internal/license-cache/invalidate` on each cloud app overwrites the
 * entry with a stamped refusal (`hyperwhisper-cloud/src/routes/
 * internal-license-cache.ts`), gated by the shared HYPERWHISPER_INTERNAL_SECRET
 * the cloud already uses to call `/api/internal/latency` here.
 *
 * Best effort and bounded: it runs after the rotation has committed, so a
 * failure here cannot bring the old key back; it only leaves the cloud's cached
 * verdict to expire on its own (at most `LICENSE_CACHE_TTL_SECONDS`, 1 hour).
 * It never throws, and never logs the key.
 */
import { env } from "@/src/env/server.mjs";

/**
 * The cloud apps that validate keys against the PRODUCTION site. Staging is
 * listed with production because it calls the production licence API too, so
 * it caches production keys.
 *
 * They are the default ONLY on the production deployment (`DEPLOY_ENV` =
 * `production`, a plain var in wrangler.jsonc). A preview or local site holds its own keys, and must never
 * tell the production cloud to refuse a string. Anywhere else nothing is
 * purged unless `HYPERWHISPER_CLOUD_URLS` (comma-separated base URLs) names
 * the cloud apps; an empty value turns the purge off everywhere.
 */
export const DEFAULT_CLOUD_URLS = [
  "https://transcribe-prod-v2.hyperwhisper.com",
  "https://transcribe-staging-v2.hyperwhisper.com",
] as const;

export const CLOUD_PURGE_PATH = "/internal/license-cache/invalidate";

/** Per cloud app. The dashboard waits on this, so it is short. */
export const CLOUD_PURGE_TIMEOUT_MS = 3_000;

export function cloudPurgeUrls(
  raw: string | undefined,
  deployEnv: string | undefined,
): string[] {
  if (raw === undefined) {
    return deployEnv === "production" ? [...DEFAULT_CLOUD_URLS] : [];
  }

  return raw
    .split(",")
    .map((url) => url.trim().replace(/\/+$/, ""))
    .filter((url) => url.length > 0);
}

export interface CloudPurgeDeps {
  fetch: typeof fetch;
  secret: string | undefined;
  urls: string[];
}

export interface CloudPurgeOutcome {
  /** Cloud apps that confirmed the refusal is stored. */
  purged: number;
  /** Cloud apps that did not; their cached verdict expires on its own. */
  failed: number;
  /** True when no secret or no cloud app is configured, so nothing was sent. */
  skipped: boolean;
}

const defaultDeps = (): CloudPurgeDeps => ({
  fetch: (input, init) => fetch(input, init),
  secret: env.HYPERWHISPER_INTERNAL_SECRET,
  // A plain Worker var (wrangler.jsonc `vars`), not part of the validated env
  // (the Sentry config reads it the same way).
  urls: cloudPurgeUrls(env.HYPERWHISPER_CLOUD_URLS, process.env.DEPLOY_ENV),
});

function hostOf(url: string): string {
  try {
    return new URL(url).host;
  } catch {
    return "invalid-url";
  }
}

export async function purgeCloudLicenseCache(
  previousKey: string,
  deps: CloudPurgeDeps = defaultDeps(),
): Promise<CloudPurgeOutcome> {
  // No cloud app to tell: a preview or local site, or the purge turned off.
  if (deps.urls.length === 0) {
    return { purged: 0, failed: 0, skipped: true };
  }

  if (!deps.secret) {
    console.warn(
      "Cloud licence cache purge skipped: HYPERWHISPER_INTERNAL_SECRET is unset",
    );

    return { purged: 0, failed: 0, skipped: true };
  }

  const body = JSON.stringify({ account_key: previousKey });
  const secret = deps.secret;

  const results = await Promise.allSettled(
    deps.urls.map(async (base) => {
      const response = await deps.fetch(`${base}${CLOUD_PURGE_PATH}`, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "x-internal-secret": secret,
        },
        body,
        signal: AbortSignal.timeout(CLOUD_PURGE_TIMEOUT_MS),
      });

      if (!response.ok) throw new Error(`HTTP ${response.status}`);
    }),
  );

  let purged = 0;
  let failed = 0;

  results.forEach((result, index) => {
    if (result.status === "fulfilled") {
      purged += 1;

      return;
    }
    failed += 1;
    // The reason is our own `HTTP <status>` or a fetch error's class name:
    // a network error's message can quote the request, so it is not printed.
    const reason =
      result.reason instanceof Error &&
      result.reason.message.startsWith("HTTP ")
        ? result.reason.message
        : result.reason instanceof Error
          ? result.reason.name
          : typeof result.reason;

    console.warn(
      `Cloud licence cache purge failed at ${hostOf(deps.urls[index])}: ${reason}`,
    );
  });

  return { purged, failed, skipped: false };
}
