import HeroSection from "@/components/landing/HeroSection";
import VideoDemo from "@/components/landing/VideoDemo";
import FeaturesGrid from "@/components/landing/FeaturesGrid";
import PricingSection from "@/components/landing/PricingSection";
import FAQSection from "@/components/landing/FAQSection";
import { localizedPageMetadata } from "@/lib/localized-page-metadata";

export const generateMetadata = localizedPageMetadata({
  path: "",
});

export default function Home() {
  return (
    <main className="min-h-screen bg-black">
      <HeroSection />
      <VideoDemo />
      <FeaturesGrid />
      <PricingSection />
      <FAQSection />
    </main>
  );
}
