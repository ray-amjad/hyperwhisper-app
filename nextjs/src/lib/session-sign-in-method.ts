/**
 * How a web session was proven, stored on `session.sign_in_method`.
 *
 * - `email`: the magic link, so whoever holds the session reads the account's
 *   inbox.
 * - `account_key`: the license-key sign-in, so the session proves only that its
 *   holder has the key string.
 *
 * Better Auth's `session.create.before` hook in `auth.ts` stamps it from the
 * endpoint that created the session; it is never taken from client input.
 * Sessions created before the column existed have null and count as unproven.
 *
 * Rotating an Account Key is the remedy for a leaked key, so it needs an
 * `email` session (`server/api/routers/customer-rotate-key.ts`): a session
 * minted with the leaked key must not be able to rotate the key and lock the
 * owner out.
 */
export type SignInMethod = "email" | "account_key";

/** The Better Auth endpoint path that created a session, to its method. */
export function signInMethodForPath(
  path: string | undefined,
): SignInMethod | null {
  if (path === "/magic-link/verify") return "email";
  if (path === "/sign-in/license-key") return "account_key";

  return null;
}

/** Whether a session's stored method may rotate an Account Key. */
export function canRotateAccountKeys(signInMethod: unknown): boolean {
  return signInMethod === "email";
}
