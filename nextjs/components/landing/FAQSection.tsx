"use client";

import { Plus } from "lucide-react";
import { useTranslations } from "next-intl";
import { useState, type ReactElement } from "react";

import { EYEBROW, H2, WRAP } from "./dusk";
import Reveal from "./Reveal";

import { TEXT_LINK } from "@/components/text-link";

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
        className={TEXT_LINK}
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
  const tNav = useTranslations("navbar");

  const toggleFAQ = (index: number) => {
    setOpenIndex(openIndex === index ? null : index);
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
    <section className="pb-24 pt-10 md:pb-[140px]" id="faq">
      <div className={WRAP}>
        <div className="mx-auto max-w-[640px] text-center">
          <Reveal className={EYEBROW}>{tNav("faq")}</Reveal>
          <Reveal delay={80}>
            <h2 className={`${H2} mt-3`}>{t("title")}</h2>
          </Reveal>
        </div>

        <Reveal
          className="mx-auto mt-12 max-w-[820px] border-t border-[var(--dusk-line)]"
          delay={160}
        >
          {faqKeys.map((key, index) => (
            <div key={key} className="border-b border-[var(--dusk-line)]">
              <button
                aria-controls={`faq-panel-${key}`}
                aria-expanded={openIndex === index}
                className="flex w-full items-center justify-between gap-4 px-1 py-5 text-start text-base font-medium text-[var(--dusk-ink)] transition-colors hover:text-white focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--dusk-lav)] md:py-[22px] md:text-[17px]"
                id={`faq-trigger-${key}`}
                type="button"
                onClick={() => toggleFAQ(index)}
              >
                <span>{t(`questions.${key}.question`)}</span>
                <Plus
                  aria-hidden="true"
                  className={`size-[18px] flex-none text-[var(--dusk-mute)] transition-[rotate] duration-[250ms] ease-[var(--dusk-ease)] ${
                    openIndex === index ? "rotate-45" : ""
                  }`}
                />
              </button>
              <div
                className={`grid transition-[grid-template-rows] duration-300 ease-[var(--dusk-ease)] ${
                  openIndex === index ? "grid-rows-[1fr]" : "grid-rows-[0fr]"
                }`}
                id={`faq-panel-${key}`}
                // A closed panel is 0px tall but its links are still in the
                // Tab order; inert takes them out until the panel opens.
                inert={openIndex !== index}
              >
                {/* overflow-hidden is load-bearing: it takes this grid item out of
                    automatic-minimum-size sizing, which is what lets the 0fr track
                    resolve to 0. Without it every closed panel keeps its full height. */}
                <div className="overflow-hidden">
                  <p className="max-w-[700px] whitespace-pre-line px-1 pb-[22px] text-[15.5px] leading-[1.6] text-[var(--dusk-mute)]">
                    {parseMarkdownLinks(t(`questions.${key}.answer`))}
                  </p>
                </div>
              </div>
            </div>
          ))}
        </Reveal>

        <Reveal as="p" className="mt-10 text-center text-[var(--dusk-mute)]">
          {t("contactPrompt")}{" "}
          <a className={TEXT_LINK} href="mailto:hi@support.hyperwhisper.com">
            hi@support.hyperwhisper.com
          </a>
        </Reveal>
      </div>
    </section>
  );
}
