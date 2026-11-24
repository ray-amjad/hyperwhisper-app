import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { join } from "node:path";

import { FAQ_KEYS } from "../components/landing/faq-keys";
import { buildHomeJsonLd, stripMarkdownLinks } from "../lib/home-json-ld";
import { SITE_NAME, SITE_URL } from "../lib/site-metadata";
import { serializeJsonLd } from "../lib/security/json-ld";

const ROOT = fileURLToPath(new URL("..", import.meta.url));

test("markdown links flatten to their link text", () => {
  assert.equal(
    stripMarkdownLinks("See our [Refund Policy](/legal/refund-policy)."),
    "See our Refund Policy.",
  );
});

test("homepage JSON-LD is Organization + SoftwareApplication + FAQPage, no ratings", () => {
  const jsonLd = buildHomeJsonLd({
    locale: "en",
    description:
      "AI dictation and speech-to-text for macOS, Windows, and Linux.",
    faq: [
      {
        question: "Is there a subscription?",
        answer: "No — [download](/download) it free.",
      },
    ],
  });
  const graph = jsonLd["@graph"] as Array<Record<string, unknown>>;
  const types = graph.map((node) => node["@type"]);

  assert.deepEqual(types, ["Organization", "SoftwareApplication", "FAQPage"]);

  const org = graph[0];
  assert.equal(org.name, SITE_NAME);
  assert.equal(org.url, SITE_URL);

  const app = graph[1] as {
    operatingSystem: string;
    applicationCategory: string;
    downloadUrl: string;
    offers: { price: string; priceCurrency: string };
  };
  assert.equal(app.operatingSystem, "macOS, Windows, Linux");
  assert.equal(app.applicationCategory, "UtilitiesApplication");
  assert.equal(app.downloadUrl, `${SITE_URL}/en/download`);
  assert.equal(app.offers.price, "0");
  assert.equal(app.offers.priceCurrency, "USD");
  assert.equal("aggregateRating" in app, false);

  const faq = graph[2] as {
    mainEntity: Array<{ acceptedAnswer: { text: string } }>;
  };
  assert.equal(faq.mainEntity[0].acceptedAnswer.text, "No — download it free.");

  const serialized = serializeJsonLd(jsonLd);
  assert.doesNotMatch(serialized, /</);
  assert.ok(serialized.includes("SoftwareApplication"));
});

test("the homepage serialises JSON-LD through serializeJsonLd and the FAQ keys", () => {
  const source = readFileSync(join(ROOT, "app/[locale]/page.tsx"), "utf8");
  assert.match(source, /buildHomeJsonLd/);
  assert.match(source, /serializeJsonLd\(jsonLd\)/);
  assert.match(source, /FAQ_KEYS/);
});

test("FAQSection and homepage JSON-LD share the same question list", () => {
  const source = readFileSync(
    join(ROOT, "components/landing/FAQSection.tsx"),
    "utf8",
  );
  assert.match(source, /FAQ_KEYS/);
  assert.ok(FAQ_KEYS.length >= 10);
  assert.ok(FAQ_KEYS.includes("offline"));
  assert.ok(FAQ_KEYS.includes("refund"));
});
