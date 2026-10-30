"use client";

import { useInView, useReducedMotion } from "framer-motion";
import { useTranslations } from "next-intl";
import { useEffect, useRef, useState } from "react";

import { EYEBROW, H2, LEAD, WRAP } from "./dusk";
import Reveal from "./Reveal";
import { usePageVisible } from "./use-page-visible";

const MODES = ["email", "note", "code", "meeting", "legal", "medical"] as const;

type Mode = (typeof MODES)[number];

/**
 * One spoken sentence, rewritten by each mode. It moves to the next mode every
 * 3.2 s while the box is in view, and stops for good once someone picks a
 * mode, or when reduce motion is on.
 */
export default function ModesShowcase() {
  const t = useTranslations("features.modes");
  const tNav = useTranslations("navbar");
  const reduceMotion = useReducedMotion();
  const boxRef = useRef<HTMLDivElement>(null);
  const inView = useInView(boxRef, { amount: 0.4 });
  const pageVisible = usePageVisible();
  const [active, setActive] = useState<Mode>("email");
  const [picked, setPicked] = useState(false);

  useEffect(() => {
    if (reduceMotion || picked || !inView || !pageVisible) return;
    const id = window.setInterval(() => {
      setActive((m) => MODES[(MODES.indexOf(m) + 1) % MODES.length]);
    }, 3200);

    return () => window.clearInterval(id);
  }, [reduceMotion, picked, inView, pageVisible]);

  return (
    <section className="pb-24 pt-10 md:pb-[140px]" id="modes">
      <div className={WRAP}>
        <Reveal>
          <div
            ref={boxRef}
            className="grid items-center gap-10 rounded-[28px] border border-[var(--dusk-line)] bg-[#0a0c1d] bg-[radial-gradient(80%_120%_at_100%_0%,rgba(129,90,220,.22),transparent_60%),radial-gradient(60%_90%_at_0%_100%,rgba(253,186,140,.10),transparent_60%)] p-6 text-start sm:p-10 lg:grid-cols-[1fr_1.15fr] lg:gap-14 lg:rounded-[32px] lg:p-14"
          >
            <div>
              <p className={EYEBROW}>{tNav("modes")}</p>
              <h2 className={`${H2} mt-3`}>{t("heading")}</h2>
              <p className={LEAD}>{t("lead")}</p>
              <div
                aria-label={tNav("modes")}
                className="mt-7 flex flex-wrap gap-2"
                role="group"
              >
                {MODES.map((mode) => (
                  <button
                    key={mode}
                    aria-controls="modes-output"
                    aria-pressed={active === mode}
                    className={`rounded-full border px-4 py-[9px] text-sm transition-[background-color,color,border-color] duration-200 ease-out focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--dusk-lav)] ${
                      active === mode
                        ? "border-white bg-white text-[#0b0b14]"
                        : "border-[var(--dusk-line)] text-[var(--dusk-mute)] hover:border-white/30 hover:text-white"
                    }`}
                    type="button"
                    onClick={() => {
                      setPicked(true);
                      setActive(mode);
                    }}
                  >
                    {t(mode)}
                  </button>
                ))}
              </div>
            </div>

            <div className="grid min-w-0 gap-3">
              <div className="rounded-2xl border border-dashed border-white/[.12] bg-white/[.03] px-5 py-[18px] text-[15px] leading-[1.55] text-[var(--dusk-mute)]">
                <span className="mb-1.5 block text-[11.5px] uppercase tracking-[1.6px] text-[var(--dusk-dim)]">
                  {t("youSaid")}
                </span>
                {t("sample")}
              </div>
              <div
                aria-live={picked ? "polite" : "off"}
                className="rounded-2xl border border-[rgba(196,181,253,.18)] bg-[#0f1228] px-5 py-[18px] text-[15px] leading-[1.55]"
                id="modes-output"
              >
                <span className="mb-1.5 block text-[11.5px] uppercase tracking-[1.6px] text-[var(--dusk-dim)]">
                  {t("hyperWhisperWrote")}
                </span>
                {/* Every output sits in one grid cell, so the box is as tall
                    as the longest from the start and never jumps. */}
                <div className="grid">
                  {MODES.map((mode) => (
                    <p
                      key={mode}
                      aria-hidden={active !== mode}
                      className={`whitespace-pre-line break-words [grid-area:1/1] transition-[opacity,filter] duration-[250ms] ease-out ${
                        active === mode
                          ? "opacity-100 blur-0"
                          : "pointer-events-none opacity-0 blur-[4px]"
                      } ${mode === "code" ? "font-mono text-[14px]" : ""}`}
                    >
                      {t(`output.${mode}`)}
                    </p>
                  ))}
                </div>
              </div>
            </div>
          </div>
        </Reveal>
      </div>
    </section>
  );
}
