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

/**
 * Shared license key validation logic.
 *
 * Used by both /api/license/validate and /api/license/activate so that
 * every entry point performs a real database check (with Polar fallback)
 * instead of trusting the key blindly.
 */

/**
 * Import a license from Polar into the local database.
 *
 * Called when a license key is not found in the database but may exist in Polar.
 * Creates the license record, user (if needed), and grants 5000 credits.
 */
async function importLicenseFromPolar(licenseKey: string): Promise<{
  success: boolean;
  licenseId?: string;
  error?: string;
}> {
  try {
    // 1. Validate with Polar API
    const polarResult = await polarClient.customerPortal.licenseKeys.validate({
      key: licenseKey,
      organizationId: POLAR_ORGANIZATION_ID,
    });

    // 2. Check if valid
    if (polarResult.status !== "granted") {
      return { success: false, error: `License is ${polarResult.status}` };
    }

    // 2b. Dedupe by the stable Polar license-key id (casing-independent).
    // findAccountByKey is a case-sensitive exact match, so a different casing
    // or whitespace variant of an already-imported key misses the DB lookup
    // and reaches this fallback. Without this guard each variant would insert a
    // fresh license row and grant another 5000 credits. polarResult.id is the
    // canonical resource id regardless of how the input key was cased.
    const alreadyImported = await findAccountByPolarLicenseKeyId(polarResult.id);
    if (alreadyImported) {
      return { success: true, licenseId: alreadyImported.id };
    }

    // 3. Get customer email - required for import
    const email = polarResult.customer?.email;
    if (!email) {
      return { success: false, error: "License has no associated email" };
    }

    // 4. Get or create user
    const user = await getOrCreateUser(email, {
      name: polarResult.customer?.name ?? undefined,
      polarCustomerId: polarResult.customerId ?? undefined,
    });
    if (!user) {
      return { success: false, error: "Failed to get or create user" };
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
      return { success: false, error: "Failed to import license" };
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
      `Imported license from Polar: ${licenseKey} for ${email} with 5000 credits`
    );

    return { success: true, licenseId: license.id };
  } catch (err) {
    console.error("Polar license import error:", err);
    return {
      success: false,
      error: "Failed to validate with Polar",
    };
  }
}

export type LicenseCheckResult =
  | { valid: true; license: AccountKeyRow }
  | { valid: false; error: string; status: number };

/**
 * Checks whether a license key is valid (exists and is "granted").
 *
 * 1. Looks up the key in the database
 * 2. POLAR FALLBACK: if not found, validates against Polar and imports it
 * 3. Verifies the license status is "granted" (not revoked/disabled)
 */
export async function checkLicenseKey(
  licenseKey: string
): Promise<LicenseCheckResult> {
  // Query database for the license
  let license = await findAccountByKey(licenseKey.trim());

  // POLAR FALLBACK: If not found in DB, try Polar API
  if (!license) {
    const polarImport = await importLicenseFromPolar(licenseKey.trim());

    if (!polarImport.success) {
      return {
        valid: false,
        error: polarImport.error || "License key not found",
        status: 400,
      };
    }

    // Re-query the newly imported license
    license = await findAccountById(polarImport.licenseId!);

    if (!license) {
      return {
        valid: false,
        error: "Failed to retrieve imported license",
        status: 500,
      };
    }
  }

  // Check status
  if (license.status !== "granted") {
    return {
      valid: false,
      error: `License is ${license.status}`,
      status: 400,
    };
  }

  return { valid: true, license };
}
