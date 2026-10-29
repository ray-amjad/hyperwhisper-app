import ClosingSection from "@/components/landing/ClosingSection";
import FAQSection from "@/components/landing/FAQSection";
import FeaturesGrid from "@/components/landing/FeaturesGrid";
import HeroSection from "@/components/landing/HeroSection";
import HowItWorks from "@/components/landing/HowItWorks";
import LogoMarquee from "@/components/landing/LogoMarquee";
import ModesShowcase from "@/components/landing/ModesShowcase";
import PricingSection from "@/components/landing/PricingSection";
import StatsRow from "@/components/landing/StatsRow";
import { fontMono } from "@/config/fonts";
import { localizedPageMetadata } from "@/lib/localized-page-metadata";

export const generateMetadata = localizedPageMetadata({
  path: "",
});

export default function Home() {
  return (
    // fontMono only defines --font-mono-keys (the key caps in "How it works"),
    // so its file loads on this page alone.
    <div
      className={`min-h-screen overflow-x-clip bg-[var(--dusk-night)] text-[var(--dusk-ink)] ${fontMono.variable}`}
    >
      <HeroSection />
      <LogoMarquee />
      <HowItWorks />
      <StatsRow />
      <FeaturesGrid />
      <ModesShowcase />
      <PricingSection />
      <FAQSection />
      <ClosingSection />
    </div>
  );
}
