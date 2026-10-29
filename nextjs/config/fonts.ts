import { Inter as FontSans, JetBrains_Mono } from "next/font/google";

export const fontSans = FontSans({
  subsets: ["latin"],
  variable: "--font-sans",
});

// Key caps on the homepage ("How it works"). Only the two weights it uses.
export const fontMono = JetBrains_Mono({
  subsets: ["latin"],
  weight: ["400", "500"],
  variable: "--font-mono-keys",
});
