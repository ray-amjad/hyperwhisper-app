/**
 * The shape Better Auth's client actually resolves with — it does NOT reject.
 * A refused sign-out comes back as `{ data: null, error: { ... } }`, so a
 * caller that ignores the resolved value redirects exactly as it would after a
 * success and leaves a live session behind. Fields are optional so Better
 * Auth's concrete error type stays assignable without a cast at the call site.
 */
export interface SignOutResult {
  error?: { message?: string; status?: number; statusText?: string } | null;
}

export interface SignOutRequest {
  signOut: () => Promise<SignOutResult>;
  navigate: (destination: string) => void;
  onError: (message: string) => void;
  redirectTo: string;
}

export const SIGN_OUT_ERROR_MESSAGE = "Sign out failed. Please try again.";

/**
 * Signs out and navigates ONLY when the sign-out actually succeeded. On a
 * resolved error — or a thrown rejection — it logs, reports the failure, and
 * stays put, so the page matches the session the user still has. It never
 * touches React state: the caller's `finally` owns the busy flag.
 */
export async function signOutAndRedirect({
  signOut,
  navigate,
  onError,
  redirectTo,
}: SignOutRequest): Promise<void> {
  try {
    const result = await signOut();
    const error = result?.error;

    if (error) {
      // Deliberate: the only signal a refused sign-out has.
      // eslint-disable-next-line no-console
      console.error(
        `[auth] Sign out failed: ${error.status ?? ""} ` +
          `${error.statusText ?? ""} ${error.message ?? ""}. ` +
          `The session was NOT ended.`,
      );
      onError(error.message ?? SIGN_OUT_ERROR_MESSAGE);

      return;
    }

    navigate(redirectTo);
  } catch (thrown) {
    // Deliberate: without this the caller's void-ed promise becomes an
    // unhandled rejection and the button is the only thing that moved.
    // eslint-disable-next-line no-console
    console.error("[auth] Sign out threw. The session was NOT ended.", thrown);
    onError(SIGN_OUT_ERROR_MESSAGE);
  }
}
