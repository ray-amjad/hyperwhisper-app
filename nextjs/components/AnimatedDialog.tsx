"use client";

import type { ReactNode } from "react";
import { m, useIsPresent } from "framer-motion";

/**
 * #864: the hand-rolled admin overlays (Add credits, Refund) used to cover and
 * uncover the page in one frame. Render this as the keyed child of an
 * <AnimatePresence>: the backdrop fades, the dialog fades and scales from
 * 0.96, 250 ms in and 150 ms out. Timings match the motion tokens in
 * styles/globals.css (--motion-duration-fast in, --motion-duration-micro
 * out, --motion-ease-smooth-out). Under "Reduce motion" the app's
 * <MotionConfig reducedMotion="user"> drops the scale and keeps the fade.
 *
 * AnimatePresence keeps a closed overlay mounted, as last rendered, until its
 * exit ends. That copy still holds its old handlers (a Refund button keeps the
 * old target), so while it exits it is `inert`: no click, no focus, no key
 * press reaches it, and a click lands on the page below.
 */
const EASE = [0.22, 1, 0.36, 1] as const;
const OPEN = { duration: 0.25, ease: EASE };
const CLOSE = { duration: 0.15, ease: EASE };

export const DIALOG_BACKDROP = {
  initial: { opacity: 0 },
  animate: { opacity: 1, transition: OPEN },
  exit: { opacity: 0, transition: CLOSE },
} as const;

export const DIALOG_PANEL = {
  initial: { opacity: 0, scale: 0.96 },
  animate: { opacity: 1, scale: 1, transition: OPEN },
  exit: { opacity: 0, scale: 0.96, transition: CLOSE },
} as const;

export function AnimatedDialog({
  onBackdropClick,
  children,
}: {
  onBackdropClick: () => void;
  children: ReactNode;
}) {
  const isPresent = useIsPresent();

  return (
    <div
      className={`fixed inset-0 z-50 flex items-center justify-center${isPresent ? "" : " pointer-events-none"}`}
      data-closing={isPresent ? undefined : ""}
      inert={!isPresent}
    >
      <m.div
        {...DIALOG_BACKDROP}
        className="absolute inset-0 bg-black/60 backdrop-blur-sm"
        onClick={onBackdropClick}
      />
      <m.div
        {...DIALOG_PANEL}
        className="relative w-full max-w-md mx-4 p-6 space-y-5 rounded-[22px] border border-white/[.14] bg-[rgba(10,12,28,.86)] shadow-[0_24px_80px_rgba(0,0,0,.6)] backdrop-blur-[22px]"
      >
        {children}
      </m.div>
    </div>
  );
}
