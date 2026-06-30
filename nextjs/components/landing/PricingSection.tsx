"use client";

import { useState } from "react";
import { Card, CardBody } from "@heroui/card";
import { Button } from "@heroui/button";
import { m } from "framer-motion";
import { Check, X, Sparkles, Building2, Cloud, KeyRound } from "lucide-react";
import { useTranslations } from "next-intl";

import EnterpriseContactModal from "./EnterpriseContactModal";

import { Link } from "@/src/i18n/navigation";
import { useDownloadModal } from "@/contexts/DownloadModalContext";

/**
 * "Two ways to go cloud" pricing section.
 *
 * Replaces the old lifetime-license pricing table. Two paths: bring your own
 * API keys (free, but you do the legwork) or HyperWhisper Cloud Credits
 * (pay-as-you-go, zero setup) which routes to /credits. Copy lives under the
 * `cloudSection` namespace in messages/*.json.
 */
export default function PricingSection() {
  const { openModal } = useDownloadModal();
  const t = useTranslations("cloudSection");
  const [showEnterpriseModal, setShowEnterpriseModal] = useState(false);

  const byoFeatures = [
    { text: t("byo.features.signup"), included: false },
    { text: t("byo.features.rotate"), included: false },
    { text: t("byo.features.bill"), included: false },
    { text: t("byo.features.control"), included: true },
    { text: t("byo.features.free"), included: true },
  ];

  const cloudFeatures = [
    t("credits.features.noKeys"),
    t("credits.features.oneBalance"),
    t("credits.features.models"),
    t("credits.features.payPerUse"),
    t("credits.features.noTraining"),
  ];

  return (
    <section className="px-6 py-20" id="cloud">
      <m.div
        className="max-w-6xl mx-auto"
        initial={{ opacity: 0, y: 20 }}
        transition={{ duration: 0.5 }}
        viewport={{ once: true }}
        whileInView={{ opacity: 1, y: 0 }}
      >
        <div className="text-center mb-14">
          <span className="text-purple-400 text-sm font-semibold tracking-widest uppercase">
            {t("eyebrow")}
          </span>
          <h2 className="text-4xl md:text-5xl font-bold mt-3 mb-4 bg-gradient-to-r from-white to-gray-400 bg-clip-text text-transparent">
            {t("title")}
          </h2>
          <p className="text-lg text-gray-400 max-w-2xl mx-auto">
            {t("subtitle")}
          </p>
        </div>

        <div className="grid grid-cols-1 md:grid-cols-2 gap-6 max-w-4xl mx-auto">
          {/* Bring your own keys */}
          <m.div
            initial={{ opacity: 0, y: 20 }}
            transition={{ duration: 0.5 }}
            viewport={{ once: true }}
            whileInView={{ opacity: 1, y: 0 }}
          >
            <Card className="h-full bg-gray-900/50 border-gray-800 backdrop-blur-xl">
              <CardBody className="p-8 flex flex-col">
                <div className="flex items-center gap-2 mb-1">
                  <KeyRound className="w-5 h-5 text-gray-400" />
                  <h3 className="text-xl font-bold text-white">
                    {t("byo.title")}
                  </h3>
                </div>
                <p className="text-sm text-gray-500 mb-6">
                  {t("byo.subtitle")}
                </p>
                <ul className="space-y-3 mb-8">
                  {byoFeatures.map((feature) => (
                    <li
                      key={feature.text}
                      className="flex items-start gap-3 text-sm"
                    >
                      {feature.included ? (
                        <Check className="w-4 h-4 text-green-500 mt-0.5 flex-shrink-0" />
                      ) : (
                        <X className="w-4 h-4 text-gray-600 mt-0.5 flex-shrink-0" />
                      )}
                      <span
                        className={
                          feature.included ? "text-gray-300" : "text-gray-400"
                        }
                      >
                        {feature.text}
                      </span>
                    </li>
                  ))}
                </ul>
                <Button
                  className="w-full bg-gray-800 text-gray-200 border border-gray-700 mt-auto"
                  size="lg"
                  onPress={openModal}
                >
                  {t("byo.cta")}
                </Button>
              </CardBody>
            </Card>
          </m.div>

          {/* HyperWhisper Cloud Credits */}
          <m.div
            className="relative"
            initial={{ opacity: 0, y: 20 }}
            transition={{ duration: 0.5, delay: 0.1 }}
            viewport={{ once: true }}
            whileInView={{ opacity: 1, y: 0 }}
          >
            <div className="absolute -top-3 left-8 z-10">
              <div className="bg-gradient-to-r from-purple-600 to-blue-600 text-white px-3 py-1 rounded-full text-xs font-semibold flex items-center gap-1">
                <Sparkles className="w-3.5 h-3.5" />
                {t("credits.badge")}
              </div>
            </div>
            <Card className="h-full bg-gradient-to-b from-purple-900/20 to-blue-900/20 border-purple-700 backdrop-blur-xl">
              <CardBody className="p-8 flex flex-col">
                <div className="flex items-center gap-2 mb-1">
                  <Cloud className="w-5 h-5 text-purple-300" />
                  <h3 className="text-xl font-bold text-white">
                    {t("credits.title")}
                  </h3>
                </div>
                <p className="text-sm text-gray-400 mb-6">
                  {t("credits.subtitle")}
                </p>
                <ul className="space-y-3 mb-8">
                  {cloudFeatures.map((text) => (
                    <li key={text} className="flex items-start gap-3 text-sm">
                      <Check className="w-4 h-4 text-green-500 mt-0.5 flex-shrink-0" />
                      <span className="text-gray-200">{text}</span>
                    </li>
                  ))}
                </ul>
                <Button
                  as={Link}
                  href="/credits"
                  className="w-full bg-gradient-to-r from-purple-600 to-blue-600 text-white mt-auto"
                  size="lg"
                >
                  {t("credits.cta")}
                </Button>
              </CardBody>
            </Card>
          </m.div>
        </div>

        {/* Enterprise */}
        <m.div
          className="max-w-4xl mx-auto mt-12"
          initial={{ opacity: 0, y: 20 }}
          transition={{ duration: 0.5, delay: 0.2 }}
          viewport={{ once: true }}
          whileInView={{ opacity: 1, y: 0 }}
        >
          <Card className="bg-gradient-to-r from-orange-900/20 to-amber-900/20 border-orange-700/50 backdrop-blur-xl">
            <CardBody className="p-6">
              <div className="flex flex-col md:flex-row items-center justify-between gap-4">
                <div className="flex items-center gap-4">
                  <Building2 className="w-8 h-8 text-orange-400 flex-shrink-0" />
                  <div>
                    <h3 className="text-xl font-bold text-white mb-1">
                      {t("enterprise.title")}
                    </h3>
                    <p className="text-gray-300 text-sm">
                      {t("enterprise.description")}
                    </p>
                  </div>
                </div>
                <Button
                  className="bg-gradient-to-r from-orange-600 to-amber-600 text-white flex-shrink-0"
                  size="lg"
                  onPress={() => setShowEnterpriseModal(true)}
                >
                  {t("enterprise.cta")}
                </Button>
              </div>
            </CardBody>
          </Card>
        </m.div>
      </m.div>

      {/* Enterprise Contact Modal */}
      <EnterpriseContactModal
        isOpen={showEnterpriseModal}
        onClose={() => setShowEnterpriseModal(false)}
      />
    </section>
  );
}
