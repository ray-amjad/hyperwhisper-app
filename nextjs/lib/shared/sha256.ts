import { createHash } from "node:crypto";

/**
 * Hex SHA-256 of `value`, as given.
 *
 * Deliberately a PURE digest: no trim, no lowercase, no normalisation. A caller
 * that needs a normalised digest normalises its own input first and says so —
 * `sendMagicLink` in `src/lib/auth.ts` hashes `email.trim().toLowerCase()` so a
 * failed send can be correlated with a user's support report without the address
 * ever reaching a log. Normalising in here instead would make every caller's
 * digest a hash of something other than the value it passed, silently.
 * `tests/magic-link-send.test.ts` pins a literal known-answer hex for an input
 * with surrounding whitespace and mixed case, so adding a `.trim()` here fails.
 *
 * Node-runtime server code only (`node:crypto`).
 */
export function sha256Hex(value: string): string {
  return createHash("sha256").update(value, "utf8").digest("hex");
}
