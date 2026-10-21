"use client";

import { useState, useEffect, useRef, type ReactNode } from "react";
import { useRouter, useSearchParams, useParams } from "next/navigation";
import { useTranslations } from "next-intl";

import { authClient } from "@/src/lib/auth-client";
import { withoutAccountKeyParams } from "@/src/lib/account-key-redaction";
import { sanitizeReturnTo } from "@/src/lib/license-key-redirect";
import { isRecord } from "@/src/lib/type-guards";
import { reportClientError } from "@/lib/shared/report-client-error";

type Tab = "license-key" | "email";

/**
 * #749: a form error reveals instead of mounting at full size in one frame.
 * The wrapper is always in the DOM (`.t-error-reveal` in globals.css); the
 * `role="alert"` banner inside it still mounts with its message, so the
 * screen-reader path is the one #760 built. `gap` is the space the parent's
 * `space-y-*` gave the banner: the wrapper drops that margin (`mb-0`) and
 * carries it as padding inside the clip, so a closed wrapper takes no space.
 */
function ErrorReveal({ gap, children }: { gap: string; children: ReactNode }) {
  return (
    <div className="t-error-reveal mb-0" data-open={children ? "" : undefined}>
      <div>
        <div className={gap}>{children}</div>
      </div>
    </div>
  );
}

/**
 * #749: shake the field a failed submit is about. Removing the class and
 * forcing a reflow before adding it back is what replays the shake on a
 * second failure; the class goes when the animation ends.
 */
function shake(element: HTMLElement | null) {
  if (!element) return;
  element.classList.remove("is-shaking");
  void element.offsetWidth;
  element.classList.add("is-shaking");
  element.addEventListener(
    "animationend",
    () => element.classList.remove("is-shaking"),
    { once: true },
  );
}

/**
 * The codes Better Auth's `/magic-link/verify` puts on `?error=` when the link
 * itself is dead: already used (by the user, or by an email scanner that opened
 * it first), expired, or never issued. Every other code it can send there is a
 * server-side failure, which gets the generic message.
 */
const DEAD_MAGIC_LINK_ERRORS = new Set(["INVALID_TOKEN", "EXPIRED_TOKEN"]);

// Shared by every submit button. The hover gradient is a ::before overlay that
// fades in by opacity, because a gradient background-image cannot transition.
const GRADIENT_CTA_CLASS_NAME =
  "relative isolate w-full py-3 px-4 bg-gradient-to-r from-emerald-500 to-teal-500 text-white font-semibold rounded-lg transition-[opacity,box-shadow] duration-200 shadow-lg hover:shadow-xl before:absolute before:inset-0 before:-z-10 before:rounded-[inherit] before:bg-gradient-to-r before:from-emerald-600 before:to-teal-600 before:opacity-0 before:transition-opacity before:duration-200 hover:before:opacity-100 disabled:opacity-50 disabled:cursor-not-allowed";

/**
 * User Sign-In form (client half of `page.tsx`).
 *
 * Unified sign-in page for all users (customers and admins).
 * Provides two sign-in methods via tabs:
 * 1. License Key — enter HW-XXXX-XXXX-XXXX-XXXX directly
 * 2. Email — magic link authentication via Better Auth
 *
 * Note: Any email can sign in. Admin features are shown based on
 * email being in the admin allowlist (checked in layout/middleware).
 *
 * The "you are already signed in, go to the dashboard" decision deliberately
 * does NOT live here or in `proxy.ts` — see `page.tsx`.
 */
export default function SignInClient() {
  const router = useRouter();
  const searchParams = useSearchParams();
  const params = useParams();
  const locale = typeof params.locale === "string" ? params.locale : "en";
  const t = useTranslations("signIn");

  // #2070: a magic link that failed to verify lands here with `?error=`, sent by
  // the `errorCallbackURL` below. Open on the Email tab, which is where the
  // message is and where a new link is requested.
  const magicLinkErrorCode = searchParams.get("error");

  const [activeTab, setActiveTab] = useState<Tab>(
    magicLinkErrorCode ? "email" : "license-key",
  );
  const [autoSigningIn, setAutoSigningIn] = useState(false);

  // License key tab state
  const [licenseKey, setLicenseKey] = useState("");
  const [licenseSubmitting, setLicenseSubmitting] = useState(false);
  const [licenseError, setLicenseError] = useState<string | null>(null);

  // Email tab state
  const [email, setEmail] = useState("");
  const [emailSubmitting, setEmailSubmitting] = useState(false);
  const [magicLinkSent, setMagicLinkSent] = useState(false);
  const [emailError, setEmailError] = useState<string | null>(null);
  const [magicLinkErrorDismissed, setMagicLinkErrorDismissed] = useState(false);

  const licenseKeyInput = useRef<HTMLInputElement>(null);
  const emailInput = useRef<HTMLInputElement>(null);

  // Every failure sets a fresh message after the submit cleared the old one,
  // so each one shakes, even when the text repeats.
  useEffect(() => {
    if (licenseError) shake(licenseKeyInput.current);
  }, [licenseError]);
  useEffect(() => {
    if (emailError) shake(emailInput.current);
  }, [emailError]);

  const magicLinkError =
    magicLinkErrorCode && !magicLinkErrorDismissed
      ? DEAD_MAGIC_LINK_ERRORS.has(magicLinkErrorCode)
        ? t("magicLinkExpired")
        : t("magicLinkFailed")
      : null;

  function getCallbackURL() {
    const returnTo = searchParams.get("returnTo");
    return sanitizeReturnTo(returnTo, `/${locale}/user/dashboard`);
  }

  // Where Better Auth sends a link that fails to verify, with `error=<code>`
  // added. Without it the failure went to `callbackURL` (the dashboard), the
  // proxy bounced the signed-out visitor back here and dropped the query, and
  // the user saw a plain form (#2070). `returnTo` rides along so the new link
  // still lands where the first one would have.
  function getErrorCallbackURL(callbackURL: string) {
    const query = new URLSearchParams({ returnTo: callbackURL });

    return `/${locale}/user/sign-in?${query.toString()}`;
  }

  const licenseKeyParam = searchParams.get("licenseKey");

  useEffect(() => {
    if (!licenseKeyParam) return;
    // The key is a credential: take it out of the address bar and the browser
    // history at once (#1897). `returnTo` stays, for getCallbackURL().
    window.history.replaceState(
      window.history.state,
      "",
      withoutAccountKeyParams(window.location.href),
    );
    setLicenseKey(licenseKeyParam);
    setAutoSigningIn(true);
    void handleLicenseKeySignInWithKey(licenseKeyParam);
  }, []); // eslint-disable-line react-hooks/exhaustive-deps

  async function handleLicenseKeySignInWithKey(key: string) {
    setLicenseError(null);
    setLicenseSubmitting(true);
    try {
      const callbackURL = getCallbackURL();
      const res = await fetch("/api/auth/sign-in/license-key", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ licenseKey: key.trim(), callbackURL }),
      });
      const data: unknown = await res.json();
      const error =
        isRecord(data) && typeof data.error === "string" ? data.error : null;
      const redirect =
        isRecord(data) && typeof data.redirect === "string"
          ? data.redirect
          : null;

      // Branch on the body, not just on `res.ok`. The endpoint rejects a bad
      // or revoked key with `ctx.json({ error }, { status: 400 })`, but Better
      // Auth's handler still puts that on the wire as HTTP 200, so `res.ok` was
      // true and this fell through to `router.push(undefined)` — a thrown
      // TypeError, no message, and a form that just sits there. Verified in
      // Chromium against the real endpoint before and after.
      if (!res.ok || error || !redirect) {
        setLicenseError(error ?? "Sign-in failed. Please try again.");
        setAutoSigningIn(false);
      } else {
        router.push(redirect);
      }
    } catch (error) {
      // Never the key itself: only the failure goes to the report.
      void reportClientError("sign_in.license_key", error);
      setLicenseError("An unexpected error occurred. Please try again.");
      setAutoSigningIn(false);
    } finally {
      setLicenseSubmitting(false);
    }
  }

  async function handleLicenseKeySignIn(e?: React.FormEvent) {
    e?.preventDefault();
    await handleLicenseKeySignInWithKey(licenseKey);
  }

  async function handleSendMagicLink(e?: React.FormEvent) {
    e?.preventDefault();
    setEmailError(null);
    // The dead link's message has done its job once a new link is asked for.
    setMagicLinkErrorDismissed(true);
    setEmailSubmitting(true);

    try {
      const callbackURL = getCallbackURL();
      const { error: authError } = await authClient.signIn.magicLink({
        email,
        callbackURL,
        errorCallbackURL: getErrorCallbackURL(callbackURL),
      });

      if (authError) {
        setEmailError(authError.message ?? "Failed to send magic link. Please try again.");
      } else {
        setMagicLinkSent(true);
      }
    } catch (error) {
      // Never the email address: only the failure goes to the report.
      void reportClientError("sign_in.magic_link", error);
      setEmailError("An unexpected error occurred. Please try again.");
    } finally {
      setEmailSubmitting(false);
    }
  }

  // ONE banner, rendered in BOTH email branches — shared as a variable the way
  // `spinnerSvg` below already is, so there is one markup and not two.
  //
  // It used to live inline in the `!magicLinkSent` form branch only, and the
  // "Resend Magic Link" button is in the other branch. So a failed resend set
  // `emailError` while `magicLinkSent` stayed true: nothing rendered it, the
  // button reverted from "Sending...", and the screen still read "We sent a
  // magic link to {email}" — #736's exact symptom, surviving on a second entry
  // point. "Use a Different Email" then cleared the message in the same commit
  // that would have revealed its branch. That resend path is also the likeliest
  // place to meet a Resend rate limit, because reaching it at all requires a
  // send that already succeeded.
  // `role="alert"` because in the `magicLinkSent` branch this banner is the ONLY
  // thing that changes when a resend fails: the surrounding copy still reads
  // "Check your email and click the link to sign in", so without it a screen
  // reader announces nothing and the page says the opposite of what happened.
  // #760: `id="email-error"` is what the `#email` input's `aria-describedby`
  // points at. The banner renders in two branches, but only one of them is
  // mounted at a time, so the id is still unique on the page.
  const emailErrorBanner = emailError ? (
    <div
      className="p-4 bg-red-500/20 border border-red-500/30 rounded-lg"
      role="alert"
    >
      <p className="text-red-300 text-sm text-center" id="email-error">
        {emailError}
      </p>
    </div>
  ) : null;

  // #2070: shown above the email form on arrival from a link that failed to
  // verify. `role="alert"` like the banner above, and `aria-describedby` on
  // `#email` points at it while it shows, since the field is where the fix is.
  const magicLinkErrorBanner = magicLinkError ? (
    <div
      className="p-4 bg-amber-500/20 border border-amber-500/30 rounded-lg"
      role="alert"
    >
      <p className="text-amber-200 text-sm text-center" id="magic-link-error">
        {magicLinkError}
      </p>
    </div>
  ) : null;

  const spinnerSvg = (
    <svg className="animate-spin h-5 w-5" fill="none" viewBox="0 0 24 24">
      <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
      <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4zm2 5.291A7.962 7.962 0 014 12H0c0 3.042 1.135 5.824 3 7.938l3-2.647z" />
    </svg>
  );

  return (
    <div className="min-h-screen flex items-center justify-center bg-gradient-to-br from-slate-900 via-slate-800 to-slate-900 py-12 px-4">
      <div className="max-w-md w-full">
        <div className="bg-white/10 backdrop-blur-lg rounded-2xl p-8 shadow-2xl border border-white/20">
          {/* Header */}
          <div className="text-center mb-8">
            <h1 className="text-2xl font-bold text-white mb-2">HyperWhisper</h1>
            <p className="text-gray-400 text-sm">
              Sign in to view your licenses and credits
            </p>
          </div>

          {/* Auto sign-in loading overlay */}
          {autoSigningIn ? (
            <div className="flex flex-col items-center justify-center py-8 gap-4">
              <svg className="animate-spin h-8 w-8 text-emerald-400" fill="none" viewBox="0 0 24 24">
                <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
                <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4zm2 5.291A7.962 7.962 0 014 12H0c0 3.042 1.135 5.824 3 7.938l3-2.647z" />
              </svg>
              <p className="text-gray-300 text-sm">Signing you in…</p>
            </div>
          ) : (
          <>

          {/* Tab Switcher. A single-choice group of toggle buttons (#1311): the
              active method is exposed by aria-pressed, not only its colour.
              Not role="tablist", which would need arrow-key roving focus and
              aria-controls panels. */}
          <div
            aria-label="Sign-in method"
            className="flex rounded-lg bg-white/5 border border-white/10 p-1 mb-6"
            role="group"
          >
            <button
              aria-pressed={activeTab === "license-key"}
              type="button"
              onClick={() => setActiveTab("license-key")}
              className={`flex-1 py-2 px-3 rounded-sm text-sm font-medium transition-all duration-200 ${
                activeTab === "license-key"
                  ? "bg-white/15 text-white shadow-sm"
                  : "text-gray-400 hover:text-gray-300"
              }`}
            >
              Account Key
            </button>
            <button
              aria-pressed={activeTab === "email"}
              type="button"
              onClick={() => setActiveTab("email")}
              className={`flex-1 py-2 px-3 rounded-sm text-sm font-medium transition-all duration-200 ${
                activeTab === "email"
                  ? "bg-white/15 text-white shadow-sm"
                  : "text-gray-400 hover:text-gray-300"
              }`}
            >
              Email
            </button>
          </div>

          {/* License Key Tab */}
          {activeTab === "license-key" && (
            <form onSubmit={handleLicenseKeySignIn} className="space-y-6">
              {/* #760: announced like `emailErrorBanner`, and described-by
                  target of the `#license-key` input below. No focus move. */}
              <ErrorReveal gap="pb-6">
                {licenseError && (
                  <div
                    className="p-4 bg-red-500/20 border border-red-500/30 rounded-lg"
                    role="alert"
                  >
                    <p className="text-red-300 text-sm text-center" id="license-key-error">
                      {licenseError}
                    </p>
                  </div>
                )}
              </ErrorReveal>

              <div>
                <label htmlFor="license-key" className="block text-sm font-medium text-gray-300 mb-2">
                  Account Key
                </label>
                <input
                  aria-describedby={licenseError ? "license-key-error" : undefined}
                  aria-invalid={licenseError ? true : undefined}
                  ref={licenseKeyInput}
                  id="license-key"
                  type="text"
                  required
                  value={licenseKey}
                  onChange={(e) => setLicenseKey(e.target.value)}
                  className="t-input w-full px-4 py-3 bg-white/5 border border-white/10 rounded-lg text-white placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-emerald-500 focus:border-transparent transition-all font-mono tracking-wide"
                  placeholder="HW-XXXX-XXXX-XXXX-XXXX"
                  /*
                    `off` is deliberate: the Account Key is a credential and we do not want a
                    browser to store it in its address/contact autofill. The email field below
                    does set `autoComplete="email"` — that one is an identity field and SC 1.3.5
                    asks for the token.
                  */
                  autoComplete="off"
                  spellCheck={false}
                />
                <p className="text-gray-400 text-xs mt-2">
                  Enter the Account Key from your purchase confirmation
                </p>
              </div>

              <div className="space-y-3">
                <button
                  type="submit"
                  disabled={licenseSubmitting}
                  className={GRADIENT_CTA_CLASS_NAME}
                >
                  {licenseSubmitting ? (
                    <span className="flex items-center justify-center gap-2">
                      {spinnerSvg}
                      Signing in...
                    </span>
                  ) : (
                    "Sign In"
                  )}
                </button>

                <button
                  type="button"
                  onClick={() => router.push(`/${locale}`)}
                  className="w-full py-3 px-4 bg-white/5 border border-white/20 text-gray-400 font-medium rounded-lg hover:bg-white/10 hover:text-white transition-all duration-200"
                >
                  Back to Home
                </button>
              </div>
            </form>
          )}

          {/* Email Tab */}
          {activeTab === "email" && (
            <>
              {!magicLinkSent ? (
                <form onSubmit={handleSendMagicLink} className="space-y-6">
                  {magicLinkErrorBanner}
                  <ErrorReveal gap="pb-6">{emailErrorBanner}</ErrorReveal>

                  <div>
                    <label htmlFor="email" className="block text-sm font-medium text-gray-300 mb-2">
                      Email address
                    </label>
                    <input
                      aria-describedby={
                        emailError
                          ? "email-error"
                          : magicLinkError
                            ? "magic-link-error"
                            : undefined
                      }
                      aria-invalid={emailError ? true : undefined}
                      autoComplete="email"
                      ref={emailInput}
                      id="email"
                      type="email"
                      required
                      value={email}
                      onChange={(e) => setEmail(e.target.value)}
                      className="t-input w-full px-4 py-3 bg-white/5 border border-white/10 rounded-lg text-white placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-emerald-500 focus:border-transparent transition-all"
                      placeholder="your@email.com"
                    />
                    <p className="text-gray-400 text-xs mt-2">
                      Use the same email you used to purchase your license
                    </p>
                  </div>

                  <div className="space-y-3">
                    <button
                      type="submit"
                      disabled={emailSubmitting}
                      className={GRADIENT_CTA_CLASS_NAME}
                    >
                      {emailSubmitting ? (
                        <span className="flex items-center justify-center gap-2">
                          {spinnerSvg}
                          Sending...
                        </span>
                      ) : (
                        "Send Magic Link"
                      )}
                    </button>

                    <button
                      type="button"
                      onClick={() => router.push(`/${locale}`)}
                      className="w-full py-3 px-4 bg-white/5 border border-white/20 text-gray-400 font-medium rounded-lg hover:bg-white/10 hover:text-white transition-all duration-200"
                    >
                      Back to Home
                    </button>
                  </div>
                </form>
              ) : (
                // Magic Link Sent confirmation
                <div className="space-y-6">
                  <div className="text-center">
                    <div className="mb-4">
                      <svg className="mx-auto h-12 w-12 text-emerald-400" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={1.5}>
                        <path strokeLinecap="round" strokeLinejoin="round" d="M21.75 6.75v10.5a2.25 2.25 0 01-2.25 2.25h-15a2.25 2.25 0 01-2.25-2.25V6.75m19.5 0A2.25 2.25 0 0019.5 4.5h-15a2.25 2.25 0 00-2.25 2.25m19.5 0v.243a2.25 2.25 0 01-1.07 1.916l-7.5 4.615a2.25 2.25 0 01-2.36 0L3.32 8.91a2.25 2.25 0 01-1.07-1.916V6.75" />
                      </svg>
                    </div>
                    <p className="text-gray-300 mb-1">We sent a magic link to</p>
                    <p className="font-semibold text-white mb-4">{email}</p>
                    <p className="text-gray-400 text-sm">
                      Check your email and click the link to sign in. The link will expire in 10 minutes.
                    </p>
                  </div>

                  {/* Above the button that produced it, not above the "we sent
                      a magic link" text: the earlier send really did succeed,
                      and it is this resend that just failed. */}
                  <div className="space-y-3">
                    <ErrorReveal gap="pb-3">{emailErrorBanner}</ErrorReveal>

                    <button
                      type="button"
                      onClick={() => handleSendMagicLink()}
                      disabled={emailSubmitting}
                      className={GRADIENT_CTA_CLASS_NAME}
                    >
                      {emailSubmitting ? "Sending..." : "Resend Magic Link"}
                    </button>

                    <button
                      type="button"
                      onClick={() => {
                        setMagicLinkSent(false);
                        setEmailError(null);
                      }}
                      className="w-full py-3 px-4 bg-white/5 border border-white/20 text-gray-400 font-medium rounded-lg hover:bg-white/10 hover:text-white transition-all duration-200"
                    >
                      Use a Different Email
                    </button>
                  </div>
                </div>
              )}
            </>
          )}

          </>
          )}
        </div>
      </div>
    </div>
  );
}
