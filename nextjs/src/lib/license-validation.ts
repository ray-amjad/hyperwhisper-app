import { NextResponse } from "next/server";

import { polarClient, POLAR_ORGANIZATION_ID } from "@/lib/clients/polar";
import {
  findAccountByKey,
  findAccountById,
  findAccountByPolarLicenseKeyId,
  insertAccountKey,
  grantCreditLot,
  getOrCreateUser,
  type AccountKeyRow,
} from "@/src/lib/db-layer";
import {
  probeLicenseKeyReadOnly,
  type LicenseInvalid,
  type LicenseInvalidReason,
  type ReadOnlyLicenseProbeResult,
} from "@/src/lib/license-validation-probe";
import { isPolarKeyNotFoundError } from "./polar-errors";

/**
 * Shared license key validation logic.
 *
 * Used by both /api/license/validate and /api/license/activate so that
 * every entry point performs a real database check (with Polar fallback)
 * instead of trusting the key blindly.
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

/**
 * Import a license from Polar into the local database.
 *
 * Called when a license key is not found in the database but may exist in Polar.
 * Creates the license record, user (if needed), and grants 5000 credits.
 */
async function importLicenseFromPolar(licenseKey: string): Promise<
  | { success: true; licenseId: string }
  | { success: false; error: string; reason: LicenseInvalidReason }
> {
  try {
    // 1. Validate with Polar API
    const polarResult = await polarClient.customerPortal.licenseKeys.validate({
      key: licenseKey,
      organizationId: POLAR_ORGANIZATION_ID,
    });

    // 2. Check if valid
    if (polarResult.status !== "granted") {
      // Polar answered, and the answer is "not entitled" — a real verdict.
      return {
        success: false,
        error: `License is ${polarResult.status}`,
        reason: "not_entitled",
      };
    }

    // 2b. Dedupe by the stable Polar license-key id (casing-independent).
    // findAccountByKey is a case-sensitive exact match, so a different casing
    // or whitespace variant of an already-imported key misses the DB lookup
    // and reaches this fallback. Without this guard each variant would insert a
    // fresh license row and grant another 5000 credits. polarResult.id is the
    // canonical resource id regardless of how the input key was cased.
    const alreadyImported = await findAccountByPolarLicenseKeyId(
      polarResult.id,
    );
    if (alreadyImported) {
      return { success: true, licenseId: alreadyImported.id };
    }

    // 3. Get customer email - required for import
    const email = polarResult.customer?.email;
    if (!email) {
      return {
        success: false,
        error: "License has no associated email",
        reason: "lookup_failed",
      };
    }

    // 4. Get or create user
    const user = await getOrCreateUser(email, {
      name: polarResult.customer?.name ?? undefined,
      polarCustomerId: polarResult.customerId ?? undefined,
    });
    if (!user) {
      return {
        success: false,
        error: "Failed to get or create user",
        reason: "lookup_failed",
      };
    }

    // 5. Insert license into database
    const license = await insertAccountKey({
      key: licenseKey,
      email: email.toLowerCase().trim(),
      userId: user.id,
      polarLicenseKeyId: polarResult.id,
      polarCustomerId: polarResult.customerId ?? null,
      status: "granted",
    });

    if (!license) {
      console.error("Failed to insert license from Polar");
      return {
        success: false,
        error: "Failed to import license",
        reason: "lookup_failed",
      };
    }

    // 6. Create credit balance with 5000 credits
    try {
      await grantCreditLot({
        userId: license.userId,
        amount: 5000,
        sourceType: "polar_bundle",
        sourceId: polarResult.id,
      });
    } catch (creditError) {
      console.error("Failed to create credit balance:", creditError);
      // License was created, but credits failed - still return success
    }

    console.log(
      `Imported license from Polar: ${licenseKey} for ${email} with 5000 credits`,
    );

    return { success: true, licenseId: license.id };
  } catch (err) {
    // The SDK THROWS for a key Polar has never heard of, so this catch sees
    // both an unknown key (an ordinary verdict — the single commonest
    // rejection, e.g. a typo) and a Polar outage (an incident). Split them;
    // anything unrecognised stays `lookup_failed`. See `isPolarKeyNotFoundError`.
    if (isPolarKeyNotFoundError(err)) {
      return {
        success: false,
        error: "License key not found",
        reason: "not_entitled",
      };
    }

    console.error("Polar license import error:", err);
    return {
      success: false,
      error: "Failed to validate with Polar",
      reason: "lookup_failed",
    };
  }
}

export type LicenseCheckResult =
  | { valid: true; license: AccountKeyRow }
  | LicenseInvalid;

/**
 * Read-only license check for UI that presents Test separately from Activate.
 * Existing database rows are read directly; Polar fallback is validated live
 * without importing a row, creating a user, or granting credits.
 */
export async function probeLicenseKey(
  licenseKey: string,
): Promise<ReadOnlyLicenseProbeResult> {
  return probeLicenseKeyReadOnly(licenseKey, {
    findStoredLicense: findAccountByKey,
    validateWithPolar: async (key) =>
      polarClient.customerPortal.licenseKeys.validate({
        key,
        organizationId: POLAR_ORGANIZATION_ID,
      }),
  });
}

/**
 * Checks whether a license key is valid (exists and is "granted").
 *
 * 1. Looks up the key in the database
 * 2. POLAR FALLBACK: if not found, validates against Polar and imports it
 * 3. Verifies the license status is "granted" (not revoked/disabled)
 */
export async function checkLicenseKey(
  licenseKey: string,
): Promise<LicenseCheckResult> {
  // Query database for the license
  let license = await findAccountByKey(licenseKey.trim());

  // POLAR FALLBACK: If not found in DB, try Polar API
  if (!license) {
    const polarImport = await importLicenseFromPolar(licenseKey.trim());

    if (!polarImport.success) {
      // Forward the import's own classification rather than inventing one:
      // only IT knows which branch it took. See `LicenseInvalidReason`.
      return {
        valid: false,
        error: polarImport.error,
        status: 400,
        reason: polarImport.reason,
      };
    }

    // Re-query the newly imported license
    license = await findAccountById(polarImport.licenseId);

    if (!license) {
      return {
        valid: false,
        error: "Failed to retrieve imported license",
        status: 500,
        reason: "lookup_failed",
      };
    }
  }

  // Check status
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
