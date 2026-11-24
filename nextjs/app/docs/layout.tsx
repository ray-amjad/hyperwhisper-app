import type { Metadata } from "next";
import type { ReactNode } from "react";

import LocaleLayout from "../[locale]/layout";

import { DocsSidebar } from "@/components/docs/DocsSidebar";
import {
  SITE_NAME,
  SITE_URL,
  TWITTER_CARD,
  TWITTER_CREATOR,
  TWITTER_IMAGES,
} from "@/lib/site-metadata";
import { defaultLocale } from "@/src/i18n/locales";

// The docs, at /docs with no locale prefix (they are English only, and the
// URLs are the ones Mintlify served). The proxy matcher skips /docs, so
// next-intl never redirects them to /en/docs. The site shell (navbar, footer,
// dark theme, providers) is the [locale] layout rendered for English, the way
// app/not-found.tsx renders it; reading no request keeps every page static.

export const metadata: Metadata = {
  metadataBase: new URL(SITE_URL),
  // "Models - HyperWhisper": the title Mintlify served for every page.
  title: {
    default: `Documentation - ${SITE_NAME}`,
    template: `%s - ${SITE_NAME}`,
  },
  robots: { index: true, follow: true },
  icons: { icon: "/icon/32.png", apple: "/icon/256.png" },
  twitter: {
    card: TWITTER_CARD,
    creator: TWITTER_CREATOR,
    images: TWITTER_IMAGES,
  },
};

export default function DocsLayout({ children }: { children: ReactNode }) {
  return (
    <LocaleLayout params={Promise.resolve({ locale: defaultLocale })}>
      <div className="docs">
        <DocsSidebar />
        <div className="docs-main">{children}</div>
      </div>
    </LocaleLayout>
  );
}
