import HeroSection from "@/components/landing/HeroSection";
import VideoDemo from "@/components/landing/VideoDemo";
import FeaturesGrid from "@/components/landing/FeaturesGrid";
import PricingSection from "@/components/landing/PricingSection";
import FAQSection from "@/components/landing/FAQSection";
import { localizedPageMetadata } from "@/lib/localized-page-metadata";

type MetadataProps = {
  params: Promise<{ locale: string }>;
};

export async function generateMetadata({ params }: MetadataProps) {
  const { locale } = await params;

  return localizedPageMetadata(locale, "");
}

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
