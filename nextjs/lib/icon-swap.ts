// #727: cross-fade a state icon swap (copy -> check, show -> hide) instead of
// replacing the glyph in one frame. Spread onto an `m.*` element keyed by the
// state, inside <AnimatePresence mode="popLayout">.
export const ICON_SWAP = {
  initial: { opacity: 0, scale: 0.25, filter: "blur(4px)" },
  animate: { opacity: 1, scale: 1, filter: "blur(0px)" },
  exit: { opacity: 0, scale: 0.25, filter: "blur(4px)" },
  transition: { type: "spring", duration: 0.3, bounce: 0 },
} as const;
