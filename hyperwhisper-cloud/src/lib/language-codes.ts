// Per-provider language-code normalization.
//
// The desktop pickers send a language code in ONE code space: a bare, lowercased
// primary subtag (`en`, `no`, `zh`). That is not what several upstreams accept.
// `shared-app-classification/cloud-stt-catalog.json` records the divergence per
// provider — Google Chirp wants a full BCP-47 locale, ElevenLabs wants ISO-639-3,
// Azure documents `nb` and not `no` — and until this module existed the adapters
// forwarded the picker code raw. Google then 400s, and because
// `TranscriptionError.isRetryable` treats every 5xx/4xx server error as
// retryable, the app retried a permanent rejection four times before surfacing
// it. The user-visible symptom was a ~27-second hang, not an error.
//
// THE FALLBACK IS ALWAYS AUTO-DETECT, NEVER AN ERROR. When a code has no valid
// mapping for the chosen provider and model, these helpers return `null` and the
// adapter drops the language, letting the upstream detect it. A slightly worse
// transcript beats a failed one, and the picker should not have offered the
// combination in the first place — the `language_unmappable` log event is how we
// find out that it did.
//
// The tables here are derived from the catalog. `language-codes.test.ts` holds
// them to it, so a catalog edit that outdates this file fails the test run
// rather than silently reintroducing the 400.

/**
 * Strip a region/script subtag down to the primary subtag, lowercased.
 * `en-US` → `en`, `cmn-Hans-CN` → `cmn`, `pt_BR` → `pt`.
 *
 * Distinct from `providers/utils.ts`'s `explicitLanguageSubtag`, which folds the
 * `auto` sentinel to `undefined` on the way. This one is the plain string
 * reduction the tables below use for lookup, after the caller has already
 * decided the language is explicit.
 */
export function primarySubtag(code: string): string {
  return code.toLowerCase().split(/[-_]/)[0] ?? '';
}

// ---------------------------------------------------------------------------
// Google Chirp 3 — needs a full BCP-47 locale
// ---------------------------------------------------------------------------

// Chirp 3 rejects a bare primary subtag with 400 INVALID_ARGUMENT. `sw` is the
// single exception in Google's own list, and it is region-qualified here anyway
// so every row of this table has the same shape.
//
// Where Google publishes several regions for one language we pick ONE default.
// The picker only says "Portuguese", so the region is our call: we take the
// largest speaker population (pt-BR over pt-PT, es-ES for European Spanish as
// the unmarked form, en-US). A user who needs the other region is not worse off
// than today — today the request fails outright.
//
// Keys are in the PICKER's code space, which is not always the catalog's:
// Hebrew is `he` here and `iw-IL` at Google, Javanese is `jw` here (a Whisper-ism
// the shared language list inherited) and `jv-ID` at Google, and Mandarin is
// `zh` here and `cmn-Hans-CN` at Google.
const GOOGLE_CHIRP_LOCALE_BY_BASE: Readonly<Record<string, string>> = {
  af: 'af-ZA',
  am: 'am-ET',
  ar: 'ar-XA', // pan-Arabic; Google's 20 country locales all narrow this
  as: 'as-IN',
  ast: 'ast-ES',
  az: 'az-AZ',
  bg: 'bg-BG',
  bn: 'bn-IN',
  ca: 'ca-ES',
  cs: 'cs-CZ',
  cy: 'cy-GB',
  da: 'da-DK',
  de: 'de-DE',
  el: 'el-GR',
  en: 'en-US',
  es: 'es-ES',
  et: 'et-EE',
  eu: 'eu-ES',
  fa: 'fa-IR',
  fi: 'fi-FI',
  fil: 'fil-PH',
  fr: 'fr-FR',
  gl: 'gl-ES',
  gu: 'gu-IN',
  ha: 'ha-NG',
  he: 'iw-IL', // Google still uses the retired ISO code for Hebrew
  hi: 'hi-IN',
  hr: 'hr-HR',
  hu: 'hu-HU',
  hy: 'hy-AM',
  id: 'id-ID',
  is: 'is-IS',
  it: 'it-IT',
  ja: 'ja-JP',
  jw: 'jv-ID', // picker inherited Whisper's `jw`; the ISO code is `jv`
  ka: 'ka-GE',
  kk: 'kk-KZ',
  km: 'km-KH',
  kn: 'kn-IN',
  ko: 'ko-KR',
  ky: 'ky-KG',
  lb: 'lb-LU',
  lo: 'lo-LA',
  lt: 'lt-LT',
  lv: 'lv-LV',
  mi: 'mi-NZ',
  mk: 'mk-MK',
  ml: 'ml-IN',
  mn: 'mn-MN',
  mr: 'mr-IN',
  ms: 'ms-MY',
  mt: 'mt-MT',
  my: 'my-MM',
  ne: 'ne-NP',
  nl: 'nl-NL',
  no: 'no-NO',
  nso: 'nso-ZA',
  or: 'or-IN',
  pa: 'pa-Guru-IN',
  pl: 'pl-PL',
  pt: 'pt-BR',
  ro: 'ro-RO',
  ru: 'ru-RU',
  sk: 'sk-SK',
  sl: 'sl-SI',
  sq: 'sq-AL',
  sr: 'sr-RS',
  sv: 'sv-SE',
  sw: 'sw-KE',
  ta: 'ta-IN',
  te: 'te-IN',
  th: 'th-TH',
  tr: 'tr-TR',
  uk: 'uk-UA',
  uz: 'uz-UZ',
  vi: 'vi-VN',
  wo: 'wo-SN',
  xh: 'xh-ZA',
  yo: 'yo-NG',
  yue: 'yue-Hant-HK',
  zh: 'cmn-Hans-CN',
  zu: 'zu-ZA',
};

/**
 * Every locale Chirp 3 accepts, per the catalog. A client that already sends a
 * full locale (`en-GB`, `pt-PT`) gets it forwarded untouched instead of being
 * flattened to this file's default region.
 */
const GOOGLE_CHIRP_LOCALES: ReadonlySet<string> = new Set(
  [
    'ca-ES', 'cmn-Hans-CN', 'hr-HR', 'da-DK', 'nl-NL', 'en-AU', 'en-IN', 'en-GB', 'en-US',
    'fi-FI', 'fr-CA', 'fr-FR', 'de-DE', 'el-GR', 'hi-IN', 'it-IT', 'ja-JP', 'ko-KR', 'pl-PL',
    'pt-BR', 'pt-PT', 'ro-RO', 'ru-RU', 'es-ES', 'es-US', 'sv-SE', 'tr-TR', 'uk-UA', 'vi-VN',
    'af-ZA', 'sq-AL', 'am-ET', 'ar-DZ', 'ar-BH', 'ar-EG', 'ar-IQ', 'ar-IL', 'ar-JO', 'ar-KW',
    'ar-LB', 'ar-MR', 'ar-MA', 'ar-OM', 'ar-QA', 'ar-SA', 'ar-PS', 'ar-SY', 'ar-TN', 'ar-AE',
    'ar-YE', 'ar-XA', 'hy-AM', 'as-IN', 'ast-ES', 'az-AZ', 'eu-ES', 'bn-BD', 'bn-IN', 'bg-BG',
    'my-MM', 'yue-Hant-HK', 'cmn-Hant-TW', 'cs-CZ', 'en-PH', 'et-EE', 'fil-PH', 'gl-ES',
    'ka-GE', 'gu-IN', 'ha-NG', 'iw-IL', 'hu-HU', 'is-IS', 'id-ID', 'jv-ID', 'kn-IN', 'kk-KZ',
    'km-KH', 'ky-KG', 'lo-LA', 'lv-LV', 'lt-LT', 'lb-LU', 'mk-MK', 'ms-MY', 'ml-IN', 'mt-MT',
    'mi-NZ', 'mr-IN', 'mn-MN', 'ne-NP', 'nso-ZA', 'no-NO', 'or-IN', 'fa-IR', 'pa-Guru-IN',
    'sr-RS', 'sk-SK', 'sl-SI', 'es-MX', 'sw-KE', 'sw', 'ta-IN', 'te-IN', 'th-TH', 'uz-UZ',
    'cy-GB', 'wo-SN', 'xh-ZA', 'yo-NG', 'zu-ZA',
  ].map(locale => locale.toLowerCase()),
);

/**
 * Resolve a client language code to a Chirp 3 locale.
 * Returns `null` when nothing maps, meaning the caller should use `['auto']`.
 */
export function resolveGoogleChirpLocale(language: string): string | null {
  const trimmed = language.trim();
  if (trimmed.length === 0) return null;

  // Already a locale Chirp knows — forward it in the catalog's canonical casing.
  if (GOOGLE_CHIRP_LOCALES.has(trimmed.toLowerCase())) {
    return canonicalChirpCasing(trimmed);
  }
  return GOOGLE_CHIRP_LOCALE_BY_BASE[primarySubtag(trimmed)] ?? null;
}

/**
 * Google matches the region subtag case-sensitively, so `en-us` has to go back
 * out as `en-US`. Script subtags (`Hans`, `Guru`, `Hant`) are title-case.
 */
function canonicalChirpCasing(locale: string): string {
  const parts = locale.split('-');
  return parts
    .map((part, index) => {
      if (index === 0) return part.toLowerCase();
      if (part.length === 4) return part[0]!.toUpperCase() + part.slice(1).toLowerCase();
      return part.toUpperCase();
    })
    .join('-');
}

// ---------------------------------------------------------------------------
// Deepgram — language support varies BY MODEL
// ---------------------------------------------------------------------------

// The medical models are English-only. Sending `es` to nova-3-medical is not a
// niche edge case: the picker scopes its language list by TIER, not by model, so
// every non-English language stays selectable after the user switches to a
// medical model.
const DEEPGRAM_ENGLISH_ONLY: ReadonlySet<string> = new Set([
  'en', 'en-us', 'en-au', 'en-ca', 'en-gb', 'en-ie', 'en-in', 'en-nz',
]);

// Nova-2 predates the nova-3 language expansion. These are the codes nova-3
// added; on nova-2 they fall back to auto-detect rather than being sent.
// Doc-derived — the probe script's nova-2 rows are what will confirm it.
const DEEPGRAM_NOVA3_ONLY: ReadonlySet<string> = new Set([
  'be', 'bn', 'bs', 'fa', 'gu', 'he', 'hy', 'kn', 'lt', 'mk',
  'mr', 'ne', 'pa', 'sl', 'sr', 'ta', 'te', 'tl', 'ur',
]);

/**
 * Resolve a client language code for a Deepgram model.
 * Returns `null` when the model cannot do that language, meaning the caller
 * should fall back to `detect_language=true`.
 */
export function resolveDeepgramLanguage(model: string, language: string): string | null {
  const code = language.trim().toLowerCase();
  if (code.length === 0) return null;
  // `multi` is Deepgram's own multilingual sentinel, not a language.
  if (code === 'multi') return 'multi';

  if (model.includes('medical')) {
    return DEEPGRAM_ENGLISH_ONLY.has(code) ? code : null;
  }
  if (model.startsWith('nova-2') && DEEPGRAM_NOVA3_ONLY.has(primarySubtag(code))) {
    return null;
  }
  return code;
}

// ---------------------------------------------------------------------------
// Azure MAI-Transcribe — ISO-639-1, and Norwegian is `nb`
// ---------------------------------------------------------------------------

const AZURE_MAI_LOCALES: ReadonlySet<string> = new Set([
  'ar', 'as', 'bg', 'bn', 'ca', 'cs', 'da', 'de', 'el', 'en', 'es', 'et', 'fi', 'fr',
  'gu', 'hi', 'hu', 'id', 'it', 'ja', 'kn', 'ko', 'lt', 'ml', 'mr', 'nb', 'nl', 'or',
  'pa', 'pl', 'pt', 'ro', 'ru', 'sk', 'sl', 'sv', 'ta', 'te', 'th', 'tr', 'uk', 'vi',
]);

// The picker offers Norwegian as `no` (the macro-language). Azure lists only
// `nb` (Bokmål). `nn` (Nynorsk) has no Azure entry either, so it folds the
// same way rather than dropping to auto-detect.
const AZURE_MAI_ALIASES: Readonly<Record<string, string>> = {
  no: 'nb',
  nn: 'nb',
  iw: 'he', // legacy Hebrew code, though Azure lists neither — kept for the alias pass
};

/**
 * Resolve a client language code to an Azure MAI locale.
 * Returns `null` when Azure does not list the language, meaning the caller
 * should omit `definition.locales` and let Azure detect.
 */
export function resolveAzureMaiLocale(language: string): string | null {
  const base = primarySubtag(language);
  if (base.length === 0) return null;
  const aliased = AZURE_MAI_ALIASES[base] ?? base;
  return AZURE_MAI_LOCALES.has(aliased) ? aliased : null;
}

// ---------------------------------------------------------------------------
// ElevenLabs Scribe — ISO-639-3, tolerant of ISO-639-1
// ---------------------------------------------------------------------------

// Scribe takes ISO-639-3 and also accepts ISO-639-1, so most picker codes pass
// untouched. Only map the ones where the naive path lands on a code Scribe does
// NOT list — mapping everything would mean hand-maintaining a 99-row 639-1→639-3
// table for no gain.
const ELEVENLABS_ALIASES: Readonly<Record<string, string>> = {
  tl: 'fil', // 639-1 `tl` maps to `tgl`; Scribe lists Filipino as `fil`
  zh: 'cmn', // Scribe lists Mandarin as `cmn`, not `zho`
  jw: 'jav', // picker's Whisper-ism
  jv: 'jav',
  iw: 'heb', // legacy Hebrew code
  in: 'ind', // legacy Indonesian code
  nb: 'nor', // Scribe lists only the macro-language
  nn: 'nor',
};

/**
 * Resolve a client language code for ElevenLabs Scribe.
 * Region subtags are stripped — Scribe takes a bare language code only.
 */
export function resolveElevenLabsLanguage(language: string): string | null {
  const base = primarySubtag(language);
  if (base.length === 0) return null;
  return ELEVENLABS_ALIASES[base] ?? base;
}

// ---------------------------------------------------------------------------
// OpenAI — `gpt-transcribe` renamed the field
// ---------------------------------------------------------------------------

// The `gpt-transcribe` family takes a PLURAL `languages` field. The singular
// `language` that whisper-1 and the gpt-4o-* models use is accepted on the
// request and then ignored, so the language hint silently did nothing on the
// newest models.
const OPENAI_PLURAL_LANGUAGE_MODELS: ReadonlySet<string> = new Set([
  'gpt-transcribe',
  'gpt-live-transcribe',
]);

/** The multipart field name this model reads the language hint from. */
export function openaiLanguageField(model: string): 'language' | 'languages' {
  return OPENAI_PLURAL_LANGUAGE_MODELS.has(model) ? 'languages' : 'language';
}

// ---------------------------------------------------------------------------
// Gemini — the code goes into a PROMPT, so name the language
// ---------------------------------------------------------------------------

// Gemini gets the language as English prose, not as a request parameter. A bare
// two-letter code is a bad instruction there: `no` is also the English word
// "no", `is` is "is", `as` is "as", `so` is "so". Naming the language removes
// the ambiguity, and the code is kept alongside it as a tiebreaker.
const LANGUAGE_NAMES: Readonly<Record<string, string>> = {
  af: 'Afrikaans', am: 'Amharic', ar: 'Arabic', as: 'Assamese', ast: 'Asturian',
  az: 'Azerbaijani', ba: 'Bashkir', be: 'Belarusian', bg: 'Bulgarian', bn: 'Bengali',
  bo: 'Tibetan', br: 'Breton', bs: 'Bosnian', ca: 'Catalan', ceb: 'Cebuano',
  cs: 'Czech', cy: 'Welsh', da: 'Danish', de: 'German', el: 'Greek',
  en: 'English', es: 'Spanish', et: 'Estonian', eu: 'Basque', fa: 'Persian',
  fi: 'Finnish',
  fil: 'Filipino', fo: 'Faroese', fr: 'French', ga: 'Irish', gl: 'Galician',
  gu: 'Gujarati', ha: 'Hausa', haw: 'Hawaiian', he: 'Hebrew', hi: 'Hindi',
  hr: 'Croatian', ht: 'Haitian Creole', hu: 'Hungarian', hy: 'Armenian', id: 'Indonesian',
  ig: 'Igbo', is: 'Icelandic', it: 'Italian', iw: 'Hebrew', ja: 'Japanese',
  jw: 'Javanese', jv: 'Javanese', ka: 'Georgian', kk: 'Kazakh', km: 'Khmer',
  kn: 'Kannada', ko: 'Korean', ku: 'Kurdish', ky: 'Kyrgyz', la: 'Latin',
  lb: 'Luxembourgish', ln: 'Lingala', lo: 'Lao', lt: 'Lithuanian', lv: 'Latvian',
  mg: 'Malagasy', mi: 'Maori', mk: 'Macedonian', ml: 'Malayalam', mn: 'Mongolian',
  mr: 'Marathi', ms: 'Malay', mt: 'Maltese', my: 'Burmese', nb: 'Norwegian Bokmal',
  ne: 'Nepali', nl: 'Dutch', nn: 'Norwegian Nynorsk', no: 'Norwegian', nso: 'Northern Sotho',
  ny: 'Chichewa', oc: 'Occitan', or: 'Odia', pa: 'Punjabi', pl: 'Polish',
  ps: 'Pashto', pt: 'Portuguese', ro: 'Romanian', ru: 'Russian', sa: 'Sanskrit',
  sd: 'Sindhi', si: 'Sinhala', sk: 'Slovak', sl: 'Slovenian', sn: 'Shona',
  so: 'Somali', sq: 'Albanian', sr: 'Serbian', su: 'Sundanese', sv: 'Swedish',
  sw: 'Swahili', ta: 'Tamil', te: 'Telugu', tg: 'Tajik', th: 'Thai',
  tk: 'Turkmen', tl: 'Tagalog', tr: 'Turkish', tt: 'Tatar', uk: 'Ukrainian',
  ur: 'Urdu', uz: 'Uzbek', vi: 'Vietnamese', wo: 'Wolof', xh: 'Xhosa',
  yi: 'Yiddish', yo: 'Yoruba', yue: 'Cantonese', zh: 'Mandarin Chinese', zu: 'Zulu',
};

/**
 * An unambiguous English description of a language code, for a prompt.
 * Falls back to the bare code when the language is unknown — no worse than the
 * old behaviour, and Gemini can still often read it.
 */
export function describeLanguage(language: string): string {
  const base = primarySubtag(language);
  const name = LANGUAGE_NAMES[base];
  return name ? `${name} (code "${base}")` : `code "${base}"`;
}

/** Exported for the parity test only. */
export const __tables = {
  GOOGLE_CHIRP_LOCALE_BY_BASE,
  GOOGLE_CHIRP_LOCALES,
  AZURE_MAI_LOCALES,
  LANGUAGE_NAMES,
};
