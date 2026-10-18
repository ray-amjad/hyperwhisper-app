// The one inline text-link recipe for body copy on the marketing site (#892).
// An underline marks a link without relying on hue alone against gray body text.
export const TEXT_LINK =
  "text-purple-400 underline underline-offset-4 transition-colors hover:text-purple-300";

// The same recipe for links inside a `prose` block (blog posts, open-source page).
export const PROSE_TEXT_LINK =
  "prose-a:text-purple-400 prose-a:underline prose-a:underline-offset-4 prose-a:transition-colors hover:prose-a:text-purple-300";
