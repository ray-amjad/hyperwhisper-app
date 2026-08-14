// Relative, not aliased: this module and its test run under plain `tsx`, which
// does not resolve the `@/*` tsconfig paths.
import { isPolarKeyNotFoundError } from "./polar-errors";

/**
 * Machine-readable classification of an invalid-license reply, sent alongside
 * the human-readable `error` string.
 *
 * ## Canonical rationale — cite this symbol, don't restate it
 *
 * Every invalid reply from `/api/license/validate` and `/api/license/activate`
 * is HTTP 400/500 with `{ valid: false, error }`. Neither the status code nor
 * the `error` text is a reliable discriminator, because several genuinely
 * different situations share both:
 *
 * - the license exists and is not granted (revoked, expired, disabled);
 * - Polar has never heard of the key;
 * - Polar could not be reached at all, or rejected our token, or 5xx'd;
 * - the request itself was malformed.
 *
 * The first two are ORDINARY, expected answers — a lapsed subscription or a
 * mistyped key. The third is an INCIDENT worth an alert. A client that guesses
 * from the response shape gets this wrong in the direction that matters: it
 * either pages on every lapsed subscription, or goes silent during exactly the
 * outage it needs to see. So the server — the only side that knows which
 * branch it took — states its classification explicitly in `reason`.
 *
 * The split between "Polar says this key does not exist" and "we could not ask
 * Polar" is made by `isPolarKeyNotFoundError` (see `polar-errors.ts`), which
 * defaults to `lookup_failed` whenever it cannot tell.
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
   * or Polar positively reports that no such key exists. Ordinary and expected.
   */
  | "not_entitled"
  /**
   * We could not establish the license's state — Polar unreachable, token
   * rejected, a 5xx, an unrecognised error shape, a DB fault. Also the default
   * whenever the classification is uncertain, because a false `not_entitled`
   * silences a real incident.
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
  } catch (err) {
    // The Polar SDK THROWS for a key it has never heard of, so this catch sees
    // both an ordinary verdict (unknown key) and an incident (Polar down, token
    // rotated, 5xx). Split them; anything unrecognised stays `lookup_failed`,
    // because a false `not_entitled` silences a real outage. The predicate is
    // `isPolarKeyNotFoundError`; the rationale is on `LicenseInvalidReason`.
    if (isPolarKeyNotFoundError(err)) {
      return {
        valid: false,
        error: "License key not found",
        status: 400,
        reason: "not_entitled",
      };
    }

    return {
      valid: false,
      error: "Failed to validate with Polar",
      status: 400,
      reason: "lookup_failed",
    };
  }
}
