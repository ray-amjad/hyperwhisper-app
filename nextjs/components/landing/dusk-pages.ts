/**
 * Shared class strings for the Dusk sub-pages (download, credits, support,
 * legal, blog and the rest). They build on the homepage tokens in ./dusk.ts
 * and the --dusk-* colours in styles/globals.css.
 */

/** Page title in the sky band. 30px on a 320px phone (#1965), 64px on a desktop. */
export const PAGE_H1 =
  "text-3xl sm:text-4xl md:text-5xl lg:text-[64px] font-semibold leading-[1.04] tracking-[-0.0375em] text-white [overflow-wrap:anywhere] [text-wrap:balance] [text-shadow:0_4px_40px_rgba(10,10,40,.35)]";

/** A frosted panel with a hairline edge. */
export const GLASS =
  "rounded-[22px] border border-[var(--dusk-line)] bg-[var(--dusk-glass)]";

/** The highlighted panel: a violet wash and a lavender edge. */
export const GLASS_ON =
  "rounded-[22px] border border-[rgba(196,181,253,.35)] bg-[linear-gradient(180deg,rgba(129,90,220,.2),rgba(255,255,255,.03))]";

/** Small caps label above a field or a value. */
export const LABEL =
  "text-[11.5px] uppercase tracking-[1.6px] text-[var(--dusk-dim)]";

/** The body section under the sky band. */
export const SECTION = "pt-[30px] pb-20 md:pb-[110px]";

/** An inline lavender link with an arrow or a short label. */
export const LINK_LAV =
  "rounded text-[14.5px] text-[var(--dusk-lav)] underline-offset-4 transition-colors hover:text-white hover:underline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--dusk-lav)]";

/** A pill segmented control's track and its options. */
export const SEG =
  "inline-flex flex-wrap gap-1 rounded-[22px] border border-[var(--dusk-line)] bg-white/[.05] p-[5px]";
export const SEG_ITEM =
  "inline-flex items-center justify-center gap-2 rounded-full px-[18px] py-[9px] text-sm transition-colors duration-150 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--dusk-lav)] [&_svg]:size-[15px]";
export const SEG_ON = "bg-white text-[#0b0b14]";
export const SEG_OFF = "text-[var(--dusk-mute)] hover:text-white";

/** A text field on a dark panel. */
export const INPUT =
  "w-full rounded-xl border border-[#2a2e4a] bg-[#090b18] px-4 py-[13px] text-[15px] text-white placeholder:text-[var(--dusk-dim)] transition-colors focus-visible:border-[var(--dusk-lav)] focus-visible:outline-none";

/** The lavender icon tile used in feature lists. */
export const ICON_TILE =
  "grid size-[34px] shrink-0 place-items-center rounded-[10px] bg-[rgba(196,181,253,.12)] text-[var(--dusk-lav)] [&_svg]:size-[17px]";
