"use client";

import { useReducedMotion } from "framer-motion";
import { useTranslations } from "next-intl";
import { useEffect, useState, type CSSProperties } from "react";

import PlatformButtons from "./PlatformButtons";

import { Link } from "@/src/i18n/navigation";

/** The app names the subheadline cycles through. Brand names, not copy. */
const APPS = ["Slack", "Cursor", "Notion", "Gmail", "Claude", "Figma"];

/** `--d` is the entrance delay that `.dusk-enter` in globals.css reads. */
const delay = (ms: number) => ({ "--d": `${ms}ms` }) as CSSProperties;

/**
 * The full-height hero over the dusk sky. The entrance is CSS (`dusk-enter`),
 * so it plays from the server HTML and never blanks the hero until hydration
 * (#758). The sky drifts and a sheen crosses it; Reduce motion stops both,
 * the entrance, and the app-name rotation.
 */
export default function HeroSection() {
  const t = useTranslations("hero");
  const tBanner = useTranslations("openSourceBanner");
  const reduceMotion = useReducedMotion();
  const [app, setApp] = useState(0);

  useEffect(() => {
    if (reduceMotion) return;
    const id = window.setInterval(
      () => setApp((i) => (i + 1) % APPS.length),
      2000,
    );

    return () => window.clearInterval(id);
  }, [reduceMotion]);

  return (
    <section
      aria-labelledby="hero-title"
      className="relative isolate flex h-[100svh] min-h-[620px] items-center justify-center overflow-hidden text-center md:min-h-[820px]"
    >
      <div aria-hidden="true" className="dusk-drift absolute inset-[-4%] -z-10">
        <img
          alt=""
          className="h-full w-full object-cover object-[center_60%]"
          decoding="async"
          fetchPriority="high"
          sizes="100vw"
          src="/landing/dusk-1672.webp"
          srcSet="/landing/dusk-960.webp 960w, /landing/dusk-1672.webp 1672w"
        />
      </div>
      <div aria-hidden="true" className="dusk-sheen absolute inset-0 -z-10" />
      <div
        aria-hidden="true"
        className="absolute inset-x-0 bottom-0 -z-10 h-[300px] bg-gradient-to-b from-transparent to-[var(--dusk-night)]"
      />

      <div className="relative w-full max-w-[1000px] px-6 md:-mt-[120px]">
        <Link
          className="dusk-enter inline-flex max-w-full items-center gap-2 rounded-full border border-white/[.18] bg-[rgba(10,10,30,.35)] py-[7px] ps-2 pe-[14px] text-[13px] text-white backdrop-blur-[10px] transition-colors hover:bg-[rgba(10,10,30,.5)] focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--dusk-lav)]"
          href="/open-source"
          style={delay(0)}
        >
          <b className="shrink-0 rounded-full bg-white px-2 py-[3px] text-[11px] font-semibold text-[#111]">
            {t("badge")}
          </b>
          <span className="truncate">{tBanner("text")}</span>
        </Link>

        <h1
          className="mt-[26px] text-[44px] font-semibold leading-none tracking-[-0.035em] text-white [overflow-wrap:anywhere] [text-shadow:0_4px_40px_rgba(10,10,40,.35)] sm:text-[64px] lg:text-[84px]"
          id="hero-title"
        >
          <span className="dusk-enter block" style={delay(80)}>
            {t("headline")}
          </span>
          <span className="dusk-enter block" style={delay(160)}>
            {t("headlineAccent")}
          </span>
        </h1>

        <p
          className="dusk-enter mt-6 text-lg text-white/[.92] [text-shadow:0_1px_14px_rgba(20,10,40,.5)] md:text-xl"
          style={delay(260)}
        >
          {t("subheadline")}{" "}
          {/* Every name sits in one grid cell, so the box is as wide as the
              widest name and nothing moves when it changes. */}
          <span
            aria-hidden="true"
            className="inline-grid text-start align-bottom text-white/60"
          >
            {APPS.map((name, i) => (
              <span
                key={name}
                className={`[grid-area:1/1] transition-[opacity,transform,filter] duration-[350ms] ease-out ${
                  i === app
                    ? "opacity-100 blur-0"
                    : "translate-y-2 opacity-0 blur-[3px]"
                }`}
              >
                {name}.
              </span>
            ))}
          </span>
          <span className="sr-only">{APPS.join(", ")}.</span>
        </p>

        <PlatformButtons className="dusk-enter mt-9" style={delay(360)} />

        <p
          className="dusk-enter mt-[18px] text-[13.5px] text-white/85 [text-shadow:0_1px_12px_rgba(20,10,40,.7)]"
          style={delay(440)}
        >
          {t("freeForever")} · {t("worksOffline")} · {t("macos")},{" "}
          {t("windows")}, {t("linux")}
        </p>
      </div>

      <div
        aria-hidden="true"
        className="absolute bottom-[34px] left-1/2 hidden -translate-x-1/2 flex-col items-center gap-2 text-xs text-white/50 md:flex"
      >
        <i className="dusk-scroll-hint block h-[34px] w-px bg-gradient-to-b from-white/50 to-transparent" />
        {t("scroll")}
      </div>
    </section>
  );
}
