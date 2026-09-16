"use client";

import { m } from "framer-motion";
import { ChevronDown } from "lucide-react";
import { useState, type ReactElement } from "react";
import { useTranslations } from "next-intl";

// Parse markdown links [text](url) and convert to JSX
function parseMarkdownLinks(text: string) {
  const parts: (string | ReactElement)[] = [];
  const regex = /\[([^\]]+)\]\(([^)]+)\)/g;
  let lastIndex = 0;
  let match: RegExpExecArray | null;

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

  // Collapsing the open answer above the clicked question pulls that question
  // up by the answer's full height, which can drop it off screen now that the
  // panel is no longer capped. Hold the clicked trigger at the viewport
  // position it had, for as long as the 300ms transition runs.
  //
  // The hold always loses to the user: the first real input ends it on that
  // very frame, and every listener comes off however the hold ends, so nothing
  // it installed outlives the click.
  const toggleFAQ = (index: number, trigger: HTMLElement) => {
    const closingSelf = openIndex === index;
    const somethingWasOpen = openIndex !== null;

    setOpenIndex(closingSelf ? null : index);

    if (closingSelf || !somethingWasOpen) return;

    const anchor = trigger.getBoundingClientRect().top;
    const userInput = ["wheel", "touchstart", "keydown", "pointerdown"];
    let startedAt: number | null = null;
    let frame = 0;

    const release = () => {
      cancelAnimationFrame(frame);
      frame = 0;
      for (const type of userInput) {
        window.removeEventListener(type, release, true);
      }
    };
    const hold = (now: number) => {
      frame = 0;
      startedAt ??= now;

      if (!trigger.isConnected || now - startedAt >= 400) {
        release();
      } else {
        const drift = trigger.getBoundingClientRect().top - anchor;

        if (drift !== 0) window.scrollBy({ top: drift, behavior: "instant" });
        frame = requestAnimationFrame(hold);
      }
    };

    for (const type of userInput) {
      window.addEventListener(type, release, { capture: true, passive: true });
    }
    frame = requestAnimationFrame(hold);
  };

  const faqKeys = [
    "offline",
    "compatibility",
    "accuracy",
    "privacy",
    "dataTraining",
    "dataStorage",
    "lifetime",
    "refund",
    "requirements",
    "languages",
    "apiKey",
    "cloud",
    "creditExpiry",
    "deviceLimit",
    "offlineModels",
    "madeBy",
    "funding",
    "selfHost",
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
                aria-controls={`faq-panel-${key}`}
                aria-expanded={openIndex === index}
                className="w-full px-6 py-4 text-left hover:bg-gray-800/50 transition-colors flex items-center justify-between"
                id={`faq-trigger-${key}`}
                onClick={(event) => toggleFAQ(index, event.currentTarget)}
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
                className={`grid transition-[grid-template-rows] duration-300 ease-out ${
                  openIndex === index ? "grid-rows-[1fr]" : "grid-rows-[0fr]"
                }`}
                id={`faq-panel-${key}`}
              >
                {/* overflow-hidden is load-bearing: it takes this grid item out of
                    automatic-minimum-size sizing, which is what lets the 0fr track
                    resolve to 0. Without it every closed panel keeps its full height. */}
                <div className="overflow-hidden">
                  <p className="text-gray-400 px-6 pt-2 pb-6 whitespace-pre-line">
                    {parseMarkdownLinks(t(`questions.${key}.answer`))}
                  </p>
                </div>
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
              href="mailto:hi@support.hyperwhisper.com"
            >
              hi@support.hyperwhisper.com
            </a>
          </p>
        </m.div>
      </m.div>
    </section>
  );
}
