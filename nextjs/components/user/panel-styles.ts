/**
 * Shared class strings for the account portal (the customer dashboard and the
 * admin pages under app/[locale]/user/(authenticated)). They are the Dusk
 * homepage's look (components/landing/dusk.ts, the --dusk-* tokens in
 * styles/globals.css) sized down for a dense working UI: night background,
 * glass cards, uppercase dim labels, the latency page's table headers, and
 * the homepage's white and glass pill buttons.
 *
 * Class strings only. Nothing here changes what a control does.
 */
import { BTN_GLASS, BTN_WHITE } from "@/components/landing/dusk";

/** The portal page background: night, with a faint violet glow at the top. */
export const PANEL_BG =
  "bg-[var(--dusk-night)] bg-[radial-gradient(1100px_520px_at_50%_-8%,rgba(129,90,220,.16),transparent_62%),linear-gradient(180deg,var(--dusk-navy),var(--dusk-night)_420px)] text-[var(--dusk-ink)]";

/** A glass card: the mockups' `.glass`, with the portal's tighter radius. */
export const CARD =
  "rounded-[20px] border border-[var(--dusk-line)] bg-[var(--dusk-glass)]";

/** The highlighted card (the mockups' buy card): a soft violet wash. */
export const CARD_ACCENT =
  "rounded-[20px] border border-[rgba(196,181,253,.24)] bg-[linear-gradient(180deg,rgba(129,90,220,.13),rgba(255,255,255,.03))]";

/** A card's small uppercase title, like the mockups' `.label`. */
export const LABEL =
  "text-[11.5px] font-medium uppercase tracking-[1.6px] text-[var(--dusk-dim)]";

/** The page heading in the portal. */
export const PAGE_TITLE =
  "text-[26px] md:text-[30px] font-semibold leading-[1.15] tracking-[-0.03em] text-[var(--dusk-ink)] [overflow-wrap:anywhere]";

/** The line under a page heading. */
export const PAGE_SUB = "mt-1.5 text-sm text-[var(--dusk-mute)]";

/** A text input or number box, like the mockups' `.inp`. */
export const INPUT =
  "rounded-xl border border-[#2a2e4a] bg-[#090b18] text-[var(--dusk-ink)] placeholder:text-[var(--dusk-dim)] transition-[border-color,box-shadow] duration-150 ease-out focus:border-[rgba(196,181,253,.55)] focus:outline-none focus:ring-2 focus:ring-[rgba(196,181,253,.18)] motion-reduce:transition-none";

/** The portal's button size: the homepage pills, one step smaller. */
const SM = "px-4 py-2 text-sm";

/** Primary action: the homepage's white pill. */
export const BTN_PRIMARY = `${BTN_WHITE} ${SM} disabled:opacity-50 disabled:cursor-not-allowed disabled:active:scale-100`;

/** Secondary action: the homepage's glass pill. */
export const BTN_SECONDARY = `${BTN_GLASS} ${SM} disabled:opacity-50 disabled:cursor-not-allowed disabled:active:scale-100`;

/** The same two pills at the size of an inline row action. */
const XS = "px-3.5 py-1.5 text-[13px]";

export const BTN_PRIMARY_XS = `${BTN_WHITE} ${XS} disabled:opacity-50 disabled:cursor-not-allowed disabled:active:scale-100`;
export const BTN_SECONDARY_XS = `${BTN_GLASS} ${XS} disabled:opacity-50 disabled:cursor-not-allowed disabled:active:scale-100`;

/** A destructive action (refund): solid rose, pill-shaped like the rest. */
export const BTN_DANGER =
  "inline-flex items-center justify-center gap-2 rounded-full bg-rose-500/90 px-4 py-2 text-sm font-semibold text-white transition-[background-color,opacity,scale] duration-150 ease-out hover:bg-rose-500 active:scale-[.96] focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--dusk-lav)] disabled:opacity-50 disabled:active:scale-100 motion-reduce:transition-none";

/** A small ghost icon button (show/hide, copy, close). */
export const ICON_BTN =
  "rounded-lg text-[var(--dusk-mute)] transition-colors hover:bg-white/[.07] hover:text-white focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--dusk-lav)]";

/** An inline text action in the lavender accent ("Show all", "Get a key"). */
export const LINK_ACCENT =
  "text-[var(--dusk-lav)] transition-colors hover:text-white";

/** A small rounded chip. Pair it with one of the tones below. */
export const CHIP =
  "inline-flex items-center rounded-full px-2.5 py-0.5 text-[11.5px] font-medium";
export const CHIP_LAV =
  "bg-[rgba(196,181,253,.13)] text-[var(--dusk-lav)] ring-1 ring-inset ring-[rgba(196,181,253,.22)]";
export const CHIP_PEACH =
  "bg-[rgba(253,186,140,.12)] text-[var(--dusk-peach)] ring-1 ring-inset ring-[rgba(253,186,140,.22)]";
export const CHIP_MUTE =
  "bg-white/[.06] text-[var(--dusk-mute)] ring-1 ring-inset ring-white/[.08]";
export const CHIP_ROSE =
  "bg-rose-500/[.14] text-rose-300 ring-1 ring-inset ring-rose-400/25";

/** The pill segmented control (the mockups' `.seg`). */
export const SEG =
  "inline-flex gap-1 rounded-full border border-[var(--dusk-line)] bg-white/[.05] p-1";
export const SEG_ITEM =
  "rounded-full px-3.5 py-1.5 text-sm transition-[background-color,color] duration-150 ease-out focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--dusk-lav)] motion-reduce:transition-none";
export const SEG_ON = "bg-white text-[#0b0b14]";
export const SEG_OFF = "text-[var(--dusk-mute)] hover:text-white";

/** A table header cell, like the latency heatmap's `.hh`. */
export const TH =
  "px-6 py-3.5 text-left text-[11.5px] font-medium uppercase tracking-[1.3px] text-[var(--dusk-dim)]";

/** The row divider and hover inside a portal table. */
export const TBODY = "divide-y divide-[var(--dusk-line)]";
export const TR_HOVER = "transition-colors hover:bg-white/[.025]";

/** A skeleton bar. It pulses, and stops under Reduce motion. */
export const SKEL = "rounded bg-white/[.08] animate-pulse motion-reduce:animate-none";

/** Alerts: a refusal and a success, as soft tinted boxes. */
export const ALERT_ERROR =
  "rounded-2xl border border-rose-400/25 bg-rose-500/[.09]";
export const ALERT_OK =
  "rounded-2xl border border-[rgba(196,181,253,.28)] bg-[rgba(196,181,253,.09)]";
