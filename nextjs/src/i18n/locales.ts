export const locales = [
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
] as const;

export type Locale = (typeof locales)[number];

export const defaultLocale: Locale = "en";

export const localeLabels: Record<Locale, string> = {
  en: "English",
  ja: "日本語",
  es: "Español",
  zh: "中文 (简体)",
  de: "Deutsch",
  fr: "Français",
  ko: "한국어",
  "zh-Hant": "中文 (繁體)",
  it: "Italiano",
  nl: "Nederlands",
  pt: "Português",
  ar: "العربية",
  sv: "Svenska",
  da: "Dansk",
  nb: "Norsk Bokmål",
  fi: "Suomi",
  he: "עברית",
  pl: "Polski",
  cs: "Čeština",
  tr: "Türkçe",
  el: "Ελληνικά",
  ro: "Română",
  hu: "Magyar",
  sk: "Slovenčina",
  bg: "Български",
  hr: "Hrvatski",
  sl: "Slovenščina",
  sr: "Српски",
  lt: "Lietuvių",
  lv: "Latviešu",
  et: "Eesti",
  is: "Íslenska",
  ca: "Català",
  ru: "Русский",
  uk: "Українська",
  th: "ไทย",
  ms: "Bahasa Melayu",
  id: "Bahasa Indonesia",
  vi: "Tiếng Việt",
  hi: "हिन्दी",
};

const openGraphLocaleOverrides: Partial<Record<Locale, string>> = {
  en: "en_US",
  ja: "ja_JP",
  es: "es_ES",
  zh: "zh_CN",
  "zh-Hant": "zh_TW",
  pt: "pt_PT",
  nb: "nb_NO",
};

const localeSet = new Set<string>(locales);

export function isSupportedLocale(locale: string): locale is Locale {
  return localeSet.has(locale);
}

export function stripLocalePrefix(pathname: string): string {
  const segments = pathname.split("/").filter(Boolean);

  if (segments.length === 0) return "";

  if (isSupportedLocale(segments[0])) {
    const rest = segments.slice(1).join("/");
    return rest ? `/${rest}` : "";
  }

  return pathname;
}

export function buildAlternateLanguageMap(baseUrl: string, path: string) {
  const normalizedPath = path.startsWith("/") || path === "" ? path : `/${path}`;
  const map: Record<string, string> = {};

  for (const locale of locales) {
    map[locale] = `${baseUrl}/${locale}${normalizedPath}`;
  }

  return map;
}

export function toOpenGraphLocale(locale: string): string {
  if (!isSupportedLocale(locale)) return "en_US";

  const override = openGraphLocaleOverrides[locale];
  if (override) return override;

  if (locale.includes("-")) {
    return locale.replace(/-/g, "_");
  }

  return `${locale}_${locale.toUpperCase()}`;
}
