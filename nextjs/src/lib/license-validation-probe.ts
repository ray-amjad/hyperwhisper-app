export type ReadOnlyLicenseProbeResult =
  | { valid: true }
  | { valid: false; error: string; status: number };

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
      };
    }
    return { valid: true };
  } catch {
    return {
      valid: false,
      error: "Failed to validate with Polar",
      status: 400,
    };
  }
}
