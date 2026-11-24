import { LoaderCircle } from "lucide-react";

/**
 * The one "busy" indicator for the account area (#827). Size and colour come
 * from `className`; the glyph strokes in `currentColor`, so state is CSS.
 *
 * Decorative (`aria-hidden`) by default, for a site whose text already says
 * what is happening. Pass `label` where the spinner is the only busy cue: it
 * becomes a `role="status"` named by the label.
 */
export function Spinner({
  className = "w-4 h-4",
  label,
}: {
  className?: string;
  label?: string;
}) {
  const a11y = label
    ? { role: "status", "aria-label": label }
    : { "aria-hidden": true as const };

  return <LoaderCircle {...a11y} className={`animate-spin ${className}`} />;
}
