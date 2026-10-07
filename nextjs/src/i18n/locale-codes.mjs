// @ts-check

/**
 * The locale codes the site serves, in display order. The ONE list:
 * `locales.ts` re-exports it for the app, and `config/redirects.mjs` reads it
 * from `next.config.mjs`, which Node loads as plain JavaScript and so cannot
 * import a `.ts` file (#1377).
 *
 * The JSDoc const cast keeps the tuple literal, so `Locale` in locales.ts is
 * still the union of these codes and every `Record<Locale, …>` stays exhaustive.
 */
export const localeCodes = /** @type {const} */ ([
  "en",
  "ja",
  "es",
  "zh",
  "de",
  "fr",
  "ko",
  "zh-Hant",
  "it",
  "nl",
  "pt",
  "ar",
  "sv",
  "da",
  "nb",
  "fi",
  "he",
  "pl",
  "cs",
  "tr",
  "el",
  "ro",
  "hu",
  "sk",
  "bg",
  "hr",
  "sl",
  "sr",
  "lt",
  "lv",
  "et",
  "is",
  "ca",
  "ru",
  "uk",
  "th",
  "ms",
  "id",
  "vi",
  "hi",
]);
