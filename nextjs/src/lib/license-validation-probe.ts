/**
 * Machine-readable classification of an invalid-license reply, sent alongside
 * the human-readable `error` string.
 *
 * The `error` string alone cannot carry this: an unknown key and a Polar
 * outage both come back as `"Failed to validate with Polar"`, and both are
 * HTTP 400 with `{ valid: false }`. Clients need to tell "this key is not
 * entitled" (an ordinary, expected answer) from "we could not find out" (an
 * incident worth an alert), so the server — the only side that knows which
 * branch it took — says so explicitly.
 *
 * Defined here, in the leaf module, so both `license-validation-probe.ts` and
 * `license-validation.ts` can use it without an import cycle;
 * `license-validation.ts` re-exports it for consumers of that façade.
 */
export type LicenseInvalidReason =
  /** A real entitlement verdict: the license exists and is not granted. */
  | "not_entitled"
  /** We could not establish the license's state (Polar down, DB fault, …). */
  | "lookup_failed"
  /** The request itself was malformed. */
  | "bad_request";

export type ReadOnlyLicenseProbeResult =
  | { valid: true }
  | {
      valid: false;
      error: string;
      status: number;
      reason: LicenseInvalidReason;
    };

export interface ReadOnlyLicenseProbeDependencies {
  findStoredLicense: (
    licenseKey: string,
  ) => Promise<{ status: string } | null | undefined>;
  validateWithPolar: (licenseKey: string) => Promise<{ status: string }>;
}

/**
 * Validate a key without importing it or granting credits.
 *
 * Mutating database operations are intentionally absent from the dependency
 * boundary, keeping the probe path read-only even for Polar fallback keys.
 */
export async function probeLicenseKeyReadOnly(
  licenseKey: string,
  dependencies: ReadOnlyLicenseProbeDependencies,
): Promise<ReadOnlyLicenseProbeResult> {
  const trimmedKey = licenseKey.trim();
  const storedLicense = await dependencies.findStoredLicense(trimmedKey);

  if (storedLicense) {
    if (storedLicense.status !== "granted") {
      return {
        valid: false,
        error: `License is ${storedLicense.status}`,
        status: 400,
        reason: "not_entitled",
      };
    }
    return { valid: true };
  }

  try {
    const polarResult = await dependencies.validateWithPolar(trimmedKey);
    if (polarResult.status !== "granted") {
      return {
        valid: false,
        error: `License is ${polarResult.status}`,
        status: 400,
        reason: "not_entitled",
      };
    }
    return { valid: true };
  } catch {
    // An unknown key and a Polar outage land here identically — we do not know
    // which, so this is never reported as an entitlement verdict.
    return {
      valid: false,
      error: "Failed to validate with Polar",
      status: 400,
      reason: "lookup_failed",
    };
  }
}
