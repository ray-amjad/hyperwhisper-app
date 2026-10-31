"use client";

import { useEffect, useState, Suspense } from "react";
import { Button } from "@heroui/button";
import { CheckCircle, ArrowRight } from "lucide-react";
import { useSearchParams } from "next/navigation";
import { useTranslations } from "next-intl";

import { BTN_LG, BTN_WHITE } from "@/components/landing/dusk";
import {
  GLASS,
  ICON_TILE,
  LABEL,
  SECTION,
} from "@/components/landing/dusk-pages";
import { DuskPage, PageHero, delay } from "@/components/landing/DuskPage";
import { Link } from "@/src/i18n/navigation";
import { loadPostHog } from "@/src/lib/posthog-client";

function PurchaseSuccessContent() {
  const searchParams = useSearchParams();
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

  // Separate effect to capture the purchase once PostHog is loaded and
  // initialised. `loadPostHog` answers null when no key is set.
  useEffect(() => {
    if (!checkoutId || eventCaptured) return;
    let cancelled = false;

    const capture = async () => {
      const posthog = await loadPostHog();

      if (cancelled || !posthog) return;
      posthog.capture("purchase_completed", { session_id: checkoutId });
      setEventCaptured(true);
    };

    void capture();

    return () => {
      cancelled = true;
    };
  }, [checkoutId, eventCaptured]);

  const steps = [
    { title: t("step1Title"), description: t("step1Desc") },
    { title: t("step2Title"), description: t("step2Desc") },
    { title: t("step3Title"), description: t("step3Desc") },
  ];

  return (
    <DuskPage>
      <PageHero
        eyebrow={
          <span className="inline-grid size-[64px] place-items-center rounded-full border border-[rgba(196,181,253,.35)] bg-[linear-gradient(180deg,rgba(129,90,220,.28),rgba(255,255,255,.04))] text-[var(--dusk-lav)] shadow-[0_12px_40px_-10px_rgba(129,90,220,.6)] backdrop-blur-[14px]">
            <CheckCircle aria-hidden="true" className="size-8" />
          </span>
        }
        lead={hasCredits ? t("subtitle", { credits }) : t("subtitleGeneric")}
        title={t("title")}
      />

      <section className={SECTION}>
        <div className="mx-auto w-full max-w-[640px] px-6 md:px-8">
          <div
            className={`dusk-enter ${GLASS} p-6 text-start backdrop-blur-[14px] md:p-8`}
            style={delay(320)}
          >
            <h2 className={`${LABEL} mb-[22px]`}>{t("whatsNext")}</h2>
            <ul className="space-y-6">
              {steps.map((step, i) => (
                <li key={i} className="flex gap-4">
                  <span
                    className={`${ICON_TILE} text-[13px] font-semibold tabular-nums`}
                  >
                    {i + 1}
                  </span>
                  <div>
                    <p className="text-[15px] font-semibold leading-snug text-white">
                      {step.title}
                    </p>
                    <p className="mt-1.5 text-[14px] leading-relaxed text-[var(--dusk-mute)]">
                      {step.description}
                    </p>
                  </div>
                </li>
              ))}
            </ul>
          </div>

          <div className="dusk-enter mt-8" style={delay(400)}>
            <Button
              as={Link}
              className={`${BTN_WHITE} ${BTN_LG} h-auto w-full min-w-0`}
              href="/user/dashboard"
              role="link"
              size="lg"
            >
              {t("manageButton")}
              <ArrowRight className="rtl:-scale-x-100" />
            </Button>
          </div>
        </div>
      </section>
    </DuskPage>
  );
}

function LoadingFallback() {
  const t = useTranslations("purchaseSuccess");

  return (
    <DuskPage className="flex items-center justify-center py-20">
      <div className="text-center" role="status">
        <div
          aria-hidden="true"
          className="mx-auto mb-6 size-[64px] animate-pulse rounded-full border border-[var(--dusk-line)] bg-[var(--dusk-glass)] motion-reduce:animate-none"
        />
        <p className="text-lg text-[var(--dusk-mute)]">{t("loading")}</p>
      </div>
    </DuskPage>
  );
}

export default function PurchaseSuccessPage() {
  return (
    <Suspense fallback={<LoadingFallback />}>
      <PurchaseSuccessContent />
    </Suspense>
  );
}
