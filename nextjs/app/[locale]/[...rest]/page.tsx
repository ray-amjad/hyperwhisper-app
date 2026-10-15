import { notFound } from "next/navigation";

// Every unmatched URL under a locale (/de/zzz) lands here and 404s inside the
// real [locale] layout, so [locale]/not-found.tsx renders it with the locale
// from the params (next-intl's documented catch-all).
//
// It used to fall through to app/not-found.tsx, which read the locale with
// getLocale(). That is a headers() call, and Next renders the root not-found
// boundary on every page, so it made every route dynamic (#843).
//
// Thrown from generateMetadata too, or the browser keeps the layout's metadata
// instead of the 404's (#815).
export function generateMetadata(): never {
  notFound();
}

export default function CatchAllNotFound(): never {
  notFound();
}
