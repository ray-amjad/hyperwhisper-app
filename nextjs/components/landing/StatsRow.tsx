"use client";

import { useInView, useReducedMotion } from "framer-motion";
import { useTranslations } from "next-intl";
import { useEffect, useRef, useState } from "react";

import { WRAP } from "./dusk";
import Reveal from "./Reveal";

/**
 * The figures the site already states: 5x and 99% (the old stat row), 100+
 * languages (features.languages) and 30+ transcription models
 * (cloudSection.credits.features.models, "30+ models"). Nothing new is
 * claimed here.
 */
const STATS = [
  { value: 5, suffix: "x", label: "fasterThanTyping" },
  { value: 99, suffix: "%", label: "accuracyRate" },
  { value: 100, suffix: "+", label: "languagesSupported" },
  { value: 30, suffix: "+", label: "modelsSupported" },
] as const;

/**
 * Counts up from 0 over 1.2 s (ease-out cubic) the first time it is 60% in
 * view. The server HTML carries the real figure, so a crawler or a reader
 * without JavaScript sees it. Reduce motion, or a figure already on screen at
 * mount, shows the final value with no count.
 */
function CountUp({ value, suffix }: { value: number; suffix: string }) {
  const ref = useRef<HTMLElement>(null);
  const inView = useInView(ref, { amount: 0.6, once: true });
  const reduceMotion = useReducedMotion();
  const [shown, setShown] = useState(value);
  const armed = useRef(false);

  // Arm once, at mount, while the figure is still below the fold (and still
  // hidden by its Reveal): park it at 0 so the count can play.
  useEffect(() => {
    const el = ref.current;

    if (reduceMotion || !el) return;
    if (el.getBoundingClientRect().top > window.innerHeight) {
      armed.current = true;
      setShown(0);
    }
  }, [reduceMotion]);

  useEffect(() => {
    if (!inView || !armed.current) return;
    if (reduceMotion) {
      setShown(value);

      return;
    }
    let frame = 0;
    const start = performance.now();
    const tick = (now: number) => {
      const p = Math.min(1, (now - start) / 1200);

      setShown(Math.round(value * (1 - Math.pow(1 - p, 3))));
      if (p < 1) frame = requestAnimationFrame(tick);
    };

    frame = requestAnimationFrame(tick);

    return () => cancelAnimationFrame(frame);
  }, [inView, reduceMotion, value]);

  return (
    <b
      ref={ref}
      className="block text-[44px] font-semibold tracking-[-2px] tabular-nums md:text-[56px]"
    >
      {shown}
      {suffix}
    </b>
  );
}

export default function StatsRow() {
  const t = useTranslations("demo");

  return (
    <section>
      <div className={WRAP}>
        {/* gap-px over the line colour draws the dividers on any column count. */}
        <div className="grid grid-cols-2 gap-px border-y border-[var(--dusk-line)] bg-[var(--dusk-line)] lg:grid-cols-4">
          {STATS.map((stat, i) => (
            // The cell's own background stays put; only its content fades, so
            // the divider colour under it never shows through as a block.
            <div key={stat.label} className="bg-[var(--dusk-night)]">
              <Reveal
                className="flex flex-col-reverse px-5 py-8 md:px-8 md:py-11"
                delay={i * 100}
              >
                <p className="text-sm text-[var(--dusk-mute)] md:text-[14.5px]">
                  {t(stat.label)}
                </p>
                <p>
                  <CountUp suffix={stat.suffix} value={stat.value} />
                </p>
              </Reveal>
            </div>
          ))}
        </div>
      </div>
    </section>
  );
}
