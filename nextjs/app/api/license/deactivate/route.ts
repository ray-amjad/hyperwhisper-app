import { NextRequest, NextResponse } from "next/server";

import { logUnparseableRequest } from "@/src/lib/log-unparseable-request";

/**
 * License Deactivation API (STUB)
 *
 * This endpoint is now a backwards-compatibility stub.
 * Deactivation is now handled locally in the macOS app.
 *
 * BACKWARDS COMPATIBILITY:
 * - Old macOS app versions still call this endpoint
 * - Returns success without any database changes
 *
 * NEW FLOW:
 * - New app versions handle deactivation locally (clear UserDefaults)
 * - No server-side activation tracking - fair usage policy instead
 */
export async function POST(req: NextRequest) {
  let body: unknown;
  try {
    body = await req.json();
  } catch (err) {
    logUnparseableRequest("License deactivate", req, err);
    return NextResponse.json(
      { success: false, error: "Invalid request body" },
      { status: 400 }
    );
  }

  const license_key =
    typeof body === "object" && body !== null && "license_key" in body
      ? body.license_key
      : undefined;

  if (typeof license_key !== "string" || !license_key) {
    return NextResponse.json(
      { success: false, error: "License key is required" },
      { status: 400 }
    );
  }

  // STUB: Always return success
  // Deactivation is now local-only in the app
  return NextResponse.json({
    success: true,
    message: "License deactivated successfully",
  });
}
