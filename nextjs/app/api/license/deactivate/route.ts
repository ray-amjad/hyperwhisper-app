import { NextRequest, NextResponse } from "next/server";

import { readBoundedText } from "@/src/lib/bounded-body";
import { isRecord } from "@/src/lib/type-guards";
import { unparseableRequestFields } from "@/src/lib/unparseable-request-fields";

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

/** The only field is a licence key; 16 KB is far above any real request (#2904). */
const MAX_BODY_BYTES = 16 * 1024;

export async function POST(req: NextRequest) {
  let body: unknown;
  try {
    const text = await readBoundedText(req, MAX_BODY_BYTES);

    if (text instanceof NextResponse) return text;
    body = JSON.parse(text);
  } catch (err) {
    console.error(
      "License deactivate: request JSON did not parse",
      unparseableRequestFields(req, err),
    );
    return NextResponse.json(
      { success: false, error: "Invalid request body" },
      { status: 400 }
    );
  }

  const license_key =
    typeof body === "object" && body !== null && "license_key" in body
      ? body.license_key
      : undefined;

  // Trimmed before the required check, as activate and validate do (#2319).
  if (typeof license_key !== "string" || !license_key.trim()) {
    // Same wire-format event as activate (#1299): pathname and type only.
    console.warn("License deactivate: license key missing", {
      path: req.nextUrl.pathname,
      licenseKeyType: isRecord(body) ? typeof license_key : "no-body-object",
    });

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
