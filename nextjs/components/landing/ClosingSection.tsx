"use client";

import { useTranslations } from "next-intl";

import PlatformButtons from "./PlatformButtons";
import Reveal from "./Reveal";

/** "Stop typing. Start talking." over the night sky, then the download buttons. */
export default function ClosingSection() {
  const t = useTranslations("hero.closing");

  return (
    <section
      aria-labelledby="closing-title"
      className="relative isolate flex min-h-[600px] items-start justify-center overflow-hidden text-center md:min-h-[760px]"
    >
      <img
        alt=""
        aria-hidden="true"
        className="absolute inset-0 -z-10 h-full w-full object-cover object-[center_70%]"
        decoding="async"
        loading="lazy"
        sizes="100vw"
        src="/landing/night-1672.webp"
        srcSet="/landing/night-960.webp 960w, /landing/night-1672.webp 1672w"
      />
      <div
        aria-hidden="true"
        className="absolute inset-x-0 top-0 -z-10 h-[260px] bg-gradient-to-b from-[var(--dusk-night)] to-transparent"
      />

      <div className="w-full max-w-[1000px] px-6 pb-24 pt-[120px] md:pt-[170px]">
        <Reveal>
          <h2
            className="text-[44px] font-semibold leading-[1.02] tracking-[-0.035em] text-white [overflow-wrap:anywhere] sm:text-[56px] md:text-[72px]"
            id="closing-title"
          >
            {t("line1")}
            <br />
            <span className="dusk-grad">{t("line2")}</span>
          </h2>
        </Reveal>
        <Reveal className="mt-8" delay={120}>
          <PlatformButtons />
        </Reveal>
        <Reveal
          as="p"
          className="mt-[18px] text-[13.5px] text-white/80"
          delay={200}
        >
          {t("meta")}
        </Reveal>
      </div>
    </section>
  );
}
