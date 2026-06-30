"use client";

import { m } from "framer-motion";
import { useTranslations } from "next-intl";

export default function VideoDemo() {
  const t = useTranslations("demo");

  return (
    <section className="px-6 py-20" id="demo">
      <m.div
        className="max-w-6xl mx-auto"
        initial={{ opacity: 0, y: 20 }}
        transition={{ duration: 0.5 }}
        viewport={{ once: true }}
        whileInView={{ opacity: 1, y: 0 }}
      >
        <div className="w-full h-auto rounded-2xl overflow-hidden shadow-2xl">
          <div style={{ position: "relative", paddingTop: "56.25%" }}>
            <iframe
              allow="accelerometer;gyroscope;autoplay;encrypted-media;picture-in-picture;"
              allowFullScreen={true}
              loading="lazy"
              title="HyperWhisper demo video"
              src="https://iframe.mediadelivery.net/embed/523175/379cb709-30bd-4155-8f70-8965c9d0430d?autoplay=false&loop=false&muted=false&preload=true&responsive=true"
              style={{
                border: 0,
                position: "absolute",
                top: 0,
                height: "100%",
                width: "100%",
              }}
            />
          </div>
        </div>
        {/* Feature highlights below video */}
        <div className="grid grid-cols-2 md:grid-cols-4 gap-6 mt-12">
          <m.div
            className="text-center"
            initial={{ opacity: 0, y: 20 }}
            transition={{ duration: 0.5, delay: 0.1 }}
            viewport={{ once: true }}
            whileInView={{ opacity: 1, y: 0 }}
          >
            <div className="text-3xl font-bold text-purple-500 mb-2">5x</div>
            <div className="text-gray-400">{t("fasterThanTyping")}</div>
          </m.div>
          <m.div
            className="text-center"
            initial={{ opacity: 0, y: 20 }}
            transition={{ duration: 0.5, delay: 0.2 }}
            viewport={{ once: true }}
            whileInView={{ opacity: 1, y: 0 }}
          >
            <div className="text-3xl font-bold text-blue-500 mb-2">99%</div>
            <div className="text-gray-400">{t("accuracyRate")}</div>
          </m.div>
          <m.div
            className="text-center"
            initial={{ opacity: 0, y: 20 }}
            transition={{ duration: 0.5, delay: 0.3 }}
            viewport={{ once: true }}
            whileInView={{ opacity: 1, y: 0 }}
          >
            <div className="text-3xl font-bold text-cyan-500 mb-2">100+</div>
            <div className="text-gray-400">{t("languagesSupported")}</div>
          </m.div>
          <m.div
            className="text-center"
            initial={{ opacity: 0, y: 20 }}
            transition={{ duration: 0.5, delay: 0.4 }}
            viewport={{ once: true }}
            whileInView={{ opacity: 1, y: 0 }}
          >
            <div className="text-3xl font-bold text-green-500 mb-2">9+</div>
            <div className="text-gray-400">{t("providersSupported")}</div>
          </m.div>
        </div>
      </m.div>
    </section>
  );
}
