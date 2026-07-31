import { NextRequest, NextResponse } from "next/server";
import { timingSafeEqualSecret } from "@/lib/security/timing-safe-secret";
import { getGrantedEmails } from "@/src/lib/db-layer";

/**
 * Internal bulk read: returns every distinct normalized email that holds at
 * least one granted Account Key. Powers the ACS admin backfill, which marks
 * those members as having already claimed their credit perk. Read-only; no
 * mutation. Same x-internal-secret gate (timing-safe, 401 on bad secret) as the
 * other internal routes.
 */
export async function POST(request: NextRequest) {
  const secret = request.headers.get("x-internal-secret");
  if (!timingSafeEqualSecret(secret, process.env.HYPERWHISPER_INTERNAL_SECRET)) {
    return NextResponse.json({ error: "Unauthorized" }, { status: 401 });
  }

  try {
    const emails = await getGrantedEmails();
    return NextResponse.json({ emails });
  } catch (error) {
    console.error("Error in granted-emails:", error);
    return NextResponse.json({ error: "Internal server error" }, { status: 500 });
  }
}
