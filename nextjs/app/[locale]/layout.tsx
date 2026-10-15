import "@/styles/globals.css";

// Polyfill localStorage for SSR to prevent "localStorage.getItem is not a function" errors
if (typeof window === "undefined") {
  const noop = () => {};

  // Typed against the real `Storage` interface, so the polyfill cannot drift
  // out of shape without a type error.
  const storagePolyfill: Storage = {
    getItem: () => null,
    setItem: noop,
    removeItem: noop,
    clear: noop,
    key: () => null,
    length: 0,
  };

  (globalThis as typeof globalThis & { localStorage: Storage }).localStorage =
    storagePolyfill;
}

import { Viewport } from "next";
import clsx from "clsx";
import { NextIntlClientProvider } from "next-intl";
import {
  getMessages,
  getTranslations,
  setRequestLocale,
} from "next-intl/server";
import { notFound } from "next/navigation";

import { Providers } from "./providers";

import { fontSans } from "@/config/fonts";
import HtmlDocument from "@/components/layout/HtmlDocument";
import LayoutWrapper from "@/components/layout/LayoutWrapper";
import {
  localeOpenGraph,
  TWITTER_CARD,
  TWITTER_CREATOR,
  TWITTER_IMAGES,
} from "@/lib/site-metadata";
import { isSupportedLocale, locales } from "@/src/i18n/locales";

type Props = {
  children: React.ReactNode;
  params: Promise<{ locale: string }>;
};

export function generateStaticParams() {
  return locales.map((locale) => ({ locale }));
}

export async function generateMetadata({ params }: Props) {
  const { locale } = await params;

  // The proxy skips any path with a dot, so /favicon.ico or /.env reaches this
  // segment with a locale that is not one (#1853). 404 before the canonical is
  // built from it.
  if (!isSupportedLocale(locale)) notFound();

  const t = await getTranslations({ locale, namespace: "metadata" });

  // Only locale-derived fields. The canonical, the hreflang map and og:url
  // depend on the child path, which a layout can only learn from headers(), and
  // that made every route dynamic (#843). Each page sets them with
  // localizedPageMetadata (or englishStaticPageMetadata) instead.
  return {
    title: {
      default: t("title"),
      template: `%s - HyperWhisper`,
    },
    description: t("description"),
    keywords: [
      "voice transcription",
      "speech to text",
      "AI transcription",
      "macOS app",
      "Windows app",
      "Linux app",
      "whisper AI",
      "dictation software",
      "voice typing",
      "productivity tool",
      "audio transcription",
      "speech recognition",
    ],
    authors: [{ name: "HyperWhisper" }],
    creator: "HyperWhisper",
    openGraph: localeOpenGraph(locale, t("title"), t("description")),
    twitter: {
      card: TWITTER_CARD,
      title: t("title"),
      description: t("description"),
      creator: TWITTER_CREATOR,
      images: TWITTER_IMAGES,
    },
    robots: {
      index: true,
      follow: true,
      googleBot: {
        index: true,
        follow: true,
        "max-video-preview": -1,
        "max-image-preview": "large",
        "max-snippet": -1,
      },
    },
    icons: {
      icon: "/icon/32.png",
      apple: "/icon/256.png",
    },
    metadataBase: new URL("https://hyperwhisper.com"),
  };
}

export const viewport: Viewport = {
  themeColor: [
    { media: "(prefers-color-scheme: light)", color: "white" },
    { media: "(prefers-color-scheme: dark)", color: "black" },
  ],
};

export default async function LocaleLayout({ children, params }: Props) {
  // Await params to access locale (Next.js 15 requirement)
  const { locale } = await params;

  // See generateMetadata: an unknown first segment is a 404, not a page (#1853).
  if (!isSupportedLocale(locale)) notFound();

  // Enable static rendering
  setRequestLocale(locale);

  // Providing all messages to the client
  // side is the easiest way to get started
  const messages = await getMessages();

  return (
    <HtmlDocument locale={locale}>
      <NextIntlClientProvider messages={messages}>
        <Providers themeProps={{ attribute: "class", defaultTheme: "dark" }}>
          <div
            className={clsx(
              "min-h-screen text-foreground bg-background font-sans antialiased",
              fontSans.variable,
            )}
            data-locale={locale}
          >
            <LayoutWrapper>{children}</LayoutWrapper>
          </div>
        </Providers>
      </NextIntlClientProvider>
    </HtmlDocument>
  );
}
