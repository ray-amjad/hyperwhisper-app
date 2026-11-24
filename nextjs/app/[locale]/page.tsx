import { getTranslations } from "next-intl/server";

import ClosingSection from "@/components/landing/ClosingSection";
import FAQSection from "@/components/landing/FAQSection";
import { FAQ_KEYS } from "@/components/landing/faq-keys";
import FeaturesGrid from "@/components/landing/FeaturesGrid";
import HeroSection from "@/components/landing/HeroSection";
import HowItWorks from "@/components/landing/HowItWorks";
import LogoMarquee from "@/components/landing/LogoMarquee";
import ModesShowcase from "@/components/landing/ModesShowcase";
import PricingSection from "@/components/landing/PricingSection";
import StatsRow from "@/components/landing/StatsRow";
import { fontMono } from "@/config/fonts-mono";
import { buildHomeJsonLd } from "@/lib/home-json-ld";
import { localizedPageMetadata } from "@/lib/localized-page-metadata";
import { serializeJsonLd } from "@/lib/security/json-ld";

export const generateMetadata = localizedPageMetadata({
  path: "",
});

export default async function Home({
  params,
}: {
  params: Promise<{ locale: string }>;
}) {
  const { locale } = await params;
  const tFaq = await getTranslations({ locale, namespace: "faq" });
  const tMeta = await getTranslations({ locale, namespace: "metadata" });
  const jsonLd = buildHomeJsonLd({
    locale,
    description: tMeta("description"),
    faq: FAQ_KEYS.map((key) => ({
      question: tFaq(`questions.${key}.question`),
      answer: tFaq(`questions.${key}.answer`),
    })),
  });

  return (
    // fontMono only defines --font-mono-keys (the key caps in "How it works"),
    // so its file loads on this page alone.
    <div
      className={`min-h-screen overflow-x-clip bg-[var(--dusk-night)] text-[var(--dusk-ink)] ${fontMono.variable}`}
    >
      <script
        dangerouslySetInnerHTML={{ __html: serializeJsonLd(jsonLd) }}
        type="application/ld+json"
      />
      {/* JavaScript off: show every scroll reveal at once (they are
          server-rendered at their hidden start; see globals.css). */}
      <noscript>
        <style>
          {
            "[data-reveal]{opacity:1!important;transform:none!important;filter:none!important}"
          }
        </style>
      </noscript>
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
