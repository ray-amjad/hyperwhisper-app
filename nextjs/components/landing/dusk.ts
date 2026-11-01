/**
 * Shared class strings for the Dusk homepage, the navbar and the footer. The
 * colour tokens (--dusk-*) and the keyframes live in styles/globals.css.
 */

/** The page column: 1200px wide, 24px gutters on a phone, 32px from md. */
export const WRAP = "mx-auto w-full max-w-[1200px] px-6 md:px-8";

const BTN =
  "inline-flex items-center justify-center gap-[9px] rounded-full font-semibold transition-[background-color,opacity,scale] duration-150 ease-out active:scale-[.96] focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--dusk-lav)] [&_svg]:size-4 [&_svg]:shrink-0";

/** Primary pill: white on dusk. */
export const BTN_WHITE = `${BTN} bg-white text-[#0b0b14] hover:bg-[#ececf5]`;

/** Secondary pill: dark glass with a hairline edge. */
export const BTN_GLASS = `${BTN} border border-white/[.16] bg-[rgba(10,10,24,.5)] text-white backdrop-blur-[14px] hover:bg-[rgba(10,10,24,.7)]`;

/** The large button size used in the hero and the closing section. */
export const BTN_LG = "px-[22px] py-[13px] text-[15px]";

export const EYEBROW =
  "text-[12.5px] uppercase tracking-[2.4px] text-[var(--dusk-lav)]/80";

/**
 * Section titles. 30px on a phone so a long translated word still fits a
 * 320px screen (#1965), 52px on a desktop as in the design.
 */
export const H2 =
  "text-3xl sm:text-4xl md:text-5xl lg:text-[52px] font-semibold leading-[1.05] tracking-[-0.035em] text-[var(--dusk-ink)] [overflow-wrap:anywhere] [text-wrap:balance]";

export const LEAD =
  "mt-4 text-base md:text-lg leading-[1.55] text-[var(--dusk-mute)]";
