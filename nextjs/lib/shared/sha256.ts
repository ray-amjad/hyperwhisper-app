import { createHash } from "node:crypto";

/**
 * Hex SHA-256 of `value`, as given.
 *
 * Deliberately a PURE digest: no trim, no lowercase, no normalisation. Callers
 * that take a prefix of this — `sendMagicLink` in `src/lib/auth.ts` logs
 * `sha256Hex(email).slice(0, 12)` so a failed send can be correlated without
 * the address ever reaching a log — need "a 12-character SHA-256 prefix of the
 * recipient address" to mean exactly that. Normalising here would silently make
 * the digest a hash of something else.
 *
 * Node-runtime server code only (`node:crypto`).
 */
export function sha256Hex(value: string): string {
  return createHash("sha256").update(value, "utf8").digest("hex");
}
