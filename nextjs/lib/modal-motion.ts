// #864: the hand-rolled admin overlays (Add credits, Refund) fade their
// backdrop and fade + scale their dialog in, and reverse on close, instead of
// covering and uncovering the page in one frame. Spread onto `m.*` elements
// rendered inside <AnimatePresence>, which keeps the closed overlay mounted
// (as last rendered) until the exit finishes. Timings match the motion tokens
// in styles/globals.css: --motion-duration-fast (250 ms) in,
// --motion-duration-micro (150 ms) out, --motion-ease-smooth-out. Under
// "Reduce motion" the app's <MotionConfig reducedMotion="user"> drops the
// scale and keeps only the fade.
const EASE = [0.22, 1, 0.36, 1] as const;
const OPEN = { duration: 0.25, ease: EASE };
const CLOSE = { duration: 0.15, ease: EASE };

/** The fixed root: stops taking clicks the moment it starts to close. */
export const MODAL_ROOT = {
  exit: { pointerEvents: "none" },
} as const;

export const MODAL_BACKDROP = {
  initial: { opacity: 0 },
  animate: { opacity: 1, transition: OPEN },
  exit: { opacity: 0, transition: CLOSE },
} as const;

export const MODAL_DIALOG = {
  initial: { opacity: 0, scale: 0.96 },
  animate: { opacity: 1, scale: 1, transition: OPEN },
  exit: { opacity: 0, scale: 0.96, transition: CLOSE },
} as const;
