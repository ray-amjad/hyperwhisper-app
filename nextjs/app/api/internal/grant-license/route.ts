import { NextRequest, NextResponse } from "next/server";
import {
  getAccountKeysByEmail,
  provisionAccountKeyForEmail,
} from "@/src/lib/db-layer";
import { parseInternalEmailRequest } from "../email-request";

export async function POST(request: NextRequest) {
  const parsed = await parseInternalEmailRequest(request);
  if ("response" in parsed) return parsed.response;
  const { email } = parsed;

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
