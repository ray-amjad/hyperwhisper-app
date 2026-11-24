/**
 * Homepage FAQ items, in display order. FAQSection and the homepage FAQPage
 * JSON-LD both read this list so a new question cannot land in the UI without
 * structured data (or the other way around).
 */
export const FAQ_KEYS = [
  "offline",
  "compatibility",
  "accuracy",
  "privacy",
  "dataTraining",
  "dataStorage",
  "lifetime",
  "refund",
  "requirements",
  "languages",
  "apiKey",
  "cloud",
  "creditExpiry",
  "deviceLimit",
  "offlineModels",
  "madeBy",
  "funding",
  "selfHost",
] as const;

export type FaqKey = (typeof FAQ_KEYS)[number];
