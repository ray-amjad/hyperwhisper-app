"use client";

import { Card, CardBody } from "@heroui/card";
import { m, type Variants } from "framer-motion";
import {
  Mic,
  Globe,
  Zap,
  Shield,
  Cloud,
  Brain,
  Sparkles,
  Radio,
  FileAudio,
  Eye,
  Plug,
  Github,
} from "lucide-react";
import { useTranslations } from "next-intl";

// One trigger for the whole grid (#877): the cards are children of a single
// whileInView container and take a capped per-index delay through `custom`.
// Per-card triggers made the last visible row wait for its own viewport entry.
// 0.25 s and a 40 ms cap keep the grid readable in < 600 ms after an anchor
// jump, which globals.css makes a ~700 ms smooth scroll.
const cardVariants: Variants = {
  hidden: { opacity: 0, y: 12, filter: "blur(4px)" },
  show: (index: number) => ({
    opacity: 1,
    y: 0,
    filter: "blur(0px)",
    transition: { duration: 0.25, delay: Math.min(index, 2) * 0.02 },
  }),
};

export default function FeaturesGrid() {
  const t = useTranslations("features");

  const features = [
    {
      icon: <Mic className="w-6 h-6" />,
      titleKey: "customVocabulary.title",
      descriptionKey: "customVocabulary.description",
      gradient: "from-purple-600 to-pink-600",
    },
    {
      icon: <Globe className="w-6 h-6" />,
      titleKey: "languages.title",
      descriptionKey: "languages.description",
      gradient: "from-blue-600 to-cyan-600",
    },
    {
      icon: <Zap className="w-6 h-6" />,
      titleKey: "offline.title",
      descriptionKey: "offline.description",
      gradient: "from-orange-600 to-red-600",
    },
    {
      icon: <Shield className="w-6 h-6" />,
      titleKey: "privacy.title",
      descriptionKey: "privacy.description",
      gradient: "from-green-600 to-emerald-600",
    },
    {
      icon: <Github className="w-6 h-6" />,
      titleKey: "openSource.title",
      descriptionKey: "openSource.description",
      gradient: "from-slate-600 to-gray-500",
    },
    {
      icon: <Cloud className="w-6 h-6" />,
      titleKey: "hybridProcessing.title",
      descriptionKey: "hybridProcessing.description",
      gradient: "from-indigo-600 to-purple-600",
    },
    {
      icon: <Brain className="w-6 h-6" />,
      titleKey: "customizable.title",
      descriptionKey: "customizable.description",
      gradient: "from-pink-600 to-rose-600",
    },
    {
      icon: <Radio className="w-6 h-6" />,
      titleKey: "realTime.title",
      descriptionKey: "realTime.description",
      gradient: "from-emerald-600 to-teal-600",
    },
    {
      icon: <FileAudio className="w-6 h-6" />,
      titleKey: "fileImport.title",
      descriptionKey: "fileImport.description",
      gradient: "from-amber-600 to-yellow-600",
    },
    {
      icon: <Eye className="w-6 h-6" />,
      titleKey: "screenOcr.title",
      descriptionKey: "screenOcr.description",
      gradient: "from-violet-500 to-sky-500",
    },
    {
      icon: <Plug className="w-6 h-6" />,
      titleKey: "mcpServer.title",
      descriptionKey: "mcpServer.description",
      gradient: "from-teal-600 to-cyan-600",
    },
  ];

  const modes = [
    t("modes.meeting"),
    t("modes.email"),
    t("modes.note"),
    t("modes.code"),
    t("modes.legal"),
    t("modes.medical"),
  ];

  return (
    <section className="px-6 py-20" id="features">
      {/* The wrapper does not animate: an opacity fade here would multiply
          with each card's own fade below (#877). */}
      <div className="max-w-6xl mx-auto">
        <m.div
          className="text-center mb-16"
          initial={{ opacity: 0, y: 12, filter: "blur(4px)" }}
          transition={{ duration: 0.4 }}
          viewport={{ once: true }}
          whileInView={{ opacity: 1, y: 0, filter: "blur(0px)" }}
        >
          <h2 className="text-4xl md:text-5xl font-bold mb-4 bg-gradient-to-r from-white to-gray-400 bg-clip-text text-transparent">
            {t("title")}
          </h2>
          <p className="text-lg text-gray-400 max-w-2xl mx-auto">
            {t("subtitle")}
          </p>
        </m.div>

        {/* The 800px bottom margin starts the reveal before the grid reaches
            the fold, so it finishes while the scroll is still bringing it in. */}
        <m.div
          className="flex flex-wrap justify-center gap-6"
          initial="hidden"
          viewport={{ once: true, margin: "0px 0px 800px 0px" }}
          whileInView="show"
        >
          {features.map((feature, index) => (
            <m.div
              key={feature.titleKey}
              className="w-full md:w-[calc(50%-12px)] lg:w-[calc(33.333%-16px)]"
              custom={index}
              variants={cardVariants}
            >
              <Card className="bg-gray-900/50 backdrop-blur-xl border border-gray-800 hover:border-gray-700 transition-colors h-full">
                {/* text-start overrides the physical `text-left` baked into
                    HeroUI's CardBody slot, which would otherwise leave the
                    heading and body copy flush left under dir="rtl". */}
                <CardBody className="p-6 text-start">
                  <div
                    className={`w-12 h-12 rounded-xl bg-gradient-to-r ${feature.gradient} flex items-center justify-center mb-4`}
                  >
                    {feature.icon}
                  </div>
                  <h3 className="text-xl font-semibold mb-2 text-white">
                    {t(feature.titleKey)}
                  </h3>
                  <p className="text-gray-400 text-sm">
                    {t(feature.descriptionKey)}
                  </p>
                </CardBody>
              </Card>
            </m.div>
          ))}
        </m.div>

        {/* Additional feature highlight */}
        <m.div
          className="mt-16"
          initial={{ opacity: 0, y: 12, filter: "blur(4px)" }}
          transition={{ duration: 0.4, delay: 0.3 }}
          viewport={{ once: true }}
          whileInView={{ opacity: 1, y: 0, filter: "blur(0px)" }}
        >
          <Card className="bg-gradient-to-r from-purple-900/20 to-blue-900/20 backdrop-blur-xl border border-purple-800/50">
            <CardBody className="p-8 md:p-12 text-start">
              <div className="flex flex-col md:flex-row items-center gap-8">
                <div className="flex-1">
                  <div className="flex items-center gap-3 mb-4">
                    <Sparkles className="w-8 h-8 text-purple-400" />
                    <h3 className="text-2xl font-bold text-white">
                      {t("modes.title")}
                    </h3>
                  </div>
                  <p className="text-gray-300 mb-6">{t("modes.description")}</p>
                  <div className="flex flex-wrap gap-2">
                    {modes.map((mode) => (
                      <span
                        key={mode}
                        className="px-3 py-1 bg-purple-800/30 border border-purple-700/50 rounded-full text-sm text-purple-300"
                      >
                        {mode}
                      </span>
                    ))}
                  </div>
                </div>
                <div className="w-full md:w-auto">
                  <div className="grid grid-cols-2 gap-4">
                    <div className="text-center">
                      <div className="text-3xl font-bold text-purple-400 mb-1">
                        ∞
                      </div>
                      <div className="text-sm text-gray-400">
                        {t("modes.presetModes")}
                      </div>
                    </div>
                    <div className="text-center">
                      <div className="text-3xl font-bold text-blue-400 mb-1">
                        ∞
                      </div>
                      <div className="text-sm text-gray-400">
                        {t("modes.customModes")}
                      </div>
                    </div>
                  </div>
                </div>
              </div>
            </CardBody>
          </Card>
        </m.div>
      </div>
    </section>
  );
}
