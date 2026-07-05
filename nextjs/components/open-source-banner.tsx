"use client";

import { Sparkles, X } from "lucide-react";

import { Link } from "@/src/i18n/navigation";

export const OpenSourceBanner = ({ onDismiss }: { onDismiss: () => void }) => {
  return (
    <div className="w-full h-12 bg-black text-white border-b border-white/10 relative overflow-hidden">
      <div className="absolute inset-0 bg-gradient-to-r from-purple-900/30 via-blue-900/30 to-purple-900/30" />

      <div className="h-full flex items-center justify-center gap-2 sm:gap-3 px-3 relative z-10">
        <Sparkles className="w-3.5 h-3.5 text-purple-300 shrink-0" />
        <p className="text-xs sm:text-sm font-medium truncate">
          <span className="hidden sm:inline">
            HyperWhisper is now fully open source ·{" "}
          </span>
          <span className="sm:hidden">Now open source · </span>
          <Link
            className="underline underline-offset-2 hover:text-purple-200 transition-colors"
            href="/open-source"
          >
            Learn more
          </Link>
        </p>
        <button
          aria-label="Dismiss announcement"
          className="absolute right-2 sm:right-3 p-1 text-gray-400 hover:text-white transition-colors cursor-pointer"
          onClick={onDismiss}
        >
          <X className="w-3.5 h-3.5" />
        </button>
      </div>
    </div>
  );
};
