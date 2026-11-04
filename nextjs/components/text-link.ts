// The one inline text-link recipe for body copy on the marketing site (#892),
// moved to the Dusk lavender token (#2913) so the home FAQ and blog posts match
// /choosing-a-model, the legal pages and /open-source: `--dusk-lav` with a 40%
// lavender underline, both going white on hover.
// An underline marks a link without relying on hue alone against gray body text.
// Not `LINK_LAV` (components/landing/dusk-pages.ts): that is the standalone,
// no-underline "→ more" link, a different object.
export const TEXT_LINK =
  "text-[var(--dusk-lav)] underline decoration-[rgba(196,181,253,.4)] underline-offset-4 transition-colors hover:text-white hover:decoration-white";

// The same recipe for links inside a `prose` block (blog posts, open-source page).
export const PROSE_TEXT_LINK =
  "prose-a:text-[var(--dusk-lav)] prose-a:underline prose-a:decoration-[rgba(196,181,253,.4)] prose-a:font-normal prose-a:underline-offset-4 prose-a:transition-colors prose-a:hover:text-white prose-a:hover:decoration-white";
