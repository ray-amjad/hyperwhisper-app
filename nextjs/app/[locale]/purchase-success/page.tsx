"use client";

import { useEffect, useState, Suspense } from "react";
import { m } from "framer-motion";
import { Button } from "@heroui/button";
import { CheckCircle, ArrowRight } from "lucide-react";
import { useSearchParams } from "next/navigation";
import { usePostHog } from "posthog-js/react";
import { useTranslations } from "next-intl";

import { Link } from "@/src/i18n/navigation";

function PurchaseSuccessContent() {
  const searchParams = useSearchParams();
  const posthog = usePostHog();
  const t = useTranslations("purchaseSuccess");
  const [checkoutId, setCheckoutId] = useState<string | null>(null);
  const [eventCaptured, setEventCaptured] = useState(false);

  // Credits purchased, passed through the Stripe success_url. Used only to
  // personalize the confirmation copy — never trusted for entitlement.
  const creditsParam = searchParams?.get("credits");
  const credits = creditsParam ? Number.parseInt(creditsParam, 10) : NaN;
  const hasCredits = Number.isFinite(credits) && credits > 0;

  useEffect(() => {
    // Support both Stripe (session_id) and legacy Polar (checkout_id) params
    const id =
      searchParams?.get("session_id") || searchParams?.get("checkout_id");

    if (id) {
      setCheckoutId(id);
    }
  }, [searchParams]);

  // Separate effect to capture purchase event only when PostHog is ready
  useEffect(() => {
    if (checkoutId && posthog && !eventCaptured) {
      posthog.capture("purchase_completed", { session_id: checkoutId });
      setEventCaptured(true);
    }
  }, [checkoutId, posthog, eventCaptured]);

  const steps = [
    { title: t("step1Title"), description: t("step1Desc") },
    { title: t("step2Title"), description: t("step2Desc") },
    { title: t("step3Title"), description: t("step3Desc") },
  ];

  return (
    <div className="min-h-screen bg-gradient-to-b from-gray-900 via-purple-900/10 to-gray-900 flex items-center justify-center px-7 py-20">
      <m.div
        animate={{ opacity: 1, scale: 1 }}
        className="max-w-xl w-full text-center"
        initial={{ opacity: 0, scale: 0.95 }}
        transition={{ duration: 0.5 }}
      >
        <m.div
          animate={{ scale: 1 }}
          className="inline-flex items-center justify-center w-[82px] h-[82px] bg-gradient-to-r from-green-500 to-emerald-500 rounded-full mb-9 shadow-[0_12px_40px_-8px_rgba(16,185,129,0.5)]"
          initial={{ scale: 0.9 }}
          transition={{ duration: 0.5, delay: 0.2 }}
        >
          <CheckCircle className="w-11 h-11 text-white" />
        </m.div>

        <m.h1
          animate={{ opacity: 1, y: 0 }}
          className="text-4xl font-extrabold tracking-tight bg-gradient-to-r from-white to-gray-400 bg-clip-text text-transparent"
          initial={{ opacity: 0, y: 20 }}
          transition={{ duration: 0.5, delay: 0.3 }}
        >
          {t("title")}
        </m.h1>

        <m.p
          animate={{ opacity: 1, y: 0 }}
          className="text-base text-gray-400 leading-relaxed mt-[18px] mx-auto max-w-[480px]"
          initial={{ opacity: 0, y: 20 }}
          transition={{ duration: 0.5, delay: 0.4 }}
        >
          {hasCredits ? t("subtitle", { credits }) : t("subtitleGeneric")}
        </m.p>

        <m.div
          animate={{ opacity: 1, y: 0 }}
          className="text-left bg-zinc-800/40 rounded-2xl p-7 mt-10"
          initial={{ opacity: 0, y: 20 }}
          transition={{ duration: 0.5, delay: 0.5 }}
        >
          <h2 className="text-xs font-semibold uppercase tracking-wider text-gray-500 mb-[22px]">
            {t("whatsNext")}
          </h2>
          <ul className="space-y-6">
            {steps.map((step, i) => (
              <li key={i} className="flex gap-4">
                <span className="flex-none w-[26px] h-[26px] rounded-lg bg-purple-500/15 text-purple-400 text-[13px] font-bold flex items-center justify-center">
                  {i + 1}
                </span>
                <div>
                  <p className="text-[15px] font-semibold text-gray-200 leading-snug">
                    {step.title}
                  </p>
                  <p className="text-[13.5px] text-gray-500 mt-1.5 leading-relaxed">
                    {step.description}
                  </p>
                </div>
              </li>
            ))}
          </ul>
        </m.div>

        <m.div
          animate={{ opacity: 1, y: 0 }}
          className="mt-9"
          initial={{ opacity: 0, y: 20 }}
          transition={{ duration: 0.5, delay: 0.6 }}
        >
          <Button
            as={Link}
            className="w-full bg-gradient-to-r from-purple-600 to-blue-600 text-white font-semibold"
            href="/user/dashboard"
            size="lg"
          >
            {t("manageButton")}
            <ArrowRight className="w-[18px] h-[18px] ml-2" />
          </Button>
        </m.div>
      </m.div>
    </div>
  );
}

function LoadingFallback() {
  const t = useTranslations("purchaseSuccess");

  return (
    <div className="min-h-screen bg-gradient-to-b from-gray-900 via-purple-900/10 to-gray-900 flex items-center justify-center px-7 py-20">
      <div className="text-center">
        <div className="inline-flex items-center justify-center w-[82px] h-[82px] bg-gradient-to-r from-purple-600 to-blue-600 rounded-full mb-6 animate-pulse" />
        <p className="text-lg text-gray-400">{t("loading")}</p>
      </div>
    </div>
  );
}

export default function PurchaseSuccessPage() {
  return (
    <Suspense fallback={<LoadingFallback />}>
      <PurchaseSuccessContent />
    </Suspense>
  );
}
