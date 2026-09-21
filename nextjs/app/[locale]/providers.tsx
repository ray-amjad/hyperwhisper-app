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

// React warns when useLayoutEffect runs during server rendering, and this file
// is a client component that Next still renders on the server. `window` is the
// standard discriminator: the branch is evaluated once, at module scope, so the
// hook identity never changes between renders on either side.
const useIsomorphicLayoutEffect =
  typeof window !== "undefined" ? React.useLayoutEffect : React.useEffect;

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
  //
  // A LAYOUT effect, not a passive one. React calls the scheduler's
  // requestPaint() at the end of the mutation and layout phase, which is BEFORE
  // the passive phase that would flush a useEffect. With useEffect the browser
  // is therefore free to paint one frame of the new locale's content under the
  // OLD dir — a visible flash of English laid out right-to-left. A layout effect
  // runs synchronously inside that same commit, so dir is already corrected when
  // the frame the user sees is produced. The work here is one attribute write on
  // a single element, so it is not enough to be worth deferring off the commit.
  useIsomorphicLayoutEffect(() => {
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
