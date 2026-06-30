"use client";

import { useState } from "react";
import { Card, CardBody, CardHeader } from "@heroui/card";
import { Button } from "@heroui/button";
import { m } from "framer-motion";
import { Check, X, Sparkles, Building2 } from "lucide-react";
import { useTranslations } from "next-intl";

import EnterpriseContactModal from "./EnterpriseContactModal";

import { Link } from "@/src/i18n/navigation";
import { useDownloadModal } from "@/contexts/DownloadModalContext";

export default function PricingSection() {
  const { openModal } = useDownloadModal();
  const [showEnterpriseModal, setShowEnterpriseModal] = useState(false);
  const t = useTranslations("pricing");

  const plans = [
    {
      name: t("free.name"),
      price: t("free.price"),
      period: t("free.period"),
      description: t("free.description"),
      features: [
        { text: t("free.features.transcriptionLimit"), included: true },
        { text: t("free.features.basicModes"), included: true },
        { text: t("free.features.offlineTranscription"), included: true },
        { text: t("free.features.apiTranscription"), included: true },
        { text: t("free.features.postProcessing"), included: true },
        { text: t("free.features.providers"), included: true },
        { text: t("free.features.lifetimeUpdates"), included: false },
        { text: t("free.features.cloudLimited"), included: false },
        { text: t("free.features.customVocabulary"), included: false },
        { text: t("free.features.prioritySupport"), included: false },
      ],
      cta: t("free.cta"),
      popular: false,
      action: "download" as const,
    },
    {
      name: t("pro.name"),
      price: t("pro.price"),
      period: t("pro.period"),
      description: t("pro.description"),
      features: [
        { text: t("pro.features.unlimitedTranscription"), included: true },
        { text: t("pro.features.allModes"), included: true },
        { text: t("pro.features.offlineTranscription"), included: true },
        { text: t("pro.features.apiTranscription"), included: true },
        { text: t("pro.features.postProcessing"), included: true },
        { text: t("pro.features.providers"), included: true },
        { text: t("pro.features.lifetimeUpdates"), included: true },
        { text: t("pro.features.cloudCredits"), included: true },
        { text: t("pro.features.customVocabulary"), included: true },
        { text: t("pro.features.prioritySupport"), included: true },
      ],
      cta: t("pro.cta"),
      popular: true,
      action: "checkout" as const,
    },
  ];

  const handleDownload = () => {
    openModal();
  };

  return (
    <section className="px-6 py-20" id="pricing">
      <m.div
        className="max-w-6xl mx-auto"
        initial={{ opacity: 0, y: 20 }}
        transition={{ duration: 0.5 }}
        viewport={{ once: true }}
        whileInView={{ opacity: 1, y: 0 }}
      >
        <div className="text-center mb-16">
          <h2 className="text-4xl md:text-5xl font-bold mb-4 bg-gradient-to-r from-white to-gray-400 bg-clip-text text-transparent">
            {t("title")}
          </h2>
          <p className="text-lg text-gray-400 max-w-2xl mx-auto mb-4">
            {t("subtitle")}
          </p>
          <div className="inline-flex items-center gap-2 bg-green-900/20 border border-green-800 rounded-full px-4 py-2">
            <Check className="w-4 h-4 text-green-400" />
            <span className="text-green-400 text-sm font-medium">
              {t("guarantee")}
            </span>
          </div>
        </div>

        <div className="grid grid-cols-1 md:grid-cols-2 gap-8 max-w-4xl mx-auto">
          {plans.map((plan, index) => (
            <m.div
              key={plan.name}
              className="relative"
              initial={{ opacity: 0, y: 20 }}
              transition={{ duration: 0.5, delay: index * 0.1 }}
              viewport={{ once: true }}
              whileInView={{ opacity: 1, y: 0 }}
            >
              {plan.popular && (
                <div className="absolute -top-4 left-1/2 transform -translate-x-1/2 z-10">
                  <div className="bg-gradient-to-r from-purple-600 to-blue-600 text-white px-4 py-1 rounded-full text-sm font-semibold flex items-center gap-1">
                    <Sparkles className="w-4 h-4" />
                    {t("pro.mostPopular")}
                  </div>
                </div>
              )}
              <Card
                className={`h-full ${
                  plan.popular
                    ? "bg-gradient-to-b from-purple-900/20 to-blue-900/20 border-purple-700"
                    : "bg-gray-900/50 border-gray-800"
                } backdrop-blur-xl`}
              >
                <CardHeader className="pb-8 pt-6">
                  <div className="w-full">
                    <h3 className="text-2xl font-bold text-white mb-2">
                      {plan.name}
                    </h3>
                    <div className="flex items-baseline gap-1 mb-3">
                      <span className="text-4xl font-bold text-white">
                        {plan.price}
                      </span>
                      <span className="text-gray-400">/{plan.period}</span>
                    </div>
                    <p className="text-gray-400 text-sm">{plan.description}</p>
                  </div>
                </CardHeader>
                <CardBody className="pt-0">
                  <ul className="space-y-3 mb-8">
                    {plan.features.map((feature) => (
                      <li key={feature.text} className="flex items-start gap-3">
                        {feature.included ? (
                          <Check className="w-5 h-5 text-green-500 mt-0.5 flex-shrink-0" />
                        ) : (
                          <X className="w-5 h-5 text-gray-600 mt-0.5 flex-shrink-0" />
                        )}
                        <span
                          className={
                            feature.included ? "text-gray-300" : "text-gray-600"
                          }
                        >
                          {feature.text}
                        </span>
                      </li>
                    ))}
                  </ul>
                  {plan.action === "checkout" ? (
                    <Button
                      as={Link}
                      href="/checkout"
                      className={`w-full bg-gradient-to-r from-purple-600 to-blue-600 text-white`}
                      size="lg"
                    >
                      {plan.cta}
                    </Button>
                  ) : (
                    <Button
                      className="w-full bg-gray-800 text-gray-300 border border-gray-700"
                      size="lg"
                      onPress={handleDownload}
                    >
                      {plan.cta}
                    </Button>
                  )}
                </CardBody>
              </Card>
            </m.div>
          ))}
        </div>

        {/* Enterprise Pricing Card */}
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

        <m.div
          className="text-center mt-12"
          initial={{ opacity: 0, y: 20 }}
          transition={{ duration: 0.5, delay: 0.3 }}
          viewport={{ once: true }}
          whileInView={{ opacity: 1, y: 0 }}
        >
          <p className="text-gray-400">
            <span className="text-green-400">{t("guarantee")}</span>{" "}
            {t("guaranteeExtended")}{" "}
            <Link
              className="text-purple-400 hover:text-purple-300 underline"
              href="/legal/refund-policy"
            >
              {t("learnMore")}
            </Link>
          </p>
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
