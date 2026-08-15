import { NextResponse } from "next/server";

import { findAccountByKey, type AccountKeyRow } from "@/src/lib/db-layer";
import {
  probeLicenseKeyReadOnly,
  type LicenseInvalid,
  type ReadOnlyLicenseProbeResult,
} from "@/src/lib/license-validation-probe";

/**
 * Shared license key validation logic.
 *
 * Used by both /api/license/validate and /api/license/activate so that
 * every entry point performs a real database check instead of trusting
 * the key blindly.
 *
 * The database is the single source of truth. Historical Polar-sold keys
 * were imported into it by the (now removed) Polar fallback; Stripe-sold
 * keys are inserted by the Stripe webhook at purchase time.
 */

/**
 * The one way either license route serializes a rejection.
 *
 * Both routes are backed by `checkLicenseKey`, so a field added to the invalid
 * result has to reach both replies or the two endpoints disagree on the wire
 * contract. Routing every invalid exit through here makes that structural: the
 * argument is a `LicenseInvalid`, so a rejection cannot be serialized without
 * its `reason` (see `LicenseInvalidReason` for what the field is for).
 */
export function invalidLicenseResponse(result: LicenseInvalid): NextResponse {
  return NextResponse.json(
    { valid: false, error: result.error, reason: result.reason },
    { status: result.status },
  );
}

export type LicenseCheckResult =
  | { valid: true; license: AccountKeyRow }
  | LicenseInvalid;

/**
 * Read-only license check for UI that presents Test separately from Activate.
 * Reads the stored row directly; never writes.
 */
export async function probeLicenseKey(
  licenseKey: string,
): Promise<ReadOnlyLicenseProbeResult> {
  return probeLicenseKeyReadOnly(licenseKey, {
    findStoredLicense: findAccountByKey,
  });
}

/**
 * Checks whether a license key is valid (exists and is "granted").
 *
 * 1. Looks up the key in the database
 * 2. Verifies the license status is "granted" (not revoked/disabled)
 */
export async function checkLicenseKey(
  licenseKey: string,
): Promise<LicenseCheckResult> {
  const license = await findAccountByKey(licenseKey.trim());

  if (!license) {
    // An unknown key is an ordinary verdict — the single commonest rejection
    // (a typo, a revoked-and-purged key) — not an incident. See
    // `LicenseInvalidReason`.
    return {
      valid: false,
      error: "License key not found",
      status: 400,
      reason: "not_entitled",
    };
  }

  if (license.status !== "granted") {
    return {
      valid: false,
      error: `License is ${license.status}`,
      status: 400,
      reason: "not_entitled",
    };
  }

  return { valid: true, license };
}
