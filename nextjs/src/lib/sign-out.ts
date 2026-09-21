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
 * stays put, so the page matches the session the user still has.
 *
 * Returns whether it navigated. That boolean is the whole busy-flag decision:
 * `true` means the document is on its way out and nothing should be re-armed,
 * `false` means the user is still here and the UI has to come back to life.
 * It never touches React state itself — `createSignOutHandler` owns that.
 */
export async function signOutAndRedirect({
  signOut,
  navigate,
  onError,
  redirectTo,
}: SignOutRequest): Promise<boolean> {
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

      return false;
    }

    navigate(redirectTo);

    return true;
  } catch (thrown) {
    // Deliberate: without this the caller's void-ed promise becomes an
    // unhandled rejection and the button is the only thing that moved.
    // eslint-disable-next-line no-console
    console.error("[auth] Sign out threw. The session was NOT ended.", thrown);
    onError(SIGN_OUT_ERROR_MESSAGE);

    return false;
  }
}

export interface SignOutHandlerRequest {
  signOut: () => Promise<SignOutResult>;
  navigate: (destination: string) => void;
  setBusy: (busy: boolean) => void;
  setError: (message: string | null) => void;
  redirectTo: string;
}

/**
 * Builds the click handler `UserHeader` hands to its Sign Out button.
 *
 * Every decision the button makes lives HERE, not in the component, because a
 * component in this repo is reachable only through `renderToStaticMarkup` —
 * no click can be dispatched, so a rule written inline in the component is a
 * rule nothing can test. The component's only remaining job is to CALL this in
 * its render body and wire the result to `onClick`, which a static render can
 * prove.
 *
 * The busy flag is asymmetric on purpose (#881 review round 1, finding 1).
 * `navigate` sets `window.location.href`, which only SCHEDULES a navigation:
 * the document stays live and interactive for the whole page load that
 * follows. Clearing busy there would re-enable the button, flip the label back
 * to "Sign Out", and let a second sign-out be clicked against a session that is
 * already gone. So busy is cleared on the failure paths ONLY — where the user
 * really is still on this page — and deliberately left set once a navigation
 * has been scheduled.
 */
export function createSignOutHandler({
  signOut,
  navigate,
  setBusy,
  setError,
  redirectTo,
}: SignOutHandlerRequest): () => Promise<void> {
  return async function handleSignOut(): Promise<void> {
    setError(null);
    setBusy(true);

    const navigated = await signOutAndRedirect({
      signOut,
      navigate,
      onError: setError,
      redirectTo,
    });

    // Only when we are staying on this page. See the asymmetry note above.
    if (!navigated) setBusy(false);
  };
}
