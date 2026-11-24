import { SITE_NAME, SITE_URL } from "@/lib/site-metadata";

export function stripMarkdownLinks(text: string): string {
  return text.replace(/\[([^\]]+)\]\([^)]+\)/g, "$1");
}

export const ORGANIZATION_ID = `${SITE_URL}/#organization`;

/** The HyperWhisper Organization node, shared by the home page and the docs. */
export function organizationJsonLd() {
  return {
    "@type": "Organization",
    "@id": ORGANIZATION_ID,
    name: SITE_NAME,
    url: SITE_URL,
    logo: `${SITE_URL}/icon/512.png`,
    sameAs: [
      "https://github.com/ray-amjad/hyperwhisper-app",
      "https://www.linkedin.com/company/hyperwhisper/",
      "https://x.com/HyperWhisperApp",
    ],
  };
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

  return {
    "@context": "https://schema.org",
    "@graph": [
      organizationJsonLd(),
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
        publisher: { "@id": ORGANIZATION_ID },
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
