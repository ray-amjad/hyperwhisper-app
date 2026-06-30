"use client";

import { useState } from "react";

interface CloudCreditsCardProps {
  totalCredits: number;
  totalMinutesRemaining: number;
  creditsPerMinute: number;
  /** License key to use for purchasing credits (first active license) */
  activeLicenseKey: string | null;
}

const CREDIT_TIERS = [
  { amount: 5, credits: 5000 },
  { amount: 10, credits: 10000 },
  { amount: 20, credits: 20000 },
] as const;

/**
 * Cloud Credits Card
 *
 * Displays total cloud credits across all licenses and buy credits buttons.
 */
export default function CloudCreditsCard({
  totalCredits,
  totalMinutesRemaining,
  creditsPerMinute,
  activeLicenseKey,
}: CloudCreditsCardProps) {
  const [loadingTier, setLoadingTier] = useState<number | null>(null);

  const getMinutesForCredits = (credits: number) =>
    creditsPerMinute > 0 ? Math.floor(credits / creditsPerMinute) : null;

  const handleBuyCredits = async (amount: number) => {
    if (!activeLicenseKey) return;

    setLoadingTier(amount);
    try {
      const response = await fetch("/api/checkout/credits", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ licenseKey: activeLicenseKey, amount }),
      });
      const data = await response.json();
      if (data.checkoutUrl) {
        window.location.href = data.checkoutUrl;
      }
    } catch (err) {
      console.error("Failed to create checkout:", err);
    } finally {
      setLoadingTier(null);
    }
  };

  return (
    <div className="bg-white/5 rounded-xl border border-white/10 p-5">
      <div className="mb-4">
        <p className="text-sm text-gray-400 mb-1">Cloud Credits</p>
        <p className="text-2xl font-semibold text-white">
          {totalCredits.toLocaleString()}
        </p>
        {totalMinutesRemaining > 0 && (
          <p className="text-sm text-gray-400 mt-0.5">
            ~{totalMinutesRemaining} minutes remaining
          </p>
        )}
      </div>

      {activeLicenseKey && (
        <div className="grid grid-cols-3 gap-2">
          {CREDIT_TIERS.map((tier) => {
            const minutes = getMinutesForCredits(tier.credits);
            const isLoading = loadingTier === tier.amount;
            const isDisabled = loadingTier !== null;

            return (
              <button
                key={tier.amount}
                onClick={() => handleBuyCredits(tier.amount)}
                disabled={isDisabled}
                className="flex flex-col items-center justify-center px-3 py-3 bg-white/5 border border-white/10 text-white font-medium rounded-lg hover:bg-white/10 hover:border-white/20 disabled:opacity-50 disabled:cursor-not-allowed cursor-pointer transition-colors"
              >
                {isLoading ? (
                  <div className="w-5 h-5 border-2 border-white border-t-transparent rounded-full animate-spin" />
                ) : (
                  <>
                    <span className="text-lg font-semibold">${tier.amount}</span>
                    <span className="text-xs text-gray-400">
                      {tier.credits.toLocaleString()} credits
                    </span>
                    {minutes && (
                      <span className="text-xs text-gray-500">~{minutes} min</span>
                    )}
                  </>
                )}
              </button>
            );
          })}
        </div>
      )}
    </div>
  );
}
