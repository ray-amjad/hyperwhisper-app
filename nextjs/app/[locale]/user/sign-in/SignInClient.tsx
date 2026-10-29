"use client";

import { useState, useEffect, useLayoutEffect, useRef } from "react";
import { useRouter, useSearchParams, useParams } from "next/navigation";
import { useTranslations } from "next-intl";

import { authClient } from "@/src/lib/auth-client";
import { withoutAccountKeyParams } from "@/src/lib/account-key-redaction";
import { sanitizeReturnTo } from "@/src/lib/license-key-redirect";
import { isRecord } from "@/src/lib/type-guards";
import { reportClientError } from "@/lib/shared/report-client-error";
import { ErrorReveal, shake } from "@/components/ErrorReveal";
import { Spinner } from "@/components/ui/Spinner";
import { BTN_GLASS, BTN_LG, BTN_WHITE } from "@/components/landing/dusk";
import { INPUT, LABEL } from "@/components/landing/dusk-pages";

type Tab = "license-key" | "email";

/**
 * The codes Better Auth's `/magic-link/verify` puts on `?error=` when the link
 * itself is dead: already used (by the user, or by an email scanner that opened
 * it first), expired, or never issued. Every other code it can send there is a
 * server-side failure, which gets the generic message.
 */
const DEAD_MAGIC_LINK_ERRORS = new Set(["INVALID_TOKEN", "EXPIRED_TOKEN"]);

// Shared by every submit button: the Dusk white pill, full width.
const PRIMARY_CTA_CLASS_NAME = `${BTN_WHITE} ${BTN_LG} w-full disabled:cursor-not-allowed disabled:opacity-50`;

// "Use a Different Email": the dark glass pill.
const SECONDARY_CTA_CLASS_NAME = `${BTN_GLASS} ${BTN_LG} w-full`;

// "Back to Home": a quiet text button under the card's actions.
const BACK_CLASS_NAME =
  "mx-auto block rounded px-2 py-1 text-sm text-[var(--dusk-mute)] transition-colors hover:text-white focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--dusk-lav)]";

const FIELD_LABEL_CLASS_NAME = `mb-2 block text-start ${LABEL}`;
const FIELD_HELP_CLASS_NAME =
  "mt-2 text-start text-[12.5px] text-[var(--dusk-dim)]";

// Each sign-in method's toggle: white when chosen, muted otherwise.
const tabClassName = (active: boolean) =>
  `rounded-[9px] px-3 py-[9px] text-sm font-medium transition-colors duration-150 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--dusk-lav)] ${
    active ? "bg-white text-[#0b0b14]" : "text-[var(--dusk-mute)] hover:text-white"
  }`;

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

  // #752: the card tweens between the email form and the "we sent a magic
  // link" panel instead of changing height in one frame. The height is read
  // just before the swap; after it, the layout effect pins the card at that
  // height, then sets the new one, and `.t-resize` tweens between the two.
  // The pin comes off when the tween ends, so later changes (an error banner
  // in the panel) size the card as before.
  const card = useRef<HTMLDivElement>(null);
  const cardHeightBeforeSwap = useRef<number | null>(null);

  function rememberCardHeight() {
    cardHeightBeforeSwap.current = card.current?.offsetHeight ?? null;
  }

  useLayoutEffect(() => {
    const box = card.current;
    const from = cardHeightBeforeSwap.current;

    cardHeightBeforeSwap.current = null;
    if (!box || from === null) return;
    const to = box.offsetHeight;

    if (to === from) return;
    box.style.height = `${from}px`;
    void box.offsetHeight;
    box.style.height = `${to}px`;

    const unpin = () => {
      window.clearTimeout(timer);
      box.removeEventListener("transitionend", onEnd);
      box.style.height = "";
    };
    // Children's transitions bubble here too; only the card's own counts.
    const onEnd = (event: TransitionEvent) => {
      if (event.target === box && event.propertyName === "height") unpin();
    };
    // A transition that never runs (reduced motion) fires no transitionend.
    const timer = window.setTimeout(unpin, 400);

    box.addEventListener("transitionend", onEnd);
    // No cleanup: Strict Mode's dev-only re-run would unpin the card before
    // the tween ran. The timer bounds the pin either way.
  }, [magicLinkSent]);

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
        if (!magicLinkSent) rememberCardHeight();
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

  // ONE banner, rendered in BOTH email branches — shared as a variable, so
  // there is one markup and not two.
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
      className="rounded-xl border border-red-400/30 bg-red-500/[.12] p-4"
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
      className="rounded-xl border border-amber-300/30 bg-amber-500/[.12] p-4"
      role="alert"
    >
      <p className="text-amber-200 text-sm text-center" id="magic-link-error">
        {magicLinkError}
      </p>
    </div>
  ) : null;

  return (
    <main className="relative isolate flex min-h-[100svh] items-center justify-center overflow-hidden bg-[var(--dusk-night)] px-4 py-12 text-[var(--dusk-ink)]">
      {/* The homepage's dusk sky behind the card. Absolutely placed, so the
          card's position never depends on the image arriving. */}
      <div aria-hidden="true" className="absolute inset-0 -z-20">
        <img
          alt=""
          className="h-full w-full object-cover object-[center_60%] opacity-90"
          decoding="async"
          fetchPriority="high"
          sizes="100vw"
          src="/landing/dusk-1672.webp"
          srcSet="/landing/dusk-960.webp 960w, /landing/dusk-1672.webp 1672w"
        />
      </div>
      <div
        aria-hidden="true"
        className="absolute inset-0 -z-10 bg-[rgba(5,6,13,.25)]"
      />
      {/* The entrance is CSS (`dusk-enter`), on a wrapper so it never fights
          the card's own height tween; Reduce motion turns it off. */}
      <div className="dusk-enter w-full max-w-[420px]">
        <div
          ref={card}
          className="t-resize overflow-clip rounded-[22px] border border-white/[.14] bg-[rgba(10,12,28,.62)] p-6 text-center shadow-[0_40px_100px_rgba(0,0,0,.45)] backdrop-blur-[22px] sm:p-9"
        >
          {/* Header */}
          <div className="mb-6 text-center">
            <img
              alt=""
              className="mx-auto size-14 rounded-[15px] shadow-[0_0_0_1px_rgba(255,255,255,.1),0_10px_30px_rgba(0,0,0,.5)]"
              height={56}
              src="/icon/256.png"
              width={56}
            />
            <h1 className="mt-4 text-[28px] font-semibold tracking-[-0.8px] text-white">
              HyperWhisper
            </h1>
            <p className="mt-1.5 text-[15px] text-[var(--dusk-mute)]">
              Sign in to view your licenses and credits
            </p>
          </div>

          {/* Auto sign-in loading overlay */}
          {autoSigningIn ? (
            <div className="flex flex-col items-center justify-center py-8 gap-4">
              <Spinner className="h-8 w-8 text-[var(--dusk-lav)]" />
              <p className="text-sm text-[var(--dusk-mute)]">Signing you in…</p>
            </div>
          ) : (
          <>

          {/* Tab Switcher. A single-choice group of toggle buttons (#1311): the
              active method is exposed by aria-pressed, not only its colour.
              Not role="tablist", which would need arrow-key roving focus and
              aria-controls panels. */}
          <div
            aria-label="Sign-in method"
            className="mb-6 grid grid-cols-2 gap-1 rounded-xl bg-white/[.05] p-1"
            role="group"
          >
            <button
              aria-pressed={activeTab === "license-key"}
              type="button"
              onClick={() => setActiveTab("license-key")}
              className={tabClassName(activeTab === "license-key")}
            >
              Account Key
            </button>
            <button
              aria-pressed={activeTab === "email"}
              type="button"
              onClick={() => setActiveTab("email")}
              className={tabClassName(activeTab === "email")}
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
                    className="rounded-xl border border-red-400/30 bg-red-500/[.12] p-4"
                    role="alert"
                  >
                    <p className="text-red-300 text-sm text-center" id="license-key-error">
                      {licenseError}
                    </p>
                  </div>
                )}
              </ErrorReveal>

              <div>
                <label htmlFor="license-key" className={FIELD_LABEL_CLASS_NAME}>
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
                  className={`t-input ${INPUT} text-start font-mono tracking-wide`}
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
                <p className={FIELD_HELP_CLASS_NAME}>
                  Enter the Account Key from your purchase confirmation
                </p>
              </div>

              <div className="space-y-3">
                <button
                  type="submit"
                  disabled={licenseSubmitting}
                  className={PRIMARY_CTA_CLASS_NAME}
                >
                  {licenseSubmitting ? (
                    <span className="flex items-center justify-center gap-2">
                      <Spinner className="h-5 w-5" />
                      Signing in...
                    </span>
                  ) : (
                    "Sign In"
                  )}
                </button>

                <button
                  type="button"
                  onClick={() => router.push(`/${locale}`)}
                  className={BACK_CLASS_NAME}
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
                    <label htmlFor="email" className={FIELD_LABEL_CLASS_NAME}>
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
                      className={`t-input ${INPUT} text-start`}
                      placeholder="your@email.com"
                    />
                    <p className={FIELD_HELP_CLASS_NAME}>
                      Use the same email you used to purchase your license
                    </p>
                  </div>

                  <div className="space-y-3">
                    <button
                      type="submit"
                      disabled={emailSubmitting}
                      className={PRIMARY_CTA_CLASS_NAME}
                    >
                      {emailSubmitting ? (
                        <span className="flex items-center justify-center gap-2">
                          <Spinner className="h-5 w-5" />
                          Sending...
                        </span>
                      ) : (
                        "Send Magic Link"
                      )}
                    </button>

                    <button
                      type="button"
                      onClick={() => router.push(`/${locale}`)}
                      className={BACK_CLASS_NAME}
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
                      {/* #752: the one success moment on the page. The icon
                          fades and rotates up out of a blur while its stroke
                          draws (.t-success-check); pathLength="1" lets the
                          dash length be 1 whatever the path's real length. */}
                      <svg className="t-success-check mx-auto h-12 w-12 text-[var(--dusk-lav)]" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={1.5}>
                        <path pathLength={1} strokeLinecap="round" strokeLinejoin="round" d="M21.75 6.75v10.5a2.25 2.25 0 01-2.25 2.25h-15a2.25 2.25 0 01-2.25-2.25V6.75m19.5 0A2.25 2.25 0 0019.5 4.5h-15a2.25 2.25 0 00-2.25 2.25m19.5 0v.243a2.25 2.25 0 01-1.07 1.916l-7.5 4.615a2.25 2.25 0 01-2.36 0L3.32 8.91a2.25 2.25 0 01-1.07-1.916V6.75" />
                      </svg>
                    </div>
                    <p className="mb-1 text-[var(--dusk-ink)]">We sent a magic link to</p>
                    <p className="mb-4 font-semibold text-white [overflow-wrap:anywhere]">{email}</p>
                    <p className="text-sm text-[var(--dusk-mute)]">
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
                      className={PRIMARY_CTA_CLASS_NAME}
                    >
                      {emailSubmitting ? "Sending..." : "Resend Magic Link"}
                    </button>

                    <button
                      type="button"
                      onClick={() => {
                        rememberCardHeight();
                        setMagicLinkSent(false);
                        setEmailError(null);
                      }}
                      className={SECONDARY_CTA_CLASS_NAME}
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
    </main>
  );
}
