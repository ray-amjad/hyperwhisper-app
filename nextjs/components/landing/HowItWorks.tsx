"use client";

import { Modal, ModalBody, ModalContent } from "@heroui/modal";
import { useInView, useReducedMotion } from "framer-motion";
import { Play, X } from "lucide-react";
import { useTranslations } from "next-intl";
import { useEffect, useRef, useState } from "react";

import { EYEBROW, H2, LEAD, WRAP } from "./dusk";
import Reveal from "./Reveal";
import { usePageVisible } from "./use-page-visible";
import VideoDemo from "./VideoDemo";

/**
 * The app's real default recording shortcut. Both are a toggle (press to
 * start, press again to stop); push-to-talk is off by default everywhere.
 *   macOS:          ⌥ Space   app/macos/.../KeyboardShortcuts+Names.swift
 *   Windows, Linux: Ctrl + Alt app/windows/.../SettingsService.Shortcuts.cs,
 *                              app/shared-dotnet/.../SettingsViewModel.cs
 * The Mac one is served; a Windows or Linux browser switches after mount.
 */
const MAC_SHORTCUT = "⌥ Space";
const PC_SHORTCUT = "Ctrl + Alt";

function detectShortcut(): string {
  const ua = navigator.userAgent;

  if (/Windows/i.test(ua)) return PC_SHORTCUT;
  if (/Linux|X11/i.test(ua) && !/Android/i.test(ua)) return PC_SHORTCUT;

  return MAC_SHORTCUT;
}

type Phase = "idle" | "listening" | "cleaning" | "typing";

const WAVE = Array.from({ length: 14 }, (_, i) => ({
  animationDelay: `${(i * 73) % 600}ms`,
  animationDuration: `${700 + ((i * 137) % 500)}ms`,
}));

/**
 * Words for scripts that use spaces, characters for those that do not (ja,
 * zh, th), so the reply still types in steps.
 */
function chunks(text: string): string[] {
  if (!text.includes(" ")) return Array.from(text);
  // Split on the space and put it back, rather than a lookbehind regex:
  // Safari before 16.4 cannot parse `(?<= )`, and the whole chunk would fail.
  const words = text.split(" ");

  return words
    .map((word, i) => (i < words.length - 1 ? `${word} ` : word))
    .filter((part) => part !== "");
}

export default function HowItWorks() {
  const t = useTranslations("demo");
  const reduceMotion = useReducedMotion();
  const appRef = useRef<HTMLDivElement>(null);
  // Not `once`: the loop stops when the window leaves the view, or the tab is
  // hidden, and starts over when both are back.
  const inView = useInView(appRef, { amount: 0.4 });
  const pageVisible = usePageVisible();
  const [shortcut, setShortcut] = useState(MAC_SHORTCUT);
  const [phase, setPhase] = useState<Phase>("idle");
  const [typed, setTyped] = useState("");
  const [videoOpen, setVideoOpen] = useState(false);
  const reply = t("typed");

  useEffect(() => setShortcut(detectShortcut()), []);

  // The loop: idle -> listening -> cleaning up -> the reply types in -> hold.
  // It runs while the window is 40% in view in a visible tab, from the top
  // each time it comes back; a pause drops back to idle, so the Listening HUD
  // never freezes on screen. Reduce motion shows the end state and never
  // advances.
  useEffect(() => {
    if (reduceMotion || !inView || !pageVisible) return;
    let cancelled = false;
    const timers = new Set<number>();
    const wait = (ms: number) =>
      new Promise<void>((resolve) => {
        const id = window.setTimeout(() => {
          timers.delete(id);
          resolve();
        }, ms);

        timers.add(id);
      });

    (async () => {
      const parts = chunks(reply);
      const step = reply.includes(" ") ? 38 : 30;

      while (!cancelled) {
        setTyped("");
        setPhase("idle");
        await wait(900);
        if (cancelled) return;
        setPhase("listening");
        await wait(2600);
        if (cancelled) return;
        setPhase("cleaning");
        await wait(700);
        if (cancelled) return;
        setPhase("typing");
        let text = "";

        for (const part of parts) {
          if (cancelled) return;
          text += part;
          setTyped(text);
          await wait(step);
        }
        await wait(3200);
      }
    })();

    return () => {
      cancelled = true;
      timers.forEach((id) => window.clearTimeout(id));
      timers.clear();
      setPhase("idle");
      setTyped("");
    };
  }, [reduceMotion, inView, pageVisible, reply]);

  const shownPhase: Phase = reduceMotion ? "typing" : phase;
  const shownText = reduceMotion ? reply : typed;
  const activeStep =
    shownPhase === "idle" ? 0 : shownPhase === "typing" ? 2 : 1;
  const hudOn = shownPhase === "listening" || shownPhase === "cleaning";
  // The bars move only while the HUD shows and the loop runs, so a hidden HUD
  // (scrolled away, background tab, reduced motion) costs no animation frames.
  const waveOn = hudOn && !reduceMotion && inView && pageVisible;

  const steps = [
    { key: shortcut, label: t("step1") },
    { key: t("step2Key"), label: t("step2") },
    { key: t("step3Key"), label: t("step3") },
  ];

  return (
    <section className="pb-24 pt-10 md:pb-[130px]" id="demo">
      <div
        className={`${WRAP} grid items-center gap-12 lg:grid-cols-[.9fr_1.1fr] lg:gap-16`}
      >
        <div>
          <Reveal className={EYEBROW}>{t("eyebrow")}</Reveal>
          <Reveal delay={80}>
            <h2 className={`${H2} mt-3`}>
              {t("titleLine1")}
              <br />
              {t("titleLine2")}{" "}
              <span className="dusk-grad">{t("titleAccent")}</span>
            </h2>
          </Reveal>
          <Reveal as="p" className={LEAD} delay={160}>
            {t("lead")}
          </Reveal>

          <ol className="mt-[34px] grid gap-3">
            {steps.map((step, i) => (
              <Reveal
                key={i}
                as="li"
                className={`flex items-center gap-[14px] rounded-[14px] border px-4 py-[14px] transition-[background-color,border-color,color] duration-300 ease-out ${
                  i === activeStep
                    ? "border-[var(--dusk-line)] bg-[var(--dusk-glass)] text-white"
                    : "border-transparent text-[var(--dusk-mute)]"
                }`}
                delay={220 + i * 80}
              >
                <kbd className="min-w-[54px] shrink-0 whitespace-nowrap rounded-[7px] border border-b-2 border-[#2a2f52] bg-[#151830] px-2 py-1 text-center font-[family-name:var(--font-mono-keys)] text-xs text-[#dcdcff]">
                  {step.key}
                </kbd>
                <span className="min-w-0">{step.label}</span>
              </Reveal>
            ))}
          </ol>

          <Reveal delay={460}>
            <button
              className="mt-6 inline-flex items-center gap-2 rounded-full text-sm font-medium text-[var(--dusk-lav)] transition-colors hover:text-white focus-visible:outline-2 focus-visible:outline-offset-4 focus-visible:outline-[var(--dusk-lav)]"
              type="button"
              onClick={() => setVideoOpen(true)}
            >
              <Play aria-hidden="true" className="size-4" />
              {t("watchVideo")}
            </button>
          </Reveal>
        </div>

        <Reveal delay={200}>
          <div
            ref={appRef}
            aria-label={t("exampleLabel")}
            className="relative overflow-hidden rounded-[18px] border border-[var(--dusk-line)] bg-[#0e1022] text-start shadow-[0_40px_100px_rgba(0,0,0,.55),0_0_0_1px_rgba(196,181,253,.06)]"
            role="img"
          >
            <div
              aria-hidden="true"
              className="pointer-events-none absolute inset-x-[-10%] top-[-40%] h-[70%] bg-[radial-gradient(closest-side,rgba(196,181,253,.16),transparent)]"
            />
            <div className="flex items-center gap-2 border-b border-[var(--dusk-line)] px-4 py-[13px] text-[12.5px] text-[var(--dusk-mute)]">
              <i className="size-[11px] rounded-full bg-[#2c2f46]" />
              <i className="size-[11px] rounded-full bg-[#2c2f46]" />
              <i className="size-[11px] rounded-full bg-[#2c2f46]" />
              <img
                alt=""
                className="ms-2.5 size-[18px] rounded-[5px] bg-white"
                height={18}
                loading="lazy"
                src="/landing/logos/slack.webp"
                width={18}
              />
              <span dir="ltr">#launch · Slack</span>
            </div>

            <div className="px-5 pb-[90px] pt-[22px] text-sm leading-[1.6] sm:px-6">
              <div className="mb-4 flex gap-3">
                <div className="size-8 flex-none rounded-lg bg-gradient-to-br from-[#fdba8c] to-[#f472b6]" />
                <div className="min-w-0">
                  <b className="text-sm">Maya</b>
                  <small className="ms-1.5 text-[var(--dusk-dim)]">10:42</small>
                  <p className="text-[#c9cbe0]">{t("message1")}</p>
                </div>
              </div>
              <div className="mb-4 flex gap-3">
                <div className="size-8 flex-none rounded-lg bg-gradient-to-br from-[#6366f1] to-[#f0abfc]" />
                <div className="min-w-0">
                  <b className="text-sm">Sam</b>
                  <small className="ms-1.5 text-[var(--dusk-dim)]">10:44</small>
                  <p className="text-[#c9cbe0]">{t("message2")}</p>
                </div>
              </div>

              {/* The full reply sits invisibly in the same grid cell as the
                  typed one, so the box is its final height from the start and
                  nothing moves while it types. */}
              <div className="mt-5 grid min-h-[86px] rounded-xl border border-[#2a2e4a] bg-[#0b0d1c] px-4 py-[14px] text-[15px] text-white">
                <span className="invisible [grid-area:1/1]">{reply}</span>
                <span className="[grid-area:1/1]">
                  {shownText === "" ? (
                    <span className="text-[var(--dusk-dim)]">
                      {t("composePlaceholder")}
                    </span>
                  ) : (
                    <>
                      {shownText}
                      {shownPhase === "typing" && !reduceMotion ? (
                        <span className="dusk-caret ms-px inline-block h-[17px] w-0.5 bg-[var(--dusk-lav)] align-[-3px]" />
                      ) : null}
                    </>
                  )}
                </span>
              </div>
            </div>

            <div
              className={`absolute bottom-[22px] left-1/2 flex -translate-x-1/2 items-center gap-3 whitespace-nowrap rounded-full border border-white/[.12] bg-black/75 py-2.5 ps-[14px] pe-[18px] text-[13px] backdrop-blur-[12px] transition-[opacity,scale] duration-300 ease-[var(--dusk-ease)] ${
                hudOn ? "scale-100 opacity-100" : "scale-90 opacity-0"
              }`}
            >
              <span className="size-[7px] rounded-full bg-[#f87171] shadow-[0_0_0_4px_rgba(248,113,113,.2)]" />
              <div className="flex h-5 items-center gap-[3px]">
                {WAVE.map((style, i) => (
                  <i
                    key={i}
                    className={`${waveOn ? "dusk-wave-bar " : ""}h-full w-[3px] rounded-sm bg-gradient-to-b from-[#c4b5fd] to-[#f0abfc]`}
                    style={style}
                  />
                ))}
              </div>
              <span>
                {shownPhase === "cleaning" ? t("cleaningUp") : t("listening")}
              </span>
            </div>
          </div>
        </Reveal>
      </div>

      {/* HeroUI's own close button is always labelled "Close" in English, so
          this one is ours, with a translated label. */}
      <Modal
        hideCloseButton
        classNames={{ base: "bg-[#0a0c1d] border border-[var(--dusk-line)]" }}
        isOpen={videoOpen}
        placement="center"
        size="5xl"
        onClose={() => setVideoOpen(false)}
      >
        <ModalContent aria-label={t("watchVideo")}>
          {(onClose) => (
            <ModalBody className="p-3 pt-12 sm:p-4 sm:pt-12">
              <button
                aria-label={t("closeVideo")}
                className="absolute end-2 top-2 grid size-9 place-items-center rounded-full text-white/70 transition-colors hover:bg-white/10 hover:text-white focus-visible:outline-2 focus-visible:outline-[var(--dusk-lav)]"
                type="button"
                onClick={onClose}
              >
                <X aria-hidden="true" className="size-5" />
              </button>
              <VideoDemo />
            </ModalBody>
          )}
        </ModalContent>
      </Modal>
    </section>
  );
}
