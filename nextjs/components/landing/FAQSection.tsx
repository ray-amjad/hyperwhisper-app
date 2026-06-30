"use client";

import { m } from "framer-motion";
import { ChevronDown } from "lucide-react";
import { useState } from "react";
import { useTranslations } from "next-intl";

// Parse markdown links [text](url) and convert to JSX
function parseMarkdownLinks(text: string) {
  const parts: (string | JSX.Element)[] = [];
  const regex = /\[([^\]]+)\]\(([^)]+)\)/g;
  let lastIndex = 0;
  let match;

  while ((match = regex.exec(text)) !== null) {
    // Add text before the link
    if (match.index > lastIndex) {
      parts.push(text.substring(lastIndex, match.index));
    }

    // Add the link
    const [, linkText, url] = match;

    parts.push(
      <a
        key={`link-${match.index}`}
        className="text-purple-400 hover:text-purple-300 transition-colors underline"
        href={url}
        rel="noopener noreferrer"
        target="_blank"
      >
        {linkText}
      </a>,
    );

    lastIndex = regex.lastIndex;
  }

  // Add remaining text
  if (lastIndex < text.length) {
    parts.push(text.substring(lastIndex));
  }

  return parts.length > 0 ? parts : text;
}

export default function FAQSection() {
  const [openIndex, setOpenIndex] = useState<number | null>(null);
  const t = useTranslations("faq");

  const toggleFAQ = (index: number) => {
    setOpenIndex(openIndex === index ? null : index);
  };

  const faqKeys = [
    "offline",
    "compatibility",
    "accuracy",
    "privacy",
    "dataTraining",
    "lifetime",
    "requirements",
    "windowsStatus",
    "languages",
    "apiKey",
    "cloud",
    "deviceLimit",
    "offlineModels",
    "madeBy",
  ];

  return (
    <section className="px-6 py-20" id="faq">
      <m.div
        className="max-w-3xl mx-auto"
        initial={{ opacity: 0, y: 20 }}
        transition={{ duration: 0.5 }}
        viewport={{ once: true }}
        whileInView={{ opacity: 1, y: 0 }}
      >
        <div className="text-center mb-12">
          <h2 className="text-4xl md:text-5xl font-bold mb-4 bg-gradient-to-r from-white to-gray-400 bg-clip-text text-transparent">
            {t("title")}
          </h2>
          <p className="text-lg text-gray-400">{t("subtitle")}</p>
        </div>

        <div className="bg-gray-900/50 backdrop-blur-xl border border-gray-800 rounded-2xl overflow-hidden">
          {faqKeys.map((key, index) => (
            <div key={key} className="border-b border-gray-800 last:border-b-0">
              <button
                className="w-full px-6 py-4 text-left hover:bg-gray-800/50 transition-colors flex items-center justify-between"
                onClick={() => toggleFAQ(index)}
              >
                <span className="text-gray-200 font-medium">
                  {t(`questions.${key}.question`)}
                </span>
                <ChevronDown
                  className={`w-5 h-5 text-gray-400 transition-transform ${
                    openIndex === index ? "rotate-180" : ""
                  }`}
                />
              </button>
              <div
                className={`overflow-hidden transition-all duration-300 ${
                  openIndex === index ? "max-h-96" : "max-h-0"
                }`}
              >
                <p className="text-gray-400 px-6 pt-2 pb-6 whitespace-pre-line">
                  {parseMarkdownLinks(t(`questions.${key}.answer`))}
                </p>
              </div>
            </div>
          ))}
        </div>

        <m.div
          className="text-center mt-12"
          initial={{ opacity: 0, y: 20 }}
          transition={{ duration: 0.5, delay: 0.2 }}
          viewport={{ once: true }}
          whileInView={{ opacity: 1, y: 0 }}
        >
          <p className="text-gray-400">
            {t("contactPrompt")}{" "}
            <a
              className="text-purple-400 hover:text-purple-300 transition-colors"
              href="mailto:support@hyperwhisper.com"
            >
              support@hyperwhisper.com
            </a>
          </p>
        </m.div>
      </m.div>
    </section>
  );
}
