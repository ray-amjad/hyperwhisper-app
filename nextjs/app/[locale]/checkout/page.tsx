"use client";

import { useEffect, useState } from "react";
import { useSearchParams } from "next/navigation";

/**
 * Checkout Page
 *
 * This page handles the HyperWhisper license checkout flow.
 * It calls the server-side API to create a Polar checkout session and redirects
 * the user to complete their purchase.
 *
 * Features:
 * - Accepts optional discount code via URL parameter (?code=COUPON123)
 * - Shows loading state while creating checkout session
 * - Calls server-side API for secure Polar integration
 * - Automatically redirects to Polar checkout
 * - Handles errors gracefully with user-friendly messages
 *
 * URL Parameters:
 * - code (optional): Discount coupon code to apply at checkout
 *
 * Example:
 * - /checkout - Standard checkout
 * - /checkout?code=WELCOME20 - Checkout with discount code pre-applied
 */
export default function CheckoutPage() {
  const searchParams = useSearchParams();
  const [error, setError] = useState<string | null>(null);
  const [isRedirecting, setIsRedirecting] = useState(false);

  useEffect(() => {
    // Call server API to create checkout session and redirect
    const initiateCheckout = async () => {
      try {
        setIsRedirecting(true);
        // Call server-side API to create Polar checkout session
        const response = await fetch("/api/checkout/license-key");
        const data = await response.json();

        if (!response.ok) {
          throw new Error(data.error || "Failed to create checkout session");
        }

        // Redirect to Polar checkout
        if (data.checkoutUrl) {
          window.location.href = data.checkoutUrl;
        } else {
          throw new Error("No checkout URL returned");
        }
      } catch (err) {
        console.error("Checkout error:", err);
        setError(
          err instanceof Error
            ? err.message
            : "Failed to initiate checkout. Please try again or contact support.",
        );
        setIsRedirecting(false);
      }
    };

    initiateCheckout();
  }, [searchParams]);

  return (
    <div className="min-h-[60vh] flex items-center justify-center bg-gradient-to-br from-black via-slate-950 to-black py-12">
      <div className="max-w-md w-full mx-4">
        <div className="bg-white/10 backdrop-blur-lg rounded-2xl p-8 shadow-2xl border border-white/20">
          {error ? (
            // Error State
            <>
              <div className="text-center mb-6">
                <div className="inline-flex items-center justify-center w-16 h-16 rounded-full bg-red-500/20 mb-4">
                  <svg
                    className="w-8 h-8 text-red-400"
                    fill="none"
                    stroke="currentColor"
                    viewBox="0 0 24 24"
                  >
                    <path
                      d="M6 18L18 6M6 6l12 12"
                      strokeLinecap="round"
                      strokeLinejoin="round"
                      strokeWidth={2}
                    />
                  </svg>
                </div>
                <h1 className="text-2xl font-bold text-white mb-2">
                  Checkout Error
                </h1>
                <p className="text-gray-300">{error}</p>
              </div>
              <button
                className="w-full py-3 px-4 bg-gradient-to-r from-indigo-500 to-purple-500 text-white font-semibold rounded-lg hover:from-indigo-600 hover:to-purple-600 transition-all duration-200 shadow-lg hover:shadow-xl"
                onClick={() => window.location.reload()}
              >
                Try Again
              </button>
              <a
                className="block text-center mt-4 text-gray-300 hover:text-white transition-colors"
                href="/"
              >
                ← Back to Home
              </a>
            </>
          ) : (
            // Loading State
            <>
              <div className="text-center">
                <div className="inline-flex items-center justify-center w-16 h-16 rounded-full bg-indigo-500/20 mb-4">
                  <svg
                    className="animate-spin h-8 w-8 text-indigo-400"
                    fill="none"
                    viewBox="0 0 24 24"
                    xmlns="http://www.w3.org/2000/svg"
                  >
                    <circle
                      className="opacity-25"
                      cx="12"
                      cy="12"
                      r="10"
                      stroke="currentColor"
                      strokeWidth="4"
                    />
                    <path
                      className="opacity-75"
                      d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4zm2 5.291A7.962 7.962 0 014 12H0c0 3.042 1.135 5.824 3 7.938l3-2.647z"
                      fill="currentColor"
                    />
                  </svg>
                </div>
                <h1 className="text-2xl font-bold text-white mb-2">
                  {isRedirecting
                    ? "Redirecting to checkout..."
                    : "Preparing checkout..."}
                </h1>
                <p className="text-gray-300">
                  Please wait while we set up your secure checkout session.
                </p>
              </div>
            </>
          )}
        </div>
      </div>
    </div>
  );
}
