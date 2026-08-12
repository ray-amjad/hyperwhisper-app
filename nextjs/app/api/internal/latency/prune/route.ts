import { NextRequest, NextResponse } from "next/server";
import { lt, sql } from "drizzle-orm";

import { timingSafeEqualSecret } from "@/lib/security/timing-safe-secret";
import { db } from "@/src/db";
import { sttLatencySamples } from "@/src/db/schema/stt-latency-samples";
import { env } from "@/src/env/server.mjs";

export const dynamic = "force-dynamic";
export const maxDuration = 60;

/** Retention promised on the public privacy page. */
const RETENTION_DAYS = 365;
/** Rows per delete. Bounded so one run can never hold a long table lock. */
const BATCH_SIZE = 5_000;
/** Batches per run. The daily cron catches up over a few days if it must. */
const MAX_BATCHES = 20;

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
 * Deletes latency samples past the 1-year retention window.
 *
 * Runs daily from a Vercel cron (see vercel.json). Without this, "we keep these
 * rows for a year" would be a hope rather than a policy — and the privacy page
 * states it as a fact.
 *
 * Accepts the internal secret either as `x-internal-secret` (manual runs) or as
 * `Authorization: Bearer …` (Vercel cron, which cannot set custom headers).
 * Vercel signs its cron requests with CRON_SECRET, so that is accepted on the
 * bearer too — both come from src/env/server.mjs, so an unset one is a named
 * hole in the environment rather than an undefined that quietly never matches.
 */
export async function GET(request: NextRequest) {
  const expected = env.HYPERWHISPER_INTERNAL_SECRET;
  const cronSecret = env.CRON_SECRET;
  const headerSecret = request.headers.get("x-internal-secret");
  const bearer = bearerToken(request);

  const authorized =
    timingSafeEqualSecret(headerSecret, expected) ||
    timingSafeEqualSecret(bearer, expected) ||
    timingSafeEqualSecret(bearer, cronSecret);

  if (!authorized) {
    // The 500 path below logs; this one has to as well. The whole failure mode
    // this guards against is silent: an unset CRON_SECRET makes every nightly
    // run 401, rows accumulate past the 1-year retention the public privacy
    // page states as a fact, and nothing anywhere says so. Naming which
    // credentials even exist is what makes that diagnosable from a log line.
    console.error("Unauthorized latency prune request:", {
      hasInternalSecretHeader: headerSecret !== null,
      hasBearer: bearer !== null,
      internalSecretConfigured: Boolean(expected),
      cronSecretConfigured: Boolean(cronSecret),
    });
    return NextResponse.json({ error: "Unauthorized" }, { status: 401 });
  }

  const cutoff = new Date(Date.now() - RETENTION_DAYS * 24 * 60 * 60 * 1000);

  try {
    let deleted = 0;
    for (let batch = 0; batch < MAX_BATCHES; batch += 1) {
      const result = await db.delete(sttLatencySamples).where(
        sql`${sttLatencySamples.id} in (
          select ${sttLatencySamples.id} from ${sttLatencySamples}
          where ${lt(sttLatencySamples.createdAt, cutoff)}
          limit ${BATCH_SIZE}
        )`,
      );
      const count = result.rowCount ?? 0;
      deleted += count;
      if (count < BATCH_SIZE) break;
    }

    return NextResponse.json({ deleted, cutoff: cutoff.toISOString() });
  } catch (error) {
    console.error("Error pruning latency samples:", error);
    return NextResponse.json({ error: "Internal server error" }, { status: 500 });
  }
}