/**
 * Machine-readable classification of an invalid-license reply, sent alongside
 * the human-readable `error` string.
 *
 * ## Canonical rationale — cite this symbol, don't restate it
 *
 * Every invalid reply from `/api/license/validate` and `/api/license/activate`
 * is HTTP 400/429/500 with `{ valid: false, error }`. Neither the status code
 * nor the `error` text is a reliable discriminator, because genuinely
 * different situations share both:
 *
 * - the license exists and is not granted (revoked, expired, disabled);
 * - no such key exists in the database;
 * - the server could not establish the license's state (a DB fault, a
 *   rate-limited request, an unexpected exception);
 * - the request itself was malformed.
 *
 * The first two are ORDINARY, expected answers — a lapsed subscription or a
 * mistyped key. The third is an INCIDENT worth an alert. A client that guesses
 * from the response shape gets this wrong in the direction that matters: it
 * either pages on every lapsed subscription, or goes silent during exactly the
 * outage it needs to see. So the server — the only side that knows which
 * branch it took — states its classification explicitly in `reason`.
 *
 * Consumer: `LicenseNetworkService.licenseVerdictReason` in the macOS app
 * suppresses its Sentry capture for `not_entitled` only (HYPERWHISPER-SP /
 * HYPERWHISPER-FM), and tags the event with the reason otherwise.
 *
 * Defined here, in the leaf module, so both `license-validation-probe.ts` and
 * `license-validation.ts` can use it without an import cycle.
 */
export type LicenseInvalidReason =
  /**
   * A real entitlement verdict. Either the license exists and is not granted,
   * or no such key exists at all. Ordinary and expected.
   */
  | "not_entitled"
  /**
   * We could not establish the license's state — a rate-limited request, a DB
   * fault, an unexpected exception. Also the default whenever the
   * classification is uncertain, because a false `not_entitled` silences a
   * real incident.
   */
  | "lookup_failed"
  /** The request itself was malformed. */
  | "bad_request";

/**
 * The invalid arm shared by every license-check result, and the single input
 * to `invalidLicenseResponse` (see `license-validation.ts`), which is what
 * both license routes serialize through. Declared once so the two routes
 * cannot drift apart on the wire contract.
 */
export interface LicenseInvalid {
  valid: false;
  error: string;
  status: number;
  reason: LicenseInvalidReason;
}

export type ReadOnlyLicenseProbeResult = { valid: true } | LicenseInvalid;

export interface ReadOnlyLicenseProbeDependencies {
  findStoredLicense: (
    licenseKey: string,
  ) => Promise<{ status: string } | null | undefined>;
}

/**
 * Validate a key without writing anything.
 *
 * Mutating database operations are intentionally absent from the dependency
 * boundary, keeping the probe path read-only.
 */
export async function probeLicenseKeyReadOnly(
  licenseKey: string,
  dependencies: ReadOnlyLicenseProbeDependencies,
): Promise<ReadOnlyLicenseProbeResult> {
  const trimmedKey = licenseKey.trim();
  const storedLicense = await dependencies.findStoredLicense(trimmedKey);

  if (!storedLicense) {
    // An unknown key is an ordinary verdict, not an incident. See
    // `LicenseInvalidReason`.
    return {
      valid: false,
      error: "License key not found",
      status: 400,
      reason: "not_entitled",
    };
  }

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
