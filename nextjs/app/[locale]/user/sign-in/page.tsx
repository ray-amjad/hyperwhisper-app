"use client";

import { useState, useEffect } from "react";
import { useRouter, useSearchParams, useParams } from "next/navigation";

import { authClient } from "@/src/lib/auth-client";
import { sanitizeReturnTo } from "@/src/lib/license-key-redirect";

type Tab = "license-key" | "email";

/**
 * User Sign-In Page
 *
 * Unified sign-in page for all users (customers and admins).
 * Provides two sign-in methods via tabs:
 * 1. License Key — enter HW-XXXX-XXXX-XXXX-XXXX directly
 * 2. Email — magic link authentication via Better Auth
 *
 * Note: Any email can sign in. Admin features are shown based on
 * email being in the admin allowlist (checked in layout/middleware).
 */
export default function UserSignInPage() {
  const router = useRouter();
  const searchParams = useSearchParams();
  const params = useParams();
  const locale = (params.locale as string) || "en";

  const [activeTab, setActiveTab] = useState<Tab>("license-key");
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

  function getCallbackURL() {
    const returnTo = searchParams.get("returnTo");
    return sanitizeReturnTo(returnTo, `/${locale}/user/dashboard`);
  }

  const licenseKeyParam = searchParams.get("licenseKey");

  useEffect(() => {
    if (!licenseKeyParam) return;
    setLicenseKey(licenseKeyParam);
    setAutoSigningIn(true);
    handleLicenseKeySignInWithKey(licenseKeyParam);
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
      const data = await res.json();
      if (!res.ok) {
        setLicenseError(data.error ?? "Sign-in failed. Please try again.");
        setAutoSigningIn(false);
      } else {
        router.push(data.redirect);
      }
    } catch {
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
    setEmailSubmitting(true);

    try {
      const callbackURL = getCallbackURL();
      const { error: authError } = await authClient.signIn.magicLink({
        email,
        callbackURL,
      });

      if (authError) {
        setEmailError(authError.message ?? "Failed to send magic link. Please try again.");
      } else {
        setMagicLinkSent(true);
      }
    } catch {
      setEmailError("An unexpected error occurred. Please try again.");
    } finally {
      setEmailSubmitting(false);
    }
  }

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

          {/* Tab Switcher */}
          <div className="flex rounded-lg bg-white/5 border border-white/10 p-1 mb-6">
            <button
              type="button"
              onClick={() => setActiveTab("license-key")}
              className={`flex-1 py-2 px-3 rounded-md text-sm font-medium transition-all duration-200 ${
                activeTab === "license-key"
                  ? "bg-white/15 text-white shadow-sm"
                  : "text-gray-400 hover:text-gray-300"
              }`}
            >
              Account Key
            </button>
            <button
              type="button"
              onClick={() => setActiveTab("email")}
              className={`flex-1 py-2 px-3 rounded-md text-sm font-medium transition-all duration-200 ${
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
              {licenseError && (
                <div className="p-4 bg-red-500/20 border border-red-500/30 rounded-lg">
                  <p className="text-red-300 text-sm text-center">{licenseError}</p>
                </div>
              )}

              <div>
                <label htmlFor="license-key" className="block text-sm font-medium text-gray-300 mb-2">
                  Account Key
                </label>
                <input
                  id="license-key"
                  type="text"
                  required
                  value={licenseKey}
                  onChange={(e) => setLicenseKey(e.target.value)}
                  className="w-full px-4 py-3 bg-white/5 border border-white/10 rounded-lg text-white placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-emerald-500 focus:border-transparent transition-all font-mono tracking-wide"
                  placeholder="HW-XXXX-XXXX-XXXX-XXXX"
                  autoComplete="off"
                  spellCheck={false}
                />
                <p className="text-gray-500 text-xs mt-2">
                  Enter the Account Key from your purchase confirmation
                </p>
              </div>

              <div className="space-y-3">
                <button
                  type="submit"
                  disabled={licenseSubmitting}
                  className="w-full py-3 px-4 bg-gradient-to-r from-emerald-500 to-teal-500 text-white font-semibold rounded-lg hover:from-emerald-600 hover:to-teal-600 transition-all duration-200 shadow-lg hover:shadow-xl disabled:opacity-50 disabled:cursor-not-allowed"
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
                  {emailError && (
                    <div className="p-4 bg-red-500/20 border border-red-500/30 rounded-lg">
                      <p className="text-red-300 text-sm text-center">{emailError}</p>
                    </div>
                  )}

                  <div>
                    <label htmlFor="email" className="block text-sm font-medium text-gray-300 mb-2">
                      Email address
                    </label>
                    <input
                      id="email"
                      type="email"
                      required
                      value={email}
                      onChange={(e) => setEmail(e.target.value)}
                      className="w-full px-4 py-3 bg-white/5 border border-white/10 rounded-lg text-white placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-emerald-500 focus:border-transparent transition-all"
                      placeholder="your@email.com"
                    />
                    <p className="text-gray-500 text-xs mt-2">
                      Use the same email you used to purchase your license
                    </p>
                  </div>

                  <div className="space-y-3">
                    <button
                      type="submit"
                      disabled={emailSubmitting}
                      className="w-full py-3 px-4 bg-gradient-to-r from-emerald-500 to-teal-500 text-white font-semibold rounded-lg hover:from-emerald-600 hover:to-teal-600 transition-all duration-200 shadow-lg hover:shadow-xl disabled:opacity-50 disabled:cursor-not-allowed"
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

                  <div className="space-y-3">
                    <button
                      type="button"
                      onClick={() => handleSendMagicLink()}
                      disabled={emailSubmitting}
                      className="w-full py-3 px-4 bg-gradient-to-r from-emerald-500 to-teal-500 text-white font-semibold rounded-lg hover:from-emerald-600 hover:to-teal-600 transition-all duration-200 shadow-lg hover:shadow-xl disabled:opacity-50 disabled:cursor-not-allowed"
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
