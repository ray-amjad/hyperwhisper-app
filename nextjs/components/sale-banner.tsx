"use client";

import { Chip } from "@heroui/chip";
import { Copy, Check } from "lucide-react";
import { useState, useEffect } from "react";

const useCountdown = (targetDate: Date) => {
  const [timeLeft, setTimeLeft] = useState({ days: 0, hours: 0, minutes: 0, seconds: 0 });

  useEffect(() => {
    const calculateTimeLeft = () => {
      const difference = targetDate.getTime() - new Date().getTime();
      if (difference > 0) {
        setTimeLeft({
          days: Math.floor(difference / (1000 * 60 * 60 * 24)),
          hours: Math.floor((difference / (1000 * 60 * 60)) % 24),
          minutes: Math.floor((difference / 1000 / 60) % 60),
          seconds: Math.floor((difference / 1000) % 60),
        });
      }
    };

    calculateTimeLeft();
    const timer = setInterval(calculateTimeLeft, 1000);
    return () => clearInterval(timer);
  }, [targetDate]);

  return timeLeft;
};

export const SaleBanner = () => {
  const [copied, setCopied] = useState(false);
  const couponCode = "Y2026";
  const saleEndDate = new Date("2026-01-12T23:59:59Z"); // UTC
  const { days, hours, minutes, seconds } = useCountdown(saleEndDate);

  const handleCopy = () => {
    navigator.clipboard.writeText(couponCode);
    setCopied(true);
    setTimeout(() => setCopied(false), 2000);
  };

  return (
    <div className="w-full bg-black text-white py-2 sm:py-3 px-3 sm:px-4 border-b border-white/10 bg-[url('/noise.png')] relative overflow-hidden">
      <div className="absolute inset-0 bg-gradient-to-r from-purple-900/20 via-blue-900/20 to-purple-900/20" />

      <div className="flex flex-wrap items-center justify-center gap-x-2 sm:gap-x-3 gap-y-1.5 z-10 relative">
        <Chip
          className="animate-pulse"
          color="danger"
          size="sm"
          variant="shadow"
        >
          SALE
        </Chip>

        <span className="font-medium text-xs sm:text-sm text-center">
          <span className="hidden sm:inline">New Year 2026 sale on right now! Get 35% off with code</span>
          <span className="sm:hidden">New Year sale! <span className="font-bold">35% off</span></span>
        </span>

        <button
          className="flex items-center gap-1.5 sm:gap-2 px-2 sm:px-3 py-0.5 sm:py-1 bg-white/10 hover:bg-white/20 border border-white/10 rounded-lg transition-colors z-10 group cursor-pointer"
          title="Copy coupon code"
          onClick={handleCopy}
        >
          <span className="font-mono font-bold tracking-wider text-xs sm:text-sm">
            {couponCode}
          </span>
          {copied ? (
            <Check className="w-3 h-3 sm:w-4 sm:h-4 text-green-400" />
          ) : (
            <Copy className="w-3 h-3 sm:w-4 sm:h-4 text-gray-400 group-hover:text-white transition-colors" />
          )}
        </button>

        <div className="flex items-center gap-1 text-[10px] sm:text-xs text-gray-400">
          <span className="hidden sm:inline">Ends in</span>
          <div className="flex items-center gap-0.5 sm:gap-1 font-mono">
            <span className="bg-white/10 px-1 sm:px-1.5 py-0.5 rounded text-white">{days}d</span>
            <span className="bg-white/10 px-1 sm:px-1.5 py-0.5 rounded text-white">{hours}h</span>
            <span className="bg-white/10 px-1 sm:px-1.5 py-0.5 rounded text-white">{minutes}m</span>
            <span className="bg-white/10 px-1 sm:px-1.5 py-0.5 rounded text-white">{seconds}s</span>
          </div>
        </div>
      </div>
    </div>
  );
};
