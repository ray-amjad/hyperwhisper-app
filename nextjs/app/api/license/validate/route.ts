import { NextRequest, NextResponse } from "next/server";

import { upsertDeviceValidation, getCreditBalance } from "@/src/lib/db-layer";
import { checkLicenseKey, probeLicenseKey } from "@/src/lib/license-validation";
import { licenseValidateRateLimiter } from "@/lib/rate-limit";
import { getClientIPFromHeaders } from "@/server/api/routers/download-ip";

/**
 * License Validation API
 *
 * Validates license keys against the database.
 * Supports both Polar-issued and Stripe-issued licenses.
 *
 * DEVICE TRACKING:
 * When device_id is provided, tracks the device validation in the
 * device_validations table for fair usage policy monitoring.
 *
 * BACKWARD COMPATIBILITY:
 * - macOS app calls this endpoint with same request/response format
 * - device_id is optional - old apps without it still work
 *
 * EXTENDED RESPONSE (when include_credits=true):
 * - Returns credit balance for CF Workers to cache
 * - Used by HyperWhisper Cloud for usage-based billing
 *
 * CHECKS:
 * 1. License key exists in database
 * 2. Status is "granted" (not revoked/disabled)
 *
 * POLAR FALLBACK:
 * If license not found in database, validates against Polar API.
 * If valid via Polar, imports the license to the database with 5000 credits.
 * (Lookup + fallback + status check live in src/lib/license-validation.ts,
 * shared with the legacy /activate endpoint.)
 *
 * INVALID REPLIES CARRY A `reason` (LicenseInvalidReason):
 * Every `{ valid: false }` body from this route also carries a machine-readable
 * `reason` — `not_entitled` (a real entitlement verdict), `lookup_failed` (we
 * could not establish the license's state) or `bad_request`. Clients cannot
 * derive this from the status code or the `error` text: an unknown key and a
 * Polar outage are both a 400 reading "Failed to validate with Polar". The
 * macOS app uses it to decide whether a rejection is ordinary (log it) or an
 * incident (report it) — see `isLicenseVerdictResponse` in
 * app/macos/hyperwhisper/Managers/LicenseNetworkService.swift.
 * Purely additive: `valid`, `error` and the status codes are unchanged, and
 * existing readers that ignore the field are unaffected.
 */

/**
 * Tracks device validation for fair usage monitoring.
 * This is non-blocking - errors are logged but don't fail the validation.
 */
async function trackDeviceValidation(
  licenseKeyId: string,
  deviceId: string,
  deviceName?: string,
): Promise<void> {
  try {
    await upsertDeviceValidation(licenseKeyId, deviceId, deviceName);
  } catch (error) {
    // Log but don't fail validation - tracking is non-critical
    console.error("Device tracking error:", error);
  }
}

export async function POST(req: NextRequest) {
  // Rate limit by IP before any DB lookup or Polar fallback. The fallback
  // issues a live outbound Polar request on every unknown key, so this caps
  // the amplification an unauthenticated caller can drive.
  const clientIP = getClientIPFromHeaders(req.headers);
  const { success } = await licenseValidateRateLimiter.limit(clientIP);

  if (!success) {
    return NextResponse.json(
      { valid: false, error: "Too many requests. Please try again later." },
      { status: 429 },
    );
  }

  let body: unknown;
  try {
    body = await req.json();
  } catch {
    return NextResponse.json(
      { valid: false, error: "Invalid request body", reason: "bad_request" },
      { status: 400 },
    );
  }

  const { license_key, include_credits, device_id, device_name, probe_only } =
    (body ?? {}) as {
      license_key?: string;
      include_credits?: boolean;
      device_id?: string;
      device_name?: string;
      probe_only?: boolean;
    };

  if (!license_key) {
    return NextResponse.json(
      {
        valid: false,
        error: "License key is required",
        reason: "bad_request",
      },
      { status: 400 },
    );
  }

  try {
    if (probe_only === true) {
      const result = await probeLicenseKey(license_key);
      if (!result.valid) {
        return NextResponse.json(
          { valid: false, error: result.error, reason: result.reason },
          { status: result.status },
        );
      }
      return NextResponse.json({ valid: true });
    }

    // Lookup in database, with Polar fallback + status check
    const result = await checkLicenseKey(license_key);

    if (!result.valid) {
      return NextResponse.json(
        { valid: false, error: result.error, reason: result.reason },
        { status: result.status },
      );
    }

    const license = result.license;

    // DEVICE TRACKING: Record device validation for fair usage monitoring
    if (device_id) {
      await trackDeviceValidation(license.id, device_id, device_name);
    }

    // License is valid - return extended info if requested
    if (include_credits) {
      const credits = await getCreditBalance(license.userId);

      return NextResponse.json({
        valid: true,
        credits,
        stripe_customer_id: license.stripeCustomerId || null,
      });
    }

    // Basic response for macOS app compatibility
    return NextResponse.json({ valid: true });
  } catch (error) {
    console.error("License validation error:", error);

    return NextResponse.json(
      {
        valid: false,
        error: "Failed to validate license. Please try again later.",
      },
      { status: 500 },
    );
  }
}
