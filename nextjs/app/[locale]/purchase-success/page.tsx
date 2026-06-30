"use client";

import { useEffect, useState, Suspense } from "react";
import { m } from "framer-motion";
import { Card, CardBody } from "@heroui/card";
import { Button } from "@heroui/button";
import { CheckCircle, Mail, ArrowRight } from "lucide-react";
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

  useEffect(() => {
    // Support both Stripe (session_id) and legacy Polar (checkout_id) params
    const id = searchParams?.get("session_id") || searchParams?.get("checkout_id");

    if (id) {
      setCheckoutId(id);
    }
  }, [searchParams]);

  // Separate effect to capture purchase event only when PostHog is ready
  useEffect(() => {
    // Only capture once, when we have both session ID and posthog is initialized
    if (checkoutId && posthog && !eventCaptured) {
      console.log("Capturing purchase_completed event:", checkoutId);
      posthog.capture("purchase_completed", { session_id: checkoutId });
      setEventCaptured(true);
    }
  }, [checkoutId, posthog, eventCaptured]);

  return (
    <div className="min-h-screen bg-gradient-to-b from-gray-900 via-purple-900/10 to-gray-900 flex items-center justify-center px-6 py-20">
      <m.div
        animate={{ opacity: 1, scale: 1 }}
        className="max-w-2xl w-full"
        initial={{ opacity: 0, scale: 0.95 }}
        transition={{ duration: 0.5 }}
      >
        <div className="text-center mb-8">
          <m.div
            animate={{ scale: 1 }}
            className="inline-flex items-center justify-center w-20 h-20 bg-gradient-to-r from-green-500 to-emerald-500 rounded-full mb-6"
            initial={{ scale: 0.95 }}
            transition={{ duration: 0.5, delay: 0.2 }}
          >
            <CheckCircle className="w-12 h-12 text-white" />
          </m.div>

          <m.h1
            animate={{ opacity: 1, y: 0 }}
            className="text-4xl md:text-5xl font-bold mb-4 bg-gradient-to-r from-white to-gray-400 bg-clip-text text-transparent"
            initial={{ opacity: 0, y: 20 }}
            transition={{ duration: 0.5, delay: 0.3 }}
          >
            {t("title")}
          </m.h1>

          <m.p
            animate={{ opacity: 1, y: 0 }}
            className="text-lg text-gray-400"
            initial={{ opacity: 0, y: 20 }}
            transition={{ duration: 0.5, delay: 0.4 }}
          >
            {t("subtitle")}
          </m.p>
        </div>

        <m.div
          animate={{ opacity: 1, y: 0 }}
          initial={{ opacity: 0, y: 20 }}
          transition={{ duration: 0.5, delay: 0.5 }}
        >
          <Card className="bg-gray-900/50 backdrop-blur-xl border-gray-800 mb-6">
            <CardBody className="p-8">
              <div className="flex items-start gap-4 mb-6">
                <div className="p-3 bg-purple-900/30 rounded-lg">
                  <Mail className="w-6 h-6 text-purple-400" />
                </div>
                <div className="flex-1">
                  <h2 className="text-xl font-semibold text-white mb-2">
                    {t("checkEmail.title")}
                  </h2>
                  <p className="text-gray-400 mb-4">
                    {t("checkEmail.description")}
                  </p>
                </div>
              </div>

              <div className="bg-gray-800/50 rounded-lg p-4 mb-6">
                <h3 className="text-sm font-semibold text-gray-400 mb-3">
                  {t("whatsNext.title")}
                </h3>
                <ul className="space-y-3">
                  <li className="flex items-start gap-3">
                    <span className="text-purple-400 mt-1">1.</span>
                    <div>
                      <p className="text-gray-300 font-medium">
                        {t("whatsNext.step1.title")}
                      </p>
                      <p className="text-gray-500 text-sm">
                        {t("whatsNext.step1.description")}
                      </p>
                    </div>
                  </li>
                  <li className="flex items-start gap-3">
                    <span className="text-purple-400 mt-1">2.</span>
                    <div>
                      <p className="text-gray-300 font-medium">
                        {t("whatsNext.step2.title")}
                      </p>
                      <p className="text-gray-500 text-sm">
                        {t("whatsNext.step2.description")}
                      </p>
                    </div>
                  </li>
                  <li className="flex items-start gap-3">
                    <span className="text-purple-400 mt-1">3.</span>
                    <div>
                      <p className="text-gray-300 font-medium">
                        {t("whatsNext.step3.title")}
                      </p>
                      <p className="text-gray-500 text-sm">
                        {t("whatsNext.step3.description")}
                      </p>
                    </div>
                  </li>
                </ul>
              </div>

            </CardBody>
          </Card>
        </m.div>

        <m.div
          animate={{ opacity: 1, y: 0 }}
          className="space-y-4"
          initial={{ opacity: 0, y: 20 }}
          transition={{ duration: 0.5, delay: 0.6 }}
        >
          <Button
            className="w-full bg-gradient-to-r from-purple-600 to-blue-600 text-white"
            size="lg"
            onClick={() => {
              window.location.href = "/api/download";
            }}
          >
            {t("downloadButton")}
            <ArrowRight className="w-5 h-5 ml-2" />
          </Button>

          <Button
            as={Link}
            className="w-full border-gray-700 text-gray-400"
            href="/"
            size="lg"
            variant="bordered"
          >
            {t("returnHome")}
          </Button>
        </m.div>

        <m.div
          animate={{ opacity: 1 }}
          className="text-center mt-8"
          initial={{ opacity: 0 }}
          transition={{ duration: 0.5, delay: 0.7 }}
        >
          <p className="text-gray-500 text-sm">
            {t("noEmail")}{" "}
            <Link
              className="text-purple-400 hover:text-purple-300"
              href="/support"
            >
              {t("contactSupport")}
            </Link>
          </p>
        </m.div>
      </m.div>
    </div>
  );
}

function LoadingFallback() {
  const t = useTranslations("purchaseSuccess");

  return (
    <div className="min-h-screen bg-gradient-to-b from-gray-900 via-purple-900/10 to-gray-900 flex items-center justify-center px-6 py-20">
      <div className="text-center">
        <div className="inline-flex items-center justify-center w-20 h-20 bg-gradient-to-r from-purple-600 to-blue-600 rounded-full mb-6 animate-pulse" />
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
