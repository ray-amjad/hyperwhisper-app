"use client";

import type { ThemeProviderProps } from "next-themes";

import * as React from "react";
import { HeroUIProvider } from "@heroui/system";
import { useRouter } from "next/navigation";
import { ThemeProvider as NextThemesProvider } from "next-themes";
import { useLocale } from "next-intl";

import { LazyMotion, domAnimation } from "framer-motion";

import { TRPCProvider } from "@/lib/trpc/TRPCProvider";
import { DownloadModalProvider } from "@/contexts/DownloadModalContext";
import DownloadModal from "@/components/landing/DownloadModal";
import { PostHogClientProvider } from "@/contexts/PostHogProvider";
import { applyHtmlLocaleAttributes } from "@/src/i18n/locales";

export interface ProvidersProps {
  children: React.ReactNode;
  themeProps?: ThemeProviderProps;
}

declare module "@react-types/shared" {
  interface RouterConfig {
    routerOptions: NonNullable<
      Parameters<ReturnType<typeof useRouter>["push"]>[1]
    >;
  }
}

export function Providers({ children, themeProps }: ProvidersProps) {
  const router = useRouter();
  const locale = useLocale();

  // The <html> element is rendered by the ROOT layout, which sits above the
  // [locale] segment and is not re-rendered on a soft client-side navigation.
  // The language switcher navigates with router.replace, so without this the
  // shell keeps the lang and dir of the locale the tab was first loaded with:
  // switch from /ar to /en and English content keeps rendering right-to-left.
  // This provider is inside NextIntlClientProvider and already re-renders with
  // the new locale, so it is the cheapest place to resync both attributes.
  // <html> carries suppressHydrationWarning, and on a full page load the server
  // already emitted these same values, so this is a no-op there.
  React.useEffect(() => {
    applyHtmlLocaleAttributes(document.documentElement, locale);
  }, [locale]);

  return (
    <TRPCProvider>
      <HeroUIProvider navigate={router.push}>
        <PostHogClientProvider>
          <NextThemesProvider {...themeProps}>
            <LazyMotion features={domAnimation}>
              <DownloadModalProvider>
                {children}
                <DownloadModal />
              </DownloadModalProvider>
            </LazyMotion>
          </NextThemesProvider>
        </PostHogClientProvider>
      </HeroUIProvider>
    </TRPCProvider>
  );
}
