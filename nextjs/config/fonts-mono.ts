import { JetBrains_Mono } from "next/font/google";

/**
 * Key caps on the homepage ("How it works"), in the two weights it uses.
 * Its own module, imported only by app/[locale]/page.tsx: next/font preloads
 * a font on every route whose module graph imports it, and config/fonts.ts is
 * imported by the root layout, so a font there was preloaded site-wide.
 */
export const fontMono = JetBrains_Mono({
  subsets: ["latin"],
  weight: ["400", "500"],
  variable: "--font-mono-keys",
});
