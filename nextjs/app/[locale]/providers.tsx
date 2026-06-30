"use client";

import type { ThemeProviderProps } from "next-themes";

import * as React from "react";
import { HeroUIProvider } from "@heroui/system";
import { useRouter } from "next/navigation";
import { ThemeProvider as NextThemesProvider } from "next-themes";

import { LazyMotion, domAnimation } from "framer-motion";

import { TRPCProvider } from "@/lib/trpc/TRPCProvider";
import { DownloadModalProvider } from "@/contexts/DownloadModalContext";
import DownloadModal from "@/components/landing/DownloadModal";
import { PostHogClientProvider } from "@/contexts/PostHogProvider";

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
