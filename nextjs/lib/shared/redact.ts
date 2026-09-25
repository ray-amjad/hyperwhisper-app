import { sha256Hex } from "@/lib/shared/sha256";

/**
 * A 12-hex-character tag for an email address, safe to write to a server log.
 *
 * The address is trimmed and lower-cased first, then hashed, so the tag for a
 * given customer is the same whatever case Stripe or a form handed us — and it
 * equals the `recipientHash=` that `sendMagicLink` in `src/lib/auth.ts` logs for
 * the same address, so a purchase line and a sign-in line correlate (#717).
 *
 * `sha256Hex` itself stays a pure digest; the normalisation lives here.
 * Node-runtime server code only (`node:crypto`, via `sha256Hex`).
 */
export function emailTag(email: string): string {
  return sha256Hex(email.trim().toLowerCase()).slice(0, 12);
}
