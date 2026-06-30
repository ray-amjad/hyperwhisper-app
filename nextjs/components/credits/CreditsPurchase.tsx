"use client";

import { useMemo, useState } from "react";
import { Card, CardBody } from "@heroui/card";
import { Button } from "@heroui/button";
import { Input } from "@heroui/input";
import { m } from "framer-motion";
import { Cloud, Wallet, ShieldCheck, Layers, Check } from "lucide-react";

import {
  MIN_CREDIT_DOLLARS,
  MAX_CREDIT_DOLLARS,
  CREDITS_PER_DOLLAR,
  validateCreditPurchaseAmount,
  computeCreditPurchase,
} from "@/app/api/checkout/credits/validation";

const PRESETS = [5, 10] as const;

const EMAIL_RE = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

/**
 * Guest buy-credits UI for /credits.
 *
 * Visitor enters an email, picks a preset ($5 / $10) or a custom whole-dollar
 * amount, and we POST to /api/checkout/credits (mint path — no license key).
 * Stripe collects payment, the webhook mints a key and emails it to them.
 *
 * Copy is intentionally hardcoded English: the site has 41 locale files with no
 * missing-key fallback, so new i18n keys would render raw paths for every other
 * language. Translation is tracked as a follow-up.
 */
export default function CreditsPurchase() {
  const [email, setEmail] = useState("");
  const [amount, setAmount] = useState<number>(PRESETS[0]);
  const [customAmount, setCustomAmount] = useState("");
  const [isCustom, setIsCustom] = useState(false);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const effectiveAmount = isCustom ? Number(customAmount) : amount;

  // Single source of truth for bounds — same validator the API route enforces,
  // so the client preview can't accept/reject a different amount than the server.
  const amountValid = validateCreditPurchaseAmount(effectiveAmount) === null;

  const emailValid = EMAIL_RE.test(email.trim());

  const { credits, feeUsd, totalUsd } = useMemo(() => {
    if (!amountValid) return { credits: 0, feeUsd: 0, totalUsd: 0 };
    // Reuse the server's pricing math (cents) so the preview matches the charge.
    const { creditAmount, creditCents, feeCents } =
      computeCreditPurchase(effectiveAmount);
    return {
      credits: creditAmount,
      feeUsd: feeCents / 100,
      totalUsd: (creditCents + feeCents) / 100,
    };
  }, [amountValid, effectiveAmount]);

  const selectPreset = (value: number) => {
    setIsCustom(false);
    setAmount(value);
    setError(null);
  };

  const handleCheckout = async () => {
    // Guard against double-submit (rapid clicks create duplicate checkout
    // sessions / charges); the button is also disabled while loading.
    if (loading) return;
    setError(null);

    if (!emailValid) {
      setError("Enter a valid email address.");
      return;
    }
    if (!amountValid) {
      setError(
        `Choose a whole-dollar amount between $${MIN_CREDIT_DOLLARS} and $${MAX_CREDIT_DOLLARS}.`
      );
      return;
    }

    setLoading(true);
    try {
      const response = await fetch("/api/checkout/credits", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ email: email.trim(), amount: effectiveAmount }),
      });
      const data = await response.json();

      if (response.ok && data.checkoutUrl) {
        window.location.href = data.checkoutUrl;
        return;
      }

      setError(data.error || "Could not start checkout. Please try again.");
      setLoading(false);
    } catch {
      setError("Something went wrong. Please try again.");
      setLoading(false);
    }
  };

  return (
    <div className="max-w-xl mx-auto">
      <m.div
        initial={{ opacity: 0, y: 20 }}
        animate={{ opacity: 1, y: 0 }}
        transition={{ duration: 0.5 }}
      >
        <div className="text-center mb-10">
          <span className="text-purple-400 text-sm font-semibold tracking-widest uppercase">
            Cloud
          </span>
          <h1 className="text-4xl md:text-5xl font-bold mt-3 mb-4 bg-gradient-to-r from-white to-gray-400 bg-clip-text text-transparent">
            Add Cloud Credits
          </h1>
          <p className="text-lg text-gray-400">
            One balance, every provider. No API keys to manage. Pay only for what
            you transcribe.
          </p>
        </div>

        <Card className="bg-gradient-to-b from-purple-900/20 to-blue-900/20 border-purple-700 backdrop-blur-xl">
          <CardBody className="p-6 md:p-8">
            {/* Email */}
            <label className="block text-sm font-medium text-gray-300 mb-2">
              Your email
            </label>
            <Input
              type="email"
              value={email}
              onChange={(e) => setEmail(e.target.value)}
              placeholder="you@example.com"
              variant="bordered"
              size="lg"
              classNames={{
                inputWrapper:
                  "bg-gray-900/60 border-gray-700 data-[hover=true]:border-gray-600 group-data-[focus=true]:border-purple-500",
                input: "text-white",
              }}
            />
            <p className="text-xs text-gray-500 mt-2">
              We email your license key and credit balance here after payment.
            </p>

            {/* Amount */}
            <div className="mt-7">
              <span className="block text-sm font-medium text-gray-300 mb-3">
                Choose an amount
              </span>
              <div className="grid grid-cols-3 gap-3">
                {PRESETS.map((value) => {
                  const selected = !isCustom && amount === value;
                  return (
                    <button
                      key={value}
                      type="button"
                      onClick={() => selectPreset(value)}
                      className={`rounded-2xl py-4 text-center transition cursor-pointer border ${
                        selected
                          ? "bg-gradient-to-b from-purple-600/30 to-blue-600/20 border-purple-500"
                          : "bg-gray-900/50 border-gray-800 hover:border-gray-700"
                      }`}
                    >
                      <div className="text-2xl font-bold text-white">
                        ${value}
                      </div>
                      <div className="text-xs text-gray-400 mt-1">
                        {(value * CREDITS_PER_DOLLAR).toLocaleString()} credits
                      </div>
                    </button>
                  );
                })}
                <button
                  type="button"
                  onClick={() => setIsCustom(true)}
                  className={`rounded-2xl py-4 text-center transition cursor-pointer border ${
                    isCustom
                      ? "bg-gradient-to-b from-purple-600/30 to-blue-600/20 border-purple-500"
                      : "bg-gray-900/50 border-gray-800 hover:border-gray-700"
                  }`}
                >
                  <div className="text-2xl font-bold text-white">Custom</div>
                  <div className="text-xs text-gray-400 mt-1">your amount</div>
                </button>
              </div>

              {isCustom && (
                <div className="mt-3">
                  <Input
                    type="number"
                    value={customAmount}
                    onChange={(e) => {
                      setCustomAmount(e.target.value);
                      setError(null);
                    }}
                    placeholder={`Amount in USD (${MIN_CREDIT_DOLLARS} to ${MAX_CREDIT_DOLLARS})`}
                    variant="bordered"
                    size="lg"
                    min={MIN_CREDIT_DOLLARS}
                    max={MAX_CREDIT_DOLLARS}
                    step={1}
                    startContent={<span className="text-gray-400">$</span>}
                    classNames={{
                      inputWrapper:
                        "bg-gray-900/60 border-gray-700 data-[hover=true]:border-gray-600 group-data-[focus=true]:border-purple-500",
                      input: "text-white",
                    }}
                  />
                </div>
              )}
            </div>

            {/* Summary */}
            <div className="mt-7 rounded-2xl border border-gray-800 bg-black/30 p-4 text-sm">
              <div className="flex justify-between text-gray-400 mb-2">
                <span>Credits</span>
                <span className="text-gray-200">
                  {credits.toLocaleString()}
                </span>
              </div>
              <div className="flex justify-between text-gray-400 mb-2">
                <span>Processing fee (6%, non-refundable)</span>
                <span className="text-gray-200">${feeUsd.toFixed(2)}</span>
              </div>
              <div className="flex justify-between font-semibold text-white border-t border-gray-800 pt-2 mt-2">
                <span>Total today</span>
                <span>${totalUsd.toFixed(2)}</span>
              </div>
            </div>

            {error && (
              <p className="text-sm text-red-400 mt-4" role="alert">
                {error}
              </p>
            )}

            <Button
              className="w-full bg-gradient-to-r from-purple-600 to-blue-600 text-white font-semibold mt-6"
              size="lg"
              isLoading={loading}
              isDisabled={loading || !emailValid || !amountValid}
              onPress={handleCheckout}
            >
              {loading ? "Starting checkout" : "Continue to checkout"}
            </Button>

            <p className="text-xs text-gray-500 text-center mt-3">
              Secure checkout via Stripe. Credits never expire for a year from
              purchase.
            </p>
          </CardBody>
        </Card>

        {/* Reassurance strip */}
        <div className="grid sm:grid-cols-3 gap-3 mt-6 text-sm">
          <div className="flex items-center gap-2 text-gray-400">
            <Layers className="w-4 h-4 text-purple-300 shrink-0" />9+ providers,
            one balance
          </div>
          <div className="flex items-center gap-2 text-gray-400">
            <Wallet className="w-4 h-4 text-purple-300 shrink-0" />
            No subscription
          </div>
          <div className="flex items-center gap-2 text-gray-400">
            <ShieldCheck className="w-4 h-4 text-purple-300 shrink-0" />
            Opted out of training
          </div>
        </div>

        <div className="flex items-center justify-center gap-2 text-xs text-gray-600 mt-8">
          <Cloud className="w-3.5 h-3.5" />
          <Check className="w-3.5 h-3.5 text-green-600" />
          Already have a key? Top up from your dashboard after signing in.
        </div>
      </m.div>
    </div>
  );
}
