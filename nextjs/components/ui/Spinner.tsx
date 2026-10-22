import { LoaderCircle } from "lucide-react";

/**
 * The one "busy" indicator for the account area (#827). Size and colour come
 * from `className`; the glyph strokes in `currentColor`, so state is CSS.
 */
export function Spinner({ className = "w-4 h-4" }: { className?: string }) {
  return <LoaderCircle aria-hidden className={`animate-spin ${className}`} />;
}
