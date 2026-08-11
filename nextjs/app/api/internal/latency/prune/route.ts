import { NextRequest, NextResponse } from "next/server";
import { lt, sql } from "drizzle-orm";

import { timingSafeEqualSecret } from "@/lib/security/timing-safe-secret";
import { db } from "@/src/db";
import { sttLatencySamples } from "@/src/db/schema/stt-latency-samples";

export const dynamic = "force-dynamic";
export const maxDuration = 60;

/** Retention promised on the public privacy page. */
const RETENTION_DAYS = 365;
/** Rows per delete. Bounded so one run can never hold a long table lock. */
const BATCH_SIZE = 5_000;
/** Batches per run. The daily cron catches up over a few days if it must. */
const MAX_BATCHES = 20;

/**
 * Deletes latency samples past the 1-year retention window.
 *
 * Runs daily from a Vercel cron (see vercel.json). Without this, "we keep these
 * rows for a year" would be a hope rather than a policy — and the privacy page
 * states it as a fact.
 *
 * Accepts the internal secret either as `x-internal-secret` (manual runs) or as
 * `Authorization: Bearer …` (Vercel cron, which cannot set custom headers).
 */
export async function GET(request: NextRequest) {
  const expected = process.env.HYPERWHISPER_INTERNAL_SECRET;
  const headerSecret = request.headers.get("x-internal-secret");
  const bearer = request.headers.get("authorization")?.replace(/^Bearer /, "") ?? null;

  const authorized =
    timingSafeEqualSecret(headerSecret, expected) ||
    timingSafeEqualSecret(bearer, expected) ||
    timingSafeEqualSecret(bearer, process.env.CRON_SECRET);

  if (!authorized) {
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