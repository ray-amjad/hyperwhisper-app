"use client";

import { Button } from "@heroui/button";
import { Link } from "@heroui/link";
import { m } from "framer-motion";
import { Download, Play } from "lucide-react";
import { useTranslations } from "next-intl";

import { useDownloadModal } from "@/contexts/DownloadModalContext";

export default function HeroSection() {
  const { openModal } = useDownloadModal();
  const t = useTranslations("hero");

  return (
    <section className="relative min-h-[90vh] flex flex-col items-center justify-center px-6 pb-20 overflow-hidden">
      {/* Background gradient */}
      <div className="absolute inset-0 -z-10">
        <div className="absolute top-0 -left-4 w-72 h-72 bg-purple-700 rounded-full mix-blend-multiply filter blur-xl opacity-20 animate-blob" />
        <div className="absolute top-0 -right-4 w-72 h-72 bg-cyan-700 rounded-full mix-blend-multiply filter blur-xl opacity-20 animate-blob animation-delay-2000" />
        <div className="absolute -bottom-8 left-20 w-72 h-72 bg-pink-700 rounded-full mix-blend-multiply filter blur-xl opacity-20 animate-blob animation-delay-4000" />
      </div>

      <m.div
        animate={{ opacity: 1, y: 0 }}
        className="text-center max-w-5xl mx-auto"
        initial={{ opacity: 0, y: 20 }}
        transition={{ duration: 0.5 }}
      >
        {/* Logo */}
        <div className="mb-8 flex justify-center">
          <img
            alt={t("logoAlt")}
            className="w-40 h-40 rounded-2xl shadow-2xl"
            src="/icon/256.png"
          />
        </div>

        {/* Main headline */}
        <h1 className="text-5xl md:text-6xl lg:text-7xl font-bold mb-6 bg-gradient-to-r from-white to-gray-400 bg-clip-text text-transparent">
          {t("headline")}
          <br />
          {t("headlineAccent")}
        </h1>

        {/* Subheadline */}
        <p className="text-xl md:text-2xl text-gray-400 mb-10 max-w-3xl mx-auto">
          {t("subheadline")}
        </p>

        {/* CTA Buttons */}
        <div className="flex flex-col sm:flex-row gap-4 justify-center items-center mb-8">
          <Button
            className="bg-gradient-to-r from-purple-600 to-blue-600 text-white font-semibold px-8 py-6 text-lg"
            size="lg"
            startContent={<Download className="w-5 h-5" />}
            onClick={openModal}
          >
            {t("downloadCta")}
          </Button>
          <Button
            as={Link}
            className="border-gray-600 text-gray-300 font-semibold px-8 py-6 text-lg hover:bg-gray-800"
            href="#demo"
            size="lg"
            startContent={<Play className="w-5 h-5" />}
            variant="bordered"
          >
            {t("demoCta")}
          </Button>
        </div>

        {/* Platform badges */}
        <div className="flex gap-4 justify-center items-center">
          <span className="text-sm text-gray-500">{t("availableOn")}</span>
          <div className="flex gap-3 flex-wrap justify-center">
            <div className="px-3 py-1 bg-gray-800 rounded-lg border border-gray-700">
              <span className="text-sm text-gray-300">{t("macos")}</span>
            </div>
            <div className="px-3 py-1 bg-gray-800 rounded-lg border border-gray-700 flex items-center gap-2">
              <span className="text-sm text-gray-300">{t("windows")}</span>
              <span className="text-[10px] font-medium text-yellow-400 bg-yellow-400/10 px-1.5 py-0.5 rounded">BETA</span>
            </div>
          </div>
        </div>
      </m.div>
    </section>
  );
}
