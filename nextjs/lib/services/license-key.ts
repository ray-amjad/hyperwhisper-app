/**
 * License Key Generation Service
 *
 * Generates secure, unique license keys for HyperWhisper.
 * Format: HW-XXXX-XXXX-XXXX-XXXX (19 characters total)
 *
 * DESIGN DECISIONS:
 * - Prefix "HW-" identifies HyperWhisper keys visually
 * - Base32 alphabet without ambiguous chars (0/O, 1/I/L) reduces user errors
 * - Uses crypto.randomInt for unbiased cryptographic randomness
 * - 4 segments of 4 chars each for readability
 */
import crypto from "crypto";

// Base32-like alphabet without ambiguous characters
// Excludes: 0 (zero), O (oh), 1 (one), I (eye), L (el)
const ALPHABET = "ABCDEFGHJKMNPQRSTUVWXYZ23456789"; // 31 chars

/**
 * Generates a cryptographically secure license key.
 *
 * @returns A license key in format HW-XXXX-XXXX-XXXX-XXXX
 *
 * ENTROPY CALCULATION:
 * - 16 characters from 31-char alphabet = 16 * log2(31) ≈ 79 bits
 * - Each character is drawn uniformly via crypto.randomInt (no modulo bias)
 * - Total effective entropy: ~79 bits (sufficient for license keys)
 */
export function generateLicenseKey(): string {
  const MAX_RETRIES = 3;

  for (let attempt = 0; attempt < MAX_RETRIES; attempt++) {
    try {
      let key = "HW";

      // Generate 4 segments of 4 characters each
      for (let segment = 0; segment < 4; segment++) {
        key += "-";

        for (let char = 0; char < 4; char++) {
          // Draw a uniform index in [0, ALPHABET.length).
          // crypto.randomInt performs rejection sampling internally, so the
          // distribution is unbiased — unlike `byte % ALPHABET.length`, which
          // over-represented the first (256 % 31) = 8 alphabet symbols.
          const charIndex = crypto.randomInt(ALPHABET.length);

          // Validate charIndex is within alphabet bounds
          if (charIndex < 0 || charIndex >= ALPHABET.length) {
            throw new Error(
              `Character index ${charIndex} out of bounds (alphabet length: ${ALPHABET.length})`
            );
          }

          const alphabetChar = ALPHABET[charIndex];

          // Final safety check: ensure we got a valid character
          if (!alphabetChar || typeof alphabetChar !== "string" || alphabetChar.length !== 1) {
            throw new Error(
              `Invalid character at index ${charIndex}: got ${alphabetChar} (type: ${typeof alphabetChar})`
            );
          }

          key += alphabetChar;
        }
      }

      // Validate the generated key format before returning
      if (!isValidKeyFormat(key)) {
        throw new Error(`Generated key failed format validation: ${key}`);
      }

      return key;
    } catch (error) {
      // If this is the last attempt, throw the error
      if (attempt === MAX_RETRIES - 1) {
        throw new Error(
          `Failed to generate valid license key after ${MAX_RETRIES} attempts: ${error instanceof Error ? error.message : String(error)}`
        );
      }
      // Otherwise, log and retry
      console.warn(
        `License key generation attempt ${attempt + 1} failed, retrying...`,
        error
      );
    }
  }

  // This should never be reached due to the throw above, but TypeScript needs it
  throw new Error("License key generation failed unexpectedly");
}

/**
 * Validates that a license key matches the expected format.
 * Does NOT check database validity - only format validation.
 *
 * @param key - The license key to validate
 * @returns true if format is valid, false otherwise
 */
export function isValidKeyFormat(key: string): boolean {
  if (!key || typeof key !== "string") {
    return false;
  }

  // Format: HW-XXXX-XXXX-XXXX-XXXX
  // Where X is any character from ALPHABET
  const pattern = /^HW-[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}$/;
  return pattern.test(key.toUpperCase());
}

/**
 * Normalizes a license key for comparison/storage.
 * - Converts to uppercase
 * - Trims whitespace
 * - Ensures consistent format
 *
 * @param key - The license key to normalize
 * @returns Normalized key or null if invalid format
 */
export function normalizeLicenseKey(key: string): string | null {
  if (!key || typeof key !== "string") {
    return null;
  }

  const normalized = key.toUpperCase().trim();

  if (!isValidKeyFormat(normalized)) {
    return null;
  }

  return normalized;
}
