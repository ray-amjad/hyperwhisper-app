import type { NextRequest } from "next/server";

import { timingSafeEqualSecret } from "@/lib/security/timing-safe-secret";
import { env } from "@/src/env/server.mjs";

/**
 * Reads `Authorization: Bearer <secret>` the way the rest of this app does (see
 * isBearerAuthorized in webhooks/add-blog-post/route.ts). The scheme is
 * required and the value is trimmed — a bare secret with no scheme is not a
 * bearer token, and a stray space after "Bearer" is not a different token.
 */
function bearerToken(request: NextRequest): string | null {
  const header = request.headers.get("authorization");
  if (!header || !header.startsWith("Bearer ")) return null;
  return header.slice("Bearer ".length).trim();
}

/**
 * The gate every daily Vercel cron route under `app/api/internal/` shares.
 *
 * Accepts the internal secret either as `x-internal-secret` (manual runs) or as
 * `Authorization: Bearer …` (Vercel cron, which cannot set custom headers).
 * Vercel signs its cron requests with CRON_SECRET, so that is accepted on the
 * bearer too — both come from src/env/server.mjs, so an unset one is a named
 * hole in the environment rather than an undefined that quietly never matches.
 *
 * A refusal is logged under `label`. The failure mode this guards against is
 * silent: an unset CRON_SECRET makes every nightly run 401, rows outlive the
 * retention the public privacy page states as a fact, and nothing anywhere
 * says so. Naming which credentials even exist (never their values) is what
 * makes that diagnosable from a log line.
 */
export function isAuthorizedInternalCron(
  request: NextRequest,
  label: string,
): boolean {
  const expected = env.HYPERWHISPER_INTERNAL_SECRET;
  const cronSecret = env.CRON_SECRET;
  const headerSecret = request.headers.get("x-internal-secret");
  const bearer = bearerToken(request);

  const authorized =
    timingSafeEqualSecret(headerSecret, expected) ||
    timingSafeEqualSecret(bearer, expected) ||
    timingSafeEqualSecret(bearer, cronSecret);

  if (!authorized) {
    console.error(`Unauthorized ${label} request:`, {
      hasInternalSecretHeader: headerSecret !== null,
      hasBearer: bearer !== null,
      internalSecretConfigured: Boolean(expected),
      cronSecretConfigured: Boolean(cronSecret),
    });
  }
  return authorized;
}
