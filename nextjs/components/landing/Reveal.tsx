"use client";

import { m, useReducedMotion, type HTMLMotionProps } from "framer-motion";

/**
 * A block that enters once as it scrolls into view: 400 ms ease-out, up 12px
 * and out of a 4px blur, after `delay` ms (the ~100 ms stagger between
 * siblings is the caller's `delay`).
 *
 * Reduce motion turns the entrance off completely. Before hydration the
 * `[data-reveal]` rule in globals.css shows the block from first paint; after
 * it, `animate` settles it at the shown state with no tween. `initial` is the
 * same either way, so the server render and hydration still match (#1180).
 * Only transform, opacity and filter move, so nothing around it shifts.
 *
 * With JavaScript off the block would stay hidden, so the homepage carries a
 * <noscript> style that shows every reveal at once.
 *
 * Once it has entered, the filter is cleared to `none`: even `blur(0px)` makes
 * the block a backdrop root, and a `backdrop-blur` button inside it (the glass
 * buttons in Pricing and the closing section) would stop frosting. y settles
 * at 0, which framer-motion already writes as `transform: none`.
 */
const HIDDEN = { opacity: 0, y: 12, filter: "blur(4px)" };
const SHOWN = {
  opacity: 1,
  y: 0,
  filter: "blur(0px)",
  transitionEnd: { filter: "none" },
};

type RevealTag = "div" | "li" | "p" | "span";

type RevealProps = Omit<
  HTMLMotionProps<"div">,
  "initial" | "animate" | "whileInView" | "transition" | "viewport"
> & {
  as?: RevealTag;
  /** Milliseconds after the block comes into view. */
  delay?: number;
  /**
   * IntersectionObserver rootMargin for the trigger. A positive bottom margin
   * reveals the block before it reaches the viewport.
   */
  margin?: string;
};

export default function Reveal({
  as = "div",
  delay = 0,
  margin,
  ...rest
}: RevealProps) {
  const reduceMotion = useReducedMotion();

  // One prop shape for every tag here; m.div's is the widest of them.
  const Tag = m[as] as typeof m.div;

  return (
    <Tag
      animate={reduceMotion ? SHOWN : undefined}
      data-reveal=""
      initial={HIDDEN}
      transition={
        reduceMotion
          ? { duration: 0 }
          : { duration: 0.4, ease: "easeOut", delay: delay / 1000 }
      }
      viewport={{ once: true, amount: 0.15, margin }}
      whileInView={SHOWN}
      {...rest}
    />
  );
}
