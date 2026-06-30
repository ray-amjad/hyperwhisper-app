import "@/styles/globals.css";

// Polyfill localStorage for SSR to prevent "localStorage.getItem is not a function" errors
if (typeof window === "undefined") {
  const noop = () => {};

  (global as any).localStorage = {
    getItem: () => null,
    setItem: noop,
    removeItem: noop,
    clear: noop,
    key: () => null,
    length: 0,
  };
}

import { Viewport } from "next";
import clsx from "clsx";
import { NextIntlClientProvider } from "next-intl";
import {
  getMessages,
  getTranslations,
  setRequestLocale,
} from "next-intl/server";
import { headers } from "next/headers";

import { Providers } from "./providers";

import { fontSans } from "@/config/fonts";
import LayoutWrapper from "@/components/layout/LayoutWrapper";
import { locales } from "@/i18n";
import {
  buildAlternateLanguageMap,
  defaultLocale,
  stripLocalePrefix,
  toOpenGraphLocale,
} from "@/src/i18n/locales";

type Props = {
  children: React.ReactNode;
  params: Promise<{ locale: string }>;
};

export function generateStaticParams() {
  return locales.map((locale) => ({ locale }));
}

export async function generateMetadata({ params }: Props) {
  const { locale } = await params;
  const [t, headersList] = await Promise.all([
    getTranslations({ locale, namespace: "metadata" }),
    headers(),
  ]);
  const pathname = headersList.get("x-pathname") || "";

  // Remove locale prefix from pathname if present
  const pathWithoutLocale = stripLocalePrefix(pathname);

  const baseUrl = "https://hyperwhisper.com";
  const alternateLanguages = buildAlternateLanguageMap(baseUrl, pathWithoutLocale);

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
      "whisper AI",
      "dictation software",
      "voice typing",
      "productivity tool",
      "audio transcription",
      "speech recognition",
    ],
    authors: [{ name: "HyperWhisper" }],
    creator: "HyperWhisper",
    alternates: {
      canonical: `${baseUrl}/${locale}${pathWithoutLocale}`,
      languages: {
        ...alternateLanguages,
        "x-default": `${baseUrl}/${defaultLocale}${pathWithoutLocale}`,
      },
    },
    openGraph: {
      type: "website",
      locale: toOpenGraphLocale(locale),
      url: `${baseUrl}/${locale}${pathWithoutLocale}`,
      title: t("title"),
      description: t("description"),
      siteName: "HyperWhisper",
      images: [
        {
          url: "https://hyperwhisper.com/icon/1024.png",
          width: 1024,
          height: 1024,
          alt: "HyperWhisper Logo",
        },
        {
          url: "https://hyperwhisper.com/icon/512.png",
          width: 512,
          height: 512,
          alt: "HyperWhisper Logo",
        },
        {
          url: "https://hyperwhisper.com/icon/256.png",
          width: 256,
          height: 256,
          alt: "HyperWhisper Logo",
        },
        {
          url: "https://hyperwhisper.com/icon/128.png",
          width: 128,
          height: 128,
          alt: "HyperWhisper Logo",
        },
      ],
    },
    twitter: {
      card: "summary",
      title: t("title"),
      description: t("description"),
      creator: "@theramjad",
      images: ["https://hyperwhisper.com/icon/256.png"],
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

  // Enable static rendering
  setRequestLocale(locale);

  // Providing all messages to the client
  // side is the easiest way to get started
  const messages = await getMessages();

  return (
    <>
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
    </>
  );
}
