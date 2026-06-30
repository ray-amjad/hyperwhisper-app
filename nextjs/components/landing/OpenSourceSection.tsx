"use client";

import { Card, CardBody } from "@heroui/card";
import { Button } from "@heroui/button";
import { Link as HeroUILink } from "@heroui/link";
import { m } from "framer-motion";
import { Github } from "lucide-react";
import { useTranslations } from "next-intl";

const GITHUB_URL = "https://github.com/ray-amjad/hyperwhisper-app";

export default function OpenSourceSection() {
  const t = useTranslations("openSource");

  return (
    <section className="px-6 py-20" id="open-source">
      <m.div
        className="max-w-4xl mx-auto"
        initial={{ opacity: 0, y: 20 }}
        transition={{ duration: 0.5 }}
        viewport={{ once: true }}
        whileInView={{ opacity: 1, y: 0 }}
      >
        <Card className="bg-gradient-to-r from-slate-900/40 to-gray-900/40 backdrop-blur-xl border border-gray-700/60">
          <CardBody className="p-8 md:p-12">
            <div className="flex flex-col md:flex-row items-center gap-8">
              <div className="w-20 h-20 rounded-2xl bg-gradient-to-r from-slate-600 to-gray-500 flex items-center justify-center flex-shrink-0">
                <Github className="w-10 h-10 text-white" />
              </div>
              <div className="flex-1 text-center md:text-left">
                <h2 className="text-3xl md:text-4xl font-bold mb-4 bg-gradient-to-r from-white to-gray-400 bg-clip-text text-transparent">
                  {t("heading")}
                </h2>
                <p className="text-gray-300 mb-6 leading-relaxed">
                  {t("body")}
                </p>
                <Button
                  as={HeroUILink}
                  isExternal
                  className="bg-white text-black font-semibold"
                  href={GITHUB_URL}
                  size="lg"
                  startContent={<Github className="w-5 h-5" />}
                >
                  {t("cta")}
                </Button>
              </div>
            </div>
          </CardBody>
        </Card>
      </m.div>
    </section>
  );
}
