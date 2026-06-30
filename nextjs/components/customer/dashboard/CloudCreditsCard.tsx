"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";

import {
  MIN_CREDIT_DOLLARS,
  MAX_CREDIT_DOLLARS,
  CREDITS_PER_DOLLAR,
  validateCreditPurchaseAmount,
} from "@/app/api/checkout/credits/validation";

interface CloudCreditsCardProps {
  totalCredits: number;
  totalMinutesRemaining: number;
  creditsPerMinute: number;
  /** License key to use for purchasing credits (first active license) */
  activeLicenseKey: string | null;
}

const CREDIT_TIERS = [
  { amount: 5, credits: 5 * CREDITS_PER_DOLLAR },
  { amount: 10, credits: 10 * CREDITS_PER_DOLLAR },
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
  const t = useTranslations("cloudCreditsCard");
  // loadingTier holds the dollar amount of the in-flight checkout, or the
  // sentinel "custom" while the custom-amount checkout is being created.
  const [loadingTier, setLoadingTier] = useState<number | "custom" | null>(
    null
  );
  const [showCustom, setShowCustom] = useState(false);
  const [customAmount, setCustomAmount] = useState("");

  const getMinutesForCredits = (credits: number) =>
    creditsPerMinute > 0 ? Math.floor(credits / creditsPerMinute) : null;

  const customValid =
    validateCreditPurchaseAmount(Number(customAmount)) === null;

  const handleBuyCredits = async (
    amount: number,
    tier: number | "custom" = amount
  ) => {
    if (!activeLicenseKey) return;

    setLoadingTier(tier);
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
        <p className="text-sm text-gray-400 mb-1">{t("title")}</p>
        <p className="text-2xl font-semibold text-white">
          {totalCredits.toLocaleString()}
        </p>
        {totalMinutesRemaining > 0 && (
          <p className="text-sm text-gray-400 mt-0.5">
            {t("minutesRemaining", { minutes: totalMinutesRemaining })}
          </p>
        )}
      </div>

      {activeLicenseKey && (
        <>
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
                      <span className="text-lg font-semibold">
                        ${tier.amount}
                      </span>
                      <span className="text-xs text-gray-400">
                        {t("creditsCount", { count: tier.credits })}
                      </span>
                      {minutes && (
                        <span className="text-xs text-gray-500">
                          {t("minutes", { minutes })}
                        </span>
                      )}
                    </>
                  )}
                </button>
              );
            })}

            {/* Custom amount: toggles an inline input below the tier grid. */}
            <button
              onClick={() => setShowCustom((v) => !v)}
              disabled={loadingTier !== null}
              aria-pressed={showCustom}
              className={`flex flex-col items-center justify-center px-3 py-3 border text-white font-medium rounded-lg disabled:opacity-50 disabled:cursor-not-allowed cursor-pointer transition-colors ${
                showCustom
                  ? "bg-white/10 border-white/30"
                  : "bg-white/5 border-white/10 hover:bg-white/10 hover:border-white/20"
              }`}
            >
              <span className="text-lg font-semibold">{t("custom")}</span>
              <span className="text-xs text-gray-400">{t("customSub")}</span>
            </button>
          </div>

          {showCustom && (
            <div className="mt-2 flex items-center gap-2">
              <div className="relative flex-1">
                <span className="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-gray-400">
                  $
                </span>
                <input
                  type="number"
                  inputMode="numeric"
                  min={MIN_CREDIT_DOLLARS}
                  max={MAX_CREDIT_DOLLARS}
                  step={1}
                  value={customAmount}
                  onChange={(e) => setCustomAmount(e.target.value)}
                  placeholder={`${MIN_CREDIT_DOLLARS}–${MAX_CREDIT_DOLLARS}`}
                  disabled={loadingTier !== null}
                  className="w-full rounded-lg border border-white/10 bg-white/5 py-2 pl-7 pr-3 text-white placeholder:text-gray-500 focus:border-white/30 focus:outline-none disabled:opacity-50"
                />
              </div>
              <button
                onClick={() =>
                  handleBuyCredits(Number(customAmount), "custom")
                }
                disabled={!customValid || loadingTier !== null}
                className="flex items-center justify-center rounded-lg bg-white/10 border border-white/20 px-4 py-2 font-medium text-white hover:bg-white/20 disabled:opacity-50 disabled:cursor-not-allowed cursor-pointer transition-colors"
              >
                {loadingTier === "custom" ? (
                  <div className="w-5 h-5 border-2 border-white border-t-transparent rounded-full animate-spin" />
                ) : (
                  t("topUp")
                )}
              </button>
            </div>
          )}
        </>
      )}
    </div>
  );
}
