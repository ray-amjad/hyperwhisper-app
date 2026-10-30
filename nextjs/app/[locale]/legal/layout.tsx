import { DuskPage } from "@/components/landing/DuskPage";

/*
  The three legal pages each render <LegalDocument> (components/legal), which
  lays out the sky band, the "On this page" list and the article. The article
  keeps `overflow-wrap:anywhere`, so a long unbreakable token (the support
  address, a URL) stays inside the column instead of running off a 320px
  screen (WCAG 2.1 SC 1.4.10). `anywhere` breaks only when a line would
  otherwise overflow, so ordinary sentences are untouched; `break-all` would
  break every word and is the wrong tool.
*/
export default function LegalLayout({
  children,
}: {
  children: React.ReactNode;
}) {
  return <DuskPage>{children}</DuskPage>;
}
