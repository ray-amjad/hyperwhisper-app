"use client";

import { Check, Minus } from "lucide-react";
import { useTranslations } from "next-intl";
import { useState } from "react";

import { BTN_GLASS, BTN_WHITE, EYEBROW, H2, LEAD, WRAP } from "./dusk";
import EnterpriseContactModal from "./EnterpriseContactModal";
import Reveal from "./Reveal";

import { useDownloadModal } from "@/contexts/DownloadModalContext";
import { Link } from "@/src/i18n/navigation";

const PLAN =
  "relative flex h-full flex-col rounded-[26px] border p-7 text-start md:p-[34px]";
const PLAN_BTN = "mt-auto w-full px-[22px] py-[13px] text-[15px]";

/**
 * "Two ways to go cloud". Bring your own API keys (free, opens the download
 * dialog) or HyperWhisper Cloud Credits (pay as you go, goes to /credits),
 * plus the Enterprise contact form. Copy lives under `cloudSection`.
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
    <section className="pb-24 pt-5 md:pb-[120px]" id="cloud">
      <div className={WRAP}>
        <div className="mx-auto max-w-[640px] text-center">
          <Reveal className={EYEBROW}>{t("eyebrow")}</Reveal>
          <Reveal delay={80}>
            <h2 className={`${H2} mt-3`}>{t("title")}</h2>
          </Reveal>
          <Reveal as="p" className={LEAD} delay={160}>
            {t("subtitle")}
          </Reveal>
        </div>

        <div className="mt-12 grid gap-[18px] md:mt-[52px] md:grid-cols-2">
          <Reveal
            className={`${PLAN} border-[var(--dusk-line)] bg-[var(--dusk-glass)]`}
          >
            <h3 className="text-[22px] font-semibold">{t("byo.title")}</h3>
            <p className="mt-1 text-[var(--dusk-mute)]">{t("byo.subtitle")}</p>
            <ul className="mb-7 mt-6 grid gap-[11px] text-[15px]">
              {byoFeatures.map((feature) => (
                <li key={feature.text} className="flex items-start gap-2.5">
                  {feature.included ? (
                    <Check
                      aria-hidden="true"
                      className="mt-[3px] size-4 flex-none text-[var(--dusk-lav)]"
                      strokeWidth={2.4}
                    />
                  ) : (
                    <Minus
                      aria-hidden="true"
                      className="mt-[3px] size-4 flex-none text-[var(--dusk-dim)]"
                      strokeWidth={2.4}
                    />
                  )}
                  <span
                    className={
                      feature.included
                        ? "text-[#d6d8ea]"
                        : "text-[var(--dusk-mute)]"
                    }
                  >
                    {feature.text}
                  </span>
                </li>
              ))}
            </ul>
            <button
              className={`${BTN_GLASS} ${PLAN_BTN}`}
              type="button"
              onClick={openModal}
            >
              {t("byo.cta")}
            </button>
          </Reveal>

          <Reveal
            className={`${PLAN} border-[rgba(196,181,253,.35)] bg-[linear-gradient(180deg,rgba(129,90,220,.18),rgba(255,255,255,.03))]`}
            delay={100}
          >
            <span className="absolute end-6 top-6 rounded-full bg-white px-2.5 py-[5px] text-xs font-semibold text-[#111]">
              {t("credits.badge")}
            </span>
            <h3 className="pe-24 text-[22px] font-semibold">
              {t("credits.title")}
            </h3>
            <p className="mt-1 text-[var(--dusk-mute)]">
              {t("credits.subtitle")}
            </p>
            <ul className="mb-7 mt-6 grid gap-[11px] text-[15px] text-[#d6d8ea]">
              {cloudFeatures.map((text) => (
                <li key={text} className="flex items-start gap-2.5">
                  <Check
                    aria-hidden="true"
                    className="mt-[3px] size-4 flex-none text-[var(--dusk-lav)]"
                    strokeWidth={2.4}
                  />
                  <span>{text}</span>
                </li>
              ))}
            </ul>
            <Link className={`${BTN_WHITE} ${PLAN_BTN}`} href="/credits">
              {t("credits.cta")}
            </Link>
          </Reveal>
        </div>

        <Reveal
          className="mt-[18px] flex flex-col items-start gap-4 rounded-[20px] border border-[var(--dusk-line)] bg-white/[.025] px-6 py-[22px] text-start sm:flex-row sm:items-center sm:justify-between md:px-7"
          delay={200}
        >
          <div className="min-w-0">
            <h3 className="inline text-[17px] font-semibold">
              {t("enterprise.title")}
            </h3>
            <p className="mt-1 text-[15px] text-[var(--dusk-mute)] sm:ms-3 sm:mt-0 sm:inline">
              {t("enterprise.description")}
            </p>
          </div>
          <button
            className={`${BTN_GLASS} flex-none px-5 py-[11px] text-sm`}
            type="button"
            onClick={() => setShowEnterpriseModal(true)}
          >
            {t("enterprise.cta")}
          </button>
        </Reveal>
      </div>

      <EnterpriseContactModal
        isOpen={showEnterpriseModal}
        onClose={() => setShowEnterpriseModal(false)}
      />
    </section>
  );
}
