"use client";

import type { ReactNode } from "react";

import {
  Code,
  FileAudio,
  Globe,
  Monitor,
  Plug,
  Type,
  WifiOff,
  Zap,
  type LucideIcon,
} from "lucide-react";
import { useTranslations } from "next-intl";

import { EYEBROW, H2, WRAP } from "./dusk";
import Reveal from "./Reveal";

/** Illustrative vocabulary. Proper nouns and jargon, so not translated. */
const VOCAB = [
  { word: "Kubernetes", hl: true },
  { word: "Ray Amjad", hl: false },
  { word: "pgvector", hl: true },
  { word: "OKRs", hl: false },
  { word: "Anthropic", hl: false },
  { word: "HIPAA", hl: true },
  { word: "Q3 roadmap", hl: false },
];

/**
 * Decorative bar heights (%) on the streaming tile. Literal numbers, not
 * Math.sin/cos at render: those may round differently between the server's
 * and a browser's JS engine, and a single differing height is a hydration
 * mismatch.
 */
const BARS = [
  30, 68, 83, 70, 42, 39, 40, 32, 40, 30, 56, 79, 78, 49, 52, 80, 80, 57, 31,
  43, 37, 36, 36, 43,
];

function Tile({
  big,
  children,
  delay,
  icon: Icon,
  item,
}: {
  big?: boolean;
  children?: ReactNode;
  delay: number;
  icon: LucideIcon;
  item: string;
}) {
  const t = useTranslations("features");

  return (
    <Reveal
      as="li"
      className={`relative overflow-hidden rounded-[22px] border border-[var(--dusk-line)] bg-[var(--dusk-glass)] p-6 text-start transition-[border-color,background-color] duration-200 ease-out hover:border-[rgba(196,181,253,.28)] hover:bg-white/[.06] md:p-[26px] ${
        big ? "sm:col-span-2 lg:col-span-3 lg:min-h-[250px]" : "lg:col-span-2"
      }`}
      delay={delay}
    >
      <div className="mb-[18px] grid size-[38px] place-items-center rounded-[11px] bg-[rgba(196,181,253,.12)] text-[var(--dusk-lav)]">
        <Icon aria-hidden="true" className="size-[19px]" />
      </div>
      <h3 className="text-[17px] font-semibold text-[var(--dusk-ink)]">
        {t(`${item}.title`)}
      </h3>
      <p className="mt-1.5 text-[14.5px] leading-normal text-[var(--dusk-mute)]">
        {t(`${item}.description`)}
      </p>
      {children}
    </Reveal>
  );
}

export default function FeaturesGrid() {
  const t = useTranslations("features");
  const tNav = useTranslations("navbar");

  return (
    <section className="pb-24 pt-28 md:pb-[120px] md:pt-[140px]" id="features">
      <div className={WRAP}>
        <div className="mx-auto mb-14 max-w-[640px] text-center">
          <Reveal className={EYEBROW}>{tNav("features")}</Reveal>
          <Reveal delay={80}>
            <h2 className={`${H2} mt-3`}>{t("title")}</h2>
          </Reveal>
        </div>

        <ul className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-6">
          <Tile big delay={0} icon={Type} item="customVocabulary">
            <div aria-hidden="true" className="mt-[22px] flex flex-wrap gap-2">
              {VOCAB.map(({ word, hl }) => (
                <span
                  key={word}
                  className={`rounded-full border bg-white/[.03] px-[11px] py-1.5 text-[13px] ${
                    hl
                      ? "border-[rgba(253,186,140,.4)] text-[var(--dusk-peach)]"
                      : "border-[var(--dusk-line)] text-[#dcdcf0]"
                  }`}
                >
                  {word}
                </span>
              ))}
            </div>
          </Tile>
          <Tile big delay={100} icon={Zap} item="realTime">
            <div
              aria-hidden="true"
              className="mt-[22px] flex h-[70px] items-end gap-1.5"
            >
              {BARS.map((height, i) => (
                <i
                  key={i}
                  className="flex-1 rounded-t-[4px] rounded-b-[2px] bg-gradient-to-b from-[#c4b5fd] to-[rgba(196,181,253,.15)]"
                  style={{ height: `${height}%` }}
                />
              ))}
            </div>
          </Tile>
          <Tile delay={0} icon={Globe} item="languages">
            <div
              aria-hidden="true"
              className="pointer-events-none absolute end-[18px] top-4 text-end text-[22px] font-semibold leading-[1.1] tracking-[-1px] text-white/[.06]"
            >
              Hola
              <br />
              こんにちは
              <br />
              Bonjour
            </div>
          </Tile>
          <Tile delay={100} icon={WifiOff} item="offline" />
          <Tile delay={200} icon={Code} item="openSource" />
          <Tile delay={0} icon={Monitor} item="screenOcr" />
          <Tile delay={100} icon={Plug} item="mcpServer" />
          <Tile delay={200} icon={FileAudio} item="fileImport" />
        </ul>
      </div>
    </section>
  );
}
