"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { usePostHog } from "posthog-js/react";

import CloudCreditsCardView, {
  type CloudCreditsTierView,
} from "./CloudCreditsCardView";

import {
  MIN_CREDIT_DOLLARS,
  MAX_CREDIT_DOLLARS,
  CREDITS_PER_DOLLAR,
  validateCreditPurchaseAmount,
} from "@/app/api/checkout/credits/validation";
import {
  createBuyCreditsHandler,
  type BuyCreditsTier,
} from "@/src/lib/buy-credits";

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
 *
 * This is the STATEFUL half only. Every byte it renders lives in
 * `CloudCreditsCardView`, which holds no hooks — see the note there for why the
 * split exists (#737): a hook-free view can be called as a function in a test,
 * so the tier buttons' `onClick`, the busy state and the failed state are all
 * reachable, and none of them are reachable through `renderToStaticMarkup`.
 *
 * Because the View can call no hook, this half also PRE-FORMATS every
 * translated string, including the parameterised per-tier ones, and hands them
 * down as plain strings.
 */
export default function CloudCreditsCard({
  totalCredits,
  totalMinutesRemaining,
  creditsPerMinute,
  activeLicenseKey,
}: CloudCreditsCardProps) {
  const t = useTranslations("cloudCreditsCard");
  // The two failure strings are the sibling `/credits` form's, reused verbatim:
  // they already exist, already translated, in all 40 files under `messages/`.
  // #737 asked for a new key; this declines that and adds none.
  const tBuyCredits = useTranslations("buyCredits");
  const posthog = usePostHog();
  // loadingTier holds the dollar amount of the in-flight checkout, or the
  // sentinel "custom" while the custom-amount checkout is being created.
  const [loadingTier, setLoadingTier] = useState<BuyCreditsTier | null>(null);
  // The message from a checkout the server refused, or `null`. Before #737
  // this state did not exist and a refusal was shown to nobody.
  const [error, setError] = useState<string | null>(null);
  const [showCustom, setShowCustom] = useState(false);
  const [customAmount, setCustomAmount] = useState("");

  const getMinutesForCredits = (credits: number) =>
    creditsPerMinute > 0 ? Math.floor(credits / creditsPerMinute) : null;

  const customValid =
    validateCreditPurchaseAmount(Number(customAmount)) === null;

  const tiers: CloudCreditsTierView[] = CREDIT_TIERS.map((tier) => {
    const minutes = getMinutesForCredits(tier.credits);

    return {
      amount: tier.amount,
      creditsLabel: t("creditsCount", { count: tier.credits }),
      minutesLabel: minutes ? t("minutes", { minutes }) : null,
    };
  });

  // Built in the render body, not in a `useEffect` and not inline in `onClick`.
  // Two things depend on that: the wiring is visible to a test that can render
  // this component but cannot click it (`tests/user-header-sign-out.test.ts` is
  // the precedent), and the `setError` below is reachable during this
  // component's own render, which is the only way this repo can prove a
  // refusal message actually reaches the screen — see
  // `tests/cloud-credits-card-error-state.test.ts`. Move this call and that
  // test goes red. Every rule about when to navigate, what to show and when to
  // clear the busy flag lives in the factory — see `src/lib/buy-credits.ts`.
  const handleBuyCredits = createBuyCreditsHandler({
    // Nullable, and passed straight through: the factory owns the no-licence
    // refusal so no component can forget it.
    licenseKey: activeLicenseKey,
    // NOT the bare global `fetch`. Passed as a value it loses its `this` and
    // throws `Illegal invocation` in the browser.
    fetchImpl: (input, init) => fetch(input, init),
    // The only `window` in this flow. The seam must never grow one.
    navigate: (destination) => {
      window.location.href = destination;
    },
    // `usePostHog` is typed non-nullable but the provider is not mounted when
    // `NEXT_PUBLIC_POSTHOG_KEY` is absent, so the value really can be missing
    // at runtime — the same guard as `app/[locale]/purchase-success/page.tsx`.
    // The properties come from the seam, which keeps the licence key out of
    // them: this app's PostHog init has no redaction (#739).
    reportError: (thrown, properties) => {
      if (posthog) posthog.captureException(thrown, properties);
    },
    setBusy: setLoadingTier,
    setError,
    checkoutErrorMessage: tBuyCredits("errorCheckout"),
    genericErrorMessage: tBuyCredits("errorGeneric"),
  });

  return (
    <CloudCreditsCardView
      activeLicenseKey={activeLicenseKey}
      customAmount={customAmount}
      customValid={customValid}
      error={error}
      labels={{
        title: t("title"),
        minutesRemaining: t("minutesRemaining", {
          minutes: totalMinutesRemaining,
        }),
        custom: t("custom"),
        customSub: t("customSub"),
        topUp: t("topUp"),
      }}
      loadingTier={loadingTier}
      maxAmount={MAX_CREDIT_DOLLARS}
      minAmount={MIN_CREDIT_DOLLARS}
      showCustom={showCustom}
      tiers={tiers}
      totalCredits={totalCredits}
      totalMinutesRemaining={totalMinutesRemaining}
      onBuy={(amount, tier) => {
        void handleBuyCredits(amount, tier);
      }}
      onCustomAmountChange={setCustomAmount}
      onToggleCustom={() => setShowCustom((v) => !v)}
    />
  );
}
