import { SITE_NAME, SITE_URL } from "@/lib/site-metadata";

export function stripMarkdownLinks(text: string): string {
  return text.replace(/\[([^\]]+)\]\([^)]+\)/g, "$1");
}

export type HomeFaqItem = {
  question: string;
  answer: string;
};

/**
 * Homepage JSON-LD. SoftwareApplication is the free desktop app (price 0 is
 * the catalogue's "free forever"); Cloud credits are a separate product and
 * are not an Offer here. No aggregateRating — we do not publish ratings.
 */
export function buildHomeJsonLd({
  locale,
  description,
  faq,
}: {
  locale: string;
  description: string;
  faq: HomeFaqItem[];
}) {
  const pageUrl = `${SITE_URL}/${locale}`;
  const organizationId = `${SITE_URL}/#organization`;

  return {
    "@context": "https://schema.org",
    "@graph": [
      {
        "@type": "Organization",
        "@id": organizationId,
        name: SITE_NAME,
        url: SITE_URL,
        logo: `${SITE_URL}/icon/512.png`,
        sameAs: [
          "https://github.com/ray-amjad/hyperwhisper-app",
          "https://www.linkedin.com/company/hyperwhisper/",
          "https://x.com/HyperWhisperApp",
        ],
      },
      {
        "@type": "SoftwareApplication",
        name: SITE_NAME,
        url: pageUrl,
        operatingSystem: "macOS, Windows, Linux",
        applicationCategory: "UtilitiesApplication",
        description,
        downloadUrl: `${SITE_URL}/${locale}/download`,
        offers: {
          "@type": "Offer",
          price: "0",
          priceCurrency: "USD",
        },
        publisher: { "@id": organizationId },
      },
      {
        "@type": "FAQPage",
        mainEntity: faq.map((item) => ({
          "@type": "Question",
          name: item.question,
          acceptedAnswer: {
            "@type": "Answer",
            text: stripMarkdownLinks(item.answer),
          },
        })),
      },
    ],
  };
}
