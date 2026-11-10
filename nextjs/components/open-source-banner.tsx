"use client";

import { Sparkles, X } from "lucide-react";
import { useTranslations } from "next-intl";

import { Link } from "@/src/i18n/navigation";

export const OpenSourceBanner = ({ onDismiss }: { onDismiss: () => void }) => {
  const t = useTranslations("openSourceBanner");

  return (
    // Over a sub-page's sky band (PageHero's `data-dusk-hero`, see
    // LayoutWrapper) the banner turns to blurred glass, so the sky shows
    // through it instead of a solid strip sitting on top of the sky.
    <div className="w-full h-12 bg-[var(--dusk-night)] group-has-[[data-dusk-hero]]/site:bg-[rgba(5,6,13,.5)] group-has-[[data-dusk-hero]]/site:backdrop-blur-[16px] text-[var(--dusk-ink)] border-b border-[var(--dusk-line)] relative overflow-hidden">
      <div className="absolute inset-0 bg-[linear-gradient(90deg,rgba(129,90,220,.16),rgba(240,171,252,.08)_50%,rgba(253,186,140,.1))]" />

      <div className="h-full flex items-center justify-center gap-2 sm:gap-3 px-3 relative z-10">
        <Sparkles className="w-3.5 h-3.5 text-[var(--dusk-lav)] shrink-0" />
        <p className="text-xs sm:text-sm font-medium truncate">
          <span className="hidden sm:inline">{t("text")} · </span>
          <span className="sm:hidden">{t("shortText")} · </span>
          <Link
            className="rounded text-[var(--dusk-lav)] underline underline-offset-2 hover:text-white transition-colors focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--dusk-lav)]"
            href="/open-source"
          >
            {t("learnMore")}
          </Link>
        </p>
        <button
          aria-label={t("dismiss")}
          className="absolute right-2 sm:right-3 rounded-md p-1 text-[var(--dusk-mute)] hover:bg-white/[.07] hover:text-white transition-colors cursor-pointer focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--dusk-lav)]"
          onClick={onDismiss}
        >
          <X className="w-3.5 h-3.5" />
        </button>
      </div>
    </div>
  );
};
