import { notFound } from "next/navigation";

// An unmatched URL under a locale (/de/zzz) would otherwise skip the [locale]
// layout and render the bare app/not-found.tsx. Catching it here and throwing
// notFound() renders [locale]/not-found.tsx with the site chrome instead, and
// still answers 404 (#815). Every real route outranks a catch-all.
export default function CatchAllNotFound() {
  notFound();
}
