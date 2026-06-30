import { NextRequest, NextResponse } from "next/server";
import { timingSafeEqualSecret } from "@/lib/security/timing-safe-secret";
import {
  getAccountKeysByEmail,
  provisionAccountKeyForEmail,
} from "@/src/lib/db-layer";

export async function POST(request: NextRequest) {
  // Validate internal secret
  const secret = request.headers.get("x-internal-secret");
  if (!timingSafeEqualSecret(secret, process.env.HYPERWHISPER_INTERNAL_SECRET)) {
    return NextResponse.json({ error: "Unauthorized" }, { status: 401 });
  }

  let email: string;
  try {
    const body = await request.json();
    email = body?.email;
    if (!email || typeof email !== "string") {
      return NextResponse.json({ error: "email is required" }, { status: 400 });
    }
    email = email.toLowerCase().trim();
  } catch {
    return NextResponse.json({ error: "Invalid JSON body" }, { status: 400 });
  }

  try {
    // Check for an existing *granted* license by email (most recent first).
    // A revoked/refunded license must not be re-handed-out: it is dead at
    // /api/license/validate and returning it would skip the credit grant below.
    const existing = await getAccountKeysByEmail(email);
    const granted = existing.find((l) => l.status === "granted");
    if (granted) {
      return NextResponse.json({ licenseKey: granted.key });
    }

    // No granted license yet — mint one via the shared internal mint flow.
    const license = await provisionAccountKeyForEmail(email);
    return NextResponse.json({ licenseKey: license.key });
  } catch (error) {
    console.error("Error in grant-license:", error);
    return NextResponse.json({ error: "Internal server error" }, { status: 500 });
  }
}
