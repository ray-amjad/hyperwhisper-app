import { NextRequest, NextResponse } from "next/server";
import { lt, sql } from "drizzle-orm";

import { isAuthorizedInternalCron } from "@/lib/security/internal-cron-auth";
import { db } from "@/src/db";
import { session } from "@/src/db/schema/auth";

export const dynamic = "force-dynamic";
export const maxDuration = 60;

/** Rows per delete. Bounded so one run can never hold a long table lock. */
const BATCH_SIZE = 5_000;
/** Batches per run. The daily cron catches up over a few days if it must. */
const MAX_BATCHES = 20;

/**
 * Deletes sign-in sessions that have expired.
 *
 * Each session row holds the IP address and browser user agent of the
 * sign-in (Better Auth writes both on every `createSession`). Better Auth
 * deletes an expired row only when its holder comes back with that cookie on
 * a POST (`deferSessionRefresh` in src/lib/auth.ts), so a member who signs in
 * once and never returns would leave an IP address in the database for good.
 * This daily Vercel cron (see vercel.json) is what makes the privacy page's
 * "deleted within a day of the session expiring" true.
 *
 * `expires_at` is the live expiry: Better Auth moves it forward each time it
 * refreshes a session, so a row past it can no longer sign anyone in.
 *
 * Who may run it is lib/security/internal-cron-auth.ts.
 */
export async function GET(request: NextRequest) {
  if (!isAuthorizedInternalCron(request, "session prune")) {
    return NextResponse.json({ error: "Unauthorized" }, { status: 401 });
  }

  const cutoff = new Date(Date.now());

  try {
    let deleted = 0;
    for (let batch = 0; batch < MAX_BATCHES; batch += 1) {
      const result = await db.delete(session).where(
        sql`${session.id} in (
          select ${session.id} from ${session}
          where ${lt(session.expiresAt, cutoff)}
          limit ${BATCH_SIZE}
        )`,
      );
      const count = result.rowCount ?? 0;
      deleted += count;
      if (count < BATCH_SIZE) break;
    }

    return NextResponse.json({ deleted, cutoff: cutoff.toISOString() });
  } catch (error) {
    console.error("Error pruning expired sessions:", error);
    return NextResponse.json({ error: "Internal server error" }, { status: 500 });
  }
}
