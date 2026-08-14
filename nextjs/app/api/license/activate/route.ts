import { NextRequest, NextResponse } from "next/server";

import {
  checkLicenseKey,
  invalidLicenseResponse,
} from "@/src/lib/license-validation";
import { licenseValidateRateLimiter } from "@/lib/rate-limit";
import { getClientIPFromHeaders } from "@/server/api/routers/download-ip";

/**
 * License Activation API (legacy compatibility)
 *
 * This endpoint exists for backwards compatibility only.
 * Device tracking has moved to the /validate endpoint.
 *
 * BACKWARDS COMPATIBILITY:
 * - Old macOS app versions still call this endpoint and treat the
 *   response as the authoritative "is this license valid?" check
 * - Performs a REAL license check (DB lookup + Polar fallback) so
 *   invalid keys fail closed instead of open
 * - Returns a dummy activation_id on success - old apps expect it,
 *   but it is never used (deactivate is a stub)
 *
 * NEW FLOW:
 * - New app versions just call /validate with device_id
 * - No activation/deactivation - fair usage policy instead
 *
 * INVALID REPLIES CARRY A `reason` (LicenseInvalidReason):
 * This endpoint is backed by the same `checkLicenseKey` as /validate, so its
 * rejections carry the same machine-readable `reason` and are serialized by the
 * same `invalidLicenseResponse`. Keep it that way — one function behind two
 * endpoints that answer differently is how the wire contract drifts. What the
 * field means is documented once on `LicenseInvalidReason` in
 * src/lib/license-validation-probe.ts.
 */
export async function POST(req: NextRequest) {
  // Rate limit by IP before any DB lookup or Polar fallback. Like /validate,
  // this endpoint runs a live outbound Polar request on every unknown key, so
  // it shares the same amplification limiter.
  const clientIP = getClientIPFromHeaders(req.headers);
  const { success } = await licenseValidateRateLimiter.limit(clientIP);

  if (!success) {
    // `lookup_failed`, not `bad_request`: the request was well-formed, we
    // simply declined to establish the license's state. Never a verdict.
    return invalidLicenseResponse({
      valid: false,
      error: "Too many requests. Please try again later.",
      status: 429,
      reason: "lookup_failed",
    });
  }

  let body: unknown;
  try {
    body = await req.json();
  } catch {
    return invalidLicenseResponse({
      valid: false,
      error: "Invalid request body",
      status: 400,
      reason: "bad_request",
    });
  }

  const { license_key } = (body ?? {}) as { license_key?: string };

  if (!license_key) {
    return invalidLicenseResponse({
      valid: false,
      error: "License key is required",
      status: 400,
      reason: "bad_request",
    });
  }

  try {
    const result = await checkLicenseKey(license_key);

    if (!result.valid) {
      return invalidLicenseResponse(result);
    }

    // Old apps expect an activation_id, so we generate a UUID
    // This ID is never used - deactivate is also a stub
    return NextResponse.json({
      valid: true,
      activation_id: crypto.randomUUID(),
    });
  } catch (error) {
    console.error("License activation error:", error);

    // An unexpected fault: we could not establish the license's state, which is
    // exactly `lookup_failed`. Not a verdict — the client must keep reporting it.
    return invalidLicenseResponse({
      valid: false,
      error: "Failed to validate license. Please try again later.",
      status: 500,
      reason: "lookup_failed",
    });
  }
}
