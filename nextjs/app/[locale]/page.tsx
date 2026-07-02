import HeroSection from "@/components/landing/HeroSection";
import VideoDemo from "@/components/landing/VideoDemo";
import FeaturesGrid from "@/components/landing/FeaturesGrid";
import PricingSection from "@/components/landing/PricingSection";
import FAQSection from "@/components/landing/FAQSection";

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
