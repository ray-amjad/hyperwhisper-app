// Per-provider language-code normalization.
//
// The desktop pickers send a language code in ONE code space: a bare, lowercased
// primary subtag (`en`, `no`, `zh`), occasionally region-qualified (`zh-TW`).
// That is not what several upstreams accept.
// `shared-app-classification/cloud-stt-catalog.json` records the divergence per
// provider — Google Chirp wants a full BCP-47 locale, ElevenLabs wants ISO-639-3,
// Azure documents `nb` and not `no` — and until this module existed the adapters
// forwarded the picker code raw. Google then 400s, and because
// `TranscriptionError.isRetryable` treats every 5xx/4xx server error as
// retryable, the app retried a permanent rejection four times before surfacing
// it. The user-visible symptom was a ~27-second hang, not an error.
//
// THE FALLBACK IS ALWAYS AUTO-DETECT, NEVER AN ERROR. When a code has no valid
// mapping for the chosen provider and model, {@link resolveProviderLanguage}
// returns `null` and the adapter drops the language, letting the upstream detect
// it. A slightly worse transcript beats a failed one, and the picker should not
// have offered the combination in the first place — the `language_unmappable`
// log event is how we find out that it did.
//
// ONE ENTRY POINT, ONE EVENT. Adapters call `resolveProviderLanguage(...)` and
// read the single return value; they do not call the per-provider resolvers, do
// not re-run a resolver "for the log", and do not emit their own language event.
// There was briefly a second event name (`language_unsupported_for_model`) with
// a different `fallback` value, which meant "how often are we discarding a
// user's language?" needed two Axiom queries that could not be summed. The
// reason now rides on the one event as a `reason` field.
//
// WHERE THE TABLES COME FROM. Two shared files are the source of truth:
//
//   * `shared-models/models-catalog.json` — `supportedLanguages` per MODEL, in
//     the picker's own code space. This is exactly the key space this module
//     receives, so every allow-list below is a verbatim mirror of it.
//   * `shared-app-classification/cloud-stt-catalog.json` — the upstream-native
//     code space per provider (Chirp's BCP-47 locales, Azure's ISO-639-1 list).
//     Every value this module emits has to exist there.
//
// The tables are mirrored rather than imported because the Fly image copies only
// `src/` (see Dockerfile) — a runtime read of `../../shared-models/...` resolves
// in CI and 404s in production. `language-codes.test.ts` closes that gap by
// asserting the mirrors equal the catalogs EXACTLY, in both directions: a
// catalog edit that outdates this file fails CI rather than silently
// reintroducing the 400. Both key space and value space are checked — the
// previous test only checked values, which is how `tl` came to be missing from
// the Chirp table (every Filipino dictation silently went out as auto-detect)
// and how `DEEPGRAM_NOVA3_ONLY` drifted in three directions at once.
//
// Only genuinely EDITORIAL data is hand-written here: which region we pick when
// an upstream offers several for one language, and the picker-code aliases the
// catalogs cannot express.

import type { ProviderRequestContext } from '../providers/types';
import { isExplicitLanguage, logProviderEvent } from '../providers/utils';

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

/** Lowercased subtags of a tag: `zh_TW` → `['zh', 'tw']`. */
function subtags(code: string): string[] {
  return code.toLowerCase().split(/[-_]/).filter(part => part.length > 0);
}

/**
 * BCP-47 canonical casing: primary subtag lowercase, 4-letter script subtag
 * title-case, everything else upper-case. `en-us` → `en-US`,
 * `cmn-hans-cn` → `cmn-Hans-CN`.
 *
 * Google matches the region subtag case-sensitively, so this is load-bearing for
 * Chirp rather than cosmetic.
 */
function canonicalBcp47(tag: string): string {
  return tag
    .split(/[-_]/)
    .map((part, index) => {
      if (index === 0) return part.toLowerCase();
      if (part.length === 4) return part[0]!.toUpperCase() + part.slice(1).toLowerCase();
      return part.toUpperCase();
    })
    .join('-');
}

// ---------------------------------------------------------------------------
// The one entry point
// ---------------------------------------------------------------------------

/** Providers whose language code needs mapping before it goes upstream. */
export type LanguageMappedProvider =
  | 'google-chirp'
  | 'deepgram'
  | 'azure-mai'
  | 'elevenlabs'
  | 'openai';

/**
 * Why an explicitly requested language was discarded.
 *
 * - `no_locale` — the provider has no code for this language at all.
 * - `model_unsupported` — the provider has it, the SELECTED MODEL does not.
 *   This one means the client's per-model language scoping has drifted from the
 *   catalog, which is a different bug from the picker offering a language the
 *   vendor never had.
 */
export type LanguageFallbackReason = 'no_locale' | 'model_unsupported';

type Resolution = { code: string } | { code: null; reason: LanguageFallbackReason };

const NO_LOCALE: Resolution = { code: null, reason: 'no_locale' };
const MODEL_UNSUPPORTED: Resolution = { code: null, reason: 'model_unsupported' };

export interface ResolveProviderLanguageOptions {
  provider: LanguageMappedProvider;
  /** Catalog model id — `nova-3-medical`, `chirp_3`, … */
  model: string;
  /** Raw client language param: a code, `auto`, `''`, or absent. */
  language: string | undefined;
  context?: ProviderRequestContext;
  /**
   * Number of custom-vocabulary terms riding on this request, if any. Deepgram
   * honours `keyterm`/`keywords` only in monolingual mode, so dropping the
   * language quietly voids the user's vocabulary too — that second loss is
   * reported on the same event instead of being invisible. See GROUP D note on
   * `buildDeepgramUrl`.
   */
  vocabularyTermCount?: number;
}

/**
 * The language code to send upstream, or `null` for "omit it and let the
 * upstream auto-detect".
 *
 * Folds the `auto`/empty check, dispatches per provider, and emits exactly one
 * `provider.language_unmappable` event when an explicitly requested language
 * cannot be honoured. Callers do nothing but forward the return value.
 */
export function resolveProviderLanguage({
  provider,
  model,
  language,
  context,
  vocabularyTermCount = 0,
}: ResolveProviderLanguageOptions): string | null {
  if (!isExplicitLanguage(language)) return null;

  const resolution = resolveExplicit(provider, model, language);
  if (resolution.code !== null) return resolution.code;

  logProviderEvent(provider, 'language_unmappable', {
    model,
    requested: language,
    reason: resolution.reason,
    // Every provider's fallback is the same thing spelled differently upstream
    // (`detect_language=true`, `languageCodes: ['auto']`, omitting the field).
    // One value so the event is countable across providers.
    fallback: 'auto_detect',
    // > 0 means the language fallback also cost the user their custom
    // vocabulary — Deepgram drops keyterms outside monolingual mode.
    vocabularyTermsAtRisk: provider === 'deepgram' ? vocabularyTermCount : 0,
  }, context);
  return null;
}

function resolveExplicit(
  provider: LanguageMappedProvider,
  model: string,
  language: string,
): Resolution {
  switch (provider) {
    case 'google-chirp': return resolveGoogleChirpLocale(language);
    case 'deepgram': return resolveDeepgramLanguage(model, language);
    case 'azure-mai': return resolveAzureMaiLocale(language);
    case 'elevenlabs': return resolveElevenLabsLanguage(language);
    case 'openai': return resolveOpenAILanguage(language);
  }
}

// ---------------------------------------------------------------------------
// Google Chirp 3 — needs a full BCP-47 locale
// ---------------------------------------------------------------------------

// Chirp 3 rejects a bare primary subtag with 400 INVALID_ARGUMENT. `sw` is the
// single exception in Google's own list, and it is region-qualified here anyway
// so every row of this table has the same shape.
//
// EDITORIAL: where Google publishes several regions for one language we pick ONE
// default. The picker only says "Portuguese", so the region is our call: we take
// the largest speaker population (pt-BR over pt-PT, es-ES for European Spanish
// as the unmarked form, en-US). A user who needs the other region qualifies the
// code and gets it honoured — see resolveGoogleChirpLocale.
//
// Keys are in the PICKER's code space, which is not always the catalog's:
// Hebrew is `he` here and `iw-IL` at Google, Javanese is `jw` here (a Whisper-ism
// the shared language list inherited) and `jv-ID` at Google, Filipino is `tl`
// here and `fil-PH` at Google, and Mandarin is `zh` here and `cmn-Hans-CN` at
// Google. `language-codes.test.ts` asserts every picker code for `chirp_3` in
// models-catalog.json has a row — the missing `tl` row is why every Filipino
// dictation used to go out as auto-detect.
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
  fil: 'fil-PH', // Google's own code; the picker sends `tl` (below)
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
  tl: 'fil-PH', // the picker's Filipino code — Google spells it `fil`
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
 * Every locale Chirp 3 accepts, per the catalog, lowercased for lookup.
 * Mirrors `googleChirp3.languages.codes` in cloud-stt-catalog.json exactly.
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
 *
 * A code that ALREADY carries a region or script is honoured, not flattened:
 * `zh-TW` resolves through the picker→Google base alias (`zh` → `cmn`) to
 * `cmn-Hant-TW`, the Traditional-script locale, rather than landing on this
 * file's default `cmn-Hans-CN` and returning Simplified output under a 200 OK.
 *
 * When the qualifier cannot be honoured the answer depends on whether guessing
 * is safe. `en-CA` → `en-US`: Chirp has no Canadian English and the default
 * region carries no script, so the worst case is a regional accent model. But
 * `zh-HK` or `pa-PK` → `null`: the default for those bases pins a SCRIPT
 * (`cmn-Hans-CN`, `pa-Guru-IN`), and silently transcribing Traditional Chinese
 * with a Simplified model — or Shahmukhi Punjabi with a Gurmukhi one — is a
 * wrong answer dressed as a right one. Auto-detect is better than that.
 */
function resolveGoogleChirpLocale(language: string): Resolution {
  const trimmed = language.trim();
  if (trimmed.length === 0) return NO_LOCALE;

  // Already a locale Chirp knows — forward it in the catalog's canonical casing.
  if (GOOGLE_CHIRP_LOCALES.has(trimmed.toLowerCase())) {
    return { code: canonicalBcp47(trimmed) };
  }

  const parts = subtags(trimmed);
  const base = parts[0] ?? '';
  const defaultLocale = GOOGLE_CHIRP_LOCALE_BY_BASE[base];
  if (!defaultLocale) return NO_LOCALE;
  if (parts.length === 1) return { code: defaultLocale };

  // Region/script-qualified. Look for a catalog locale under Google's spelling
  // of this language that carries every qualifier the client asked for.
  const googleBase = primarySubtag(defaultLocale);
  const qualifiers = parts.slice(1);
  for (const candidate of GOOGLE_CHIRP_LOCALES) {
    const candidateParts = candidate.split('-');
    if (candidateParts[0] !== googleBase) continue;
    const rest = candidateParts.slice(1);
    if (qualifiers.every(q => rest.includes(q))) return { code: canonicalBcp47(candidate) };
  }

  // Nothing honours the qualifier. Falling back to the default region is only
  // safe when the default does not also pin a script (see doc comment).
  if (defaultLocale.split('-').length > 2) return NO_LOCALE;
  return { code: defaultLocale };
}

// ---------------------------------------------------------------------------
// Deepgram — language support varies BY MODEL
// ---------------------------------------------------------------------------

// Verbatim mirror of `supportedLanguages` per Deepgram model in
// shared-models/models-catalog.json, which is the picker's own code space.
// The hand-typed `DEEPGRAM_NOVA3_ONLY` set this replaces had drifted from the
// catalog in three directions at once: it downgraded Lithuanian on nova-2 (which
// nova-2 supports), still forwarded Arabic and Croatian to nova-2 (which it does
// not), carried four dead rows, and never noticed that `th`/`zh` are nova-2-ONLY
// and were being forwarded to nova-3. Deriving the whole matrix instead of the
// difference removes the class of mistake, not one instance of it.
//
// The medical models being English-only is not a niche edge case: the picker
// scopes its language list by TIER, not by model, so every non-English language
// stays selectable after the user switches to a medical model.
const DEEPGRAM_MODEL_LANGUAGES: Readonly<Record<string, ReadonlySet<string>>> = {
  'nova-3-general': new Set([
    'ar', 'be', 'bg', 'bn', 'bs', 'ca', 'cs', 'da', 'de', 'el', 'en', 'es', 'et', 'fa',
    'fi', 'fr', 'he', 'hi', 'hr', 'hu', 'id', 'it', 'ja', 'kn', 'ko', 'lt', 'lv', 'mk',
    'mr', 'ms', 'nl', 'no', 'pl', 'pt', 'ro', 'ru', 'sk', 'sl', 'sr', 'sv', 'ta', 'te',
    'tl', 'tr', 'uk', 'ur', 'vi',
  ]),
  'nova-3-medical': new Set(['en']),
  'nova-2-general': new Set([
    'bg', 'ca', 'cs', 'da', 'de', 'el', 'en', 'es', 'et', 'fi', 'fr', 'hi', 'hu', 'id',
    'it', 'ja', 'ko', 'lt', 'lv', 'ms', 'nl', 'no', 'pl', 'pt', 'ro', 'ru', 'sk', 'sv',
    'th', 'tr', 'uk', 'vi', 'zh',
  ]),
  'nova-2-medical': new Set(['en']),
};

const DEEPGRAM_DEFAULT_MODEL = 'nova-3-general';

// `multi` is Deepgram's code-switching sentinel, not a language. It is a
// nova-3 feature and is meaningless on the English-only medical models, so it is
// scoped rather than short-circuited: the previous `if (code === 'multi')`
// returned BEFORE the medical clamp, which let the sentinel through on
// nova-3-medical. `transcribe.ts` reads `language` as an unvalidated raw query
// param, so that was reachable from outside even though no shipped client emits
// it.
const DEEPGRAM_MULTI_MODELS: ReadonlySet<string> = new Set(['nova-3-general']);

// Region-qualified codes Deepgram itself lists (mirror of the hyphenated entries
// in `deepgramNova3.languages.codes`). A client that qualifies a code we can
// honour keeps the regional model — dropping `en-GB` to `en` would quietly move
// British dictation onto the US spelling model.
const DEEPGRAM_REGIONAL_CODES: ReadonlySet<string> = new Set([
  'ar-ae', 'ar-sa', 'ar-qa', 'ar-kw', 'ar-sy', 'ar-lb', 'ar-ps', 'ar-jo', 'ar-eg',
  'ar-sd', 'ar-td', 'ar-ma', 'ar-dz', 'ar-tn', 'ar-iq', 'ar-ir',
  'zh-hk', 'zh-cn', 'zh-hans', 'zh-tw', 'zh-hant',
  'da-dk', 'nl-be', 'en-us', 'en-au', 'en-gb', 'en-in', 'en-nz', 'fr-ca', 'de-ch',
  'gu-in', 'ko-kr', 'pt-br', 'pt-pt', 'es-419', 'sv-se', 'th-th',
]);

/**
 * Resolve a client language code for a Deepgram model.
 *
 * Unknown model ids fall back to the default model's list; the parity test
 * asserts this table's keys are exactly the Deepgram model ids the catalog
 * ships, so an unknown id here means a model was added without updating either.
 */
function resolveDeepgramLanguage(model: string, language: string): Resolution {
  const code = language.trim().toLowerCase();
  if (code.length === 0) return NO_LOCALE;

  if (code === 'multi') {
    return DEEPGRAM_MULTI_MODELS.has(model) ? { code: 'multi' } : MODEL_UNSUPPORTED;
  }

  const supported = DEEPGRAM_MODEL_LANGUAGES[model] ?? DEEPGRAM_MODEL_LANGUAGES[DEEPGRAM_DEFAULT_MODEL]!;
  const base = primarySubtag(code);
  if (!supported.has(base)) {
    // Distinguish "Deepgram has never had this language" from "this MODEL
    // cannot do it" — the second means the client's per-model scoping drifted.
    const anyModel = Object.values(DEEPGRAM_MODEL_LANGUAGES).some(set => set.has(base));
    return anyModel ? MODEL_UNSUPPORTED : NO_LOCALE;
  }

  return { code: DEEPGRAM_REGIONAL_CODES.has(code) ? code : base };
}

// ---------------------------------------------------------------------------
// Azure MAI-Transcribe — ISO-639-1, and Norwegian is `nb`
// ---------------------------------------------------------------------------

// Mirror of `azureMaiTranscribe.languages.codes` in cloud-stt-catalog.json.
const AZURE_MAI_LOCALES: ReadonlySet<string> = new Set([
  'ar', 'as', 'bg', 'bn', 'ca', 'cs', 'da', 'de', 'el', 'en', 'es', 'et', 'fi', 'fr',
  'gu', 'hi', 'hu', 'id', 'it', 'ja', 'kn', 'ko', 'lt', 'ml', 'mr', 'nb', 'nl', 'or',
  'pa', 'pl', 'pt', 'ro', 'ru', 'sk', 'sl', 'sv', 'ta', 'te', 'th', 'tr', 'uk', 'vi',
]);

// EDITORIAL: the picker offers Norwegian as `no` (the macro-language). Azure
// lists only `nb` (Bokmål). `nn` (Nynorsk) has no Azure entry either, so it folds
// the same way rather than dropping to auto-detect.
const AZURE_MAI_ALIASES: Readonly<Record<string, string>> = {
  no: 'nb',
  nn: 'nb',
  iw: 'he', // legacy Hebrew code, though Azure lists neither — kept for the alias pass
};

/** Resolve a client language code to an Azure MAI locale. */
function resolveAzureMaiLocale(language: string): Resolution {
  const base = primarySubtag(language);
  if (base.length === 0) return NO_LOCALE;
  const aliased = AZURE_MAI_ALIASES[base] ?? base;
  return AZURE_MAI_LOCALES.has(aliased) ? { code: aliased } : NO_LOCALE;
}

// ---------------------------------------------------------------------------
// ElevenLabs Scribe — ISO-639-3, tolerant of ISO-639-1
// ---------------------------------------------------------------------------

// Verbatim mirror of `scribe_v2.supportedLanguages` in models-catalog.json — the
// picker code space, already reduced from Scribe's 99 ISO-639-3 codes. Scribe
// accepts ISO-639-1 input, so a listed code goes out unchanged.
//
// This allow-list is the whole point: without it `resolveElevenLabsLanguage`
// could never return null, so a saved Mode on Basque or Yiddish forwarded a code
// Scribe does not have, Scribe 4xx'd, `providerHttpError` raised
// `ProviderInputError`, and `transcribe.ts` walked the elevenlabs fallback chain
// down to deepgram/groq — moving the user off the provider they picked, with no
// telemetry saying why. Every sibling resolver returns null here; this one now
// does too.
const ELEVENLABS_LANGUAGES: ReadonlySet<string> = new Set([
  'af', 'am', 'ar', 'as', 'az', 'be', 'bg', 'bn', 'bs', 'ca', 'cs', 'cy', 'da', 'de',
  'el', 'en', 'es', 'et', 'fa', 'fi', 'fr', 'gl', 'gu', 'ha', 'he', 'hi', 'hr', 'hu',
  'hy', 'id', 'is', 'it', 'ja', 'jw', 'ka', 'kk', 'km', 'kn', 'ko', 'lb', 'ln', 'lo',
  'lt', 'lv', 'mi', 'mk', 'ml', 'mn', 'mr', 'ms', 'mt', 'my', 'ne', 'nl', 'no', 'oc',
  'pa', 'pl', 'ps', 'pt', 'ro', 'ru', 'sd', 'sk', 'sl', 'sn', 'so', 'sr', 'sv', 'sw',
  'ta', 'te', 'tg', 'th', 'tl', 'tr', 'uk', 'ur', 'uz', 'vi', 'yo', 'yue', 'zh',
]);

// EDITORIAL: codes where passing the picker's spelling through lands on
// something Scribe does not list. Alias KEYS are allowed even when they are not
// in the list above — they are the codes we can explicitly fold, which is why a
// saved macOS Mode on `nn` (Nynorsk) keeps working instead of being reset.
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
function resolveElevenLabsLanguage(language: string): Resolution {
  const base = primarySubtag(language);
  if (base.length === 0) return NO_LOCALE;
  const alias = ELEVENLABS_ALIASES[base];
  if (alias) return { code: alias };
  return ELEVENLABS_LANGUAGES.has(base) ? { code: base } : NO_LOCALE;
}

// ---------------------------------------------------------------------------
// OpenAI — ISO-639-1 only
// ---------------------------------------------------------------------------

// OpenAI's language hint is documented as ISO-639-1. The picker's list comes
// from Whisper's tokenizer, which is NOT purely 639-1: `haw` (Hawaiian) and
// `yue` (Cantonese) are 639-3, and `jw` is a Whisper-ism that the tokenizer —
// and therefore the API — does accept. Forwarding `haw`/`yue` sends a value the
// field cannot express; omit the hint and auto-detect instead. Mirror of the
// 2-letter subset of `openaiWhisper.languages.codes` in cloud-stt-catalog.json.
const OPENAI_LANGUAGES: ReadonlySet<string> = new Set([
  'af', 'am', 'ar', 'as', 'az', 'ba', 'be', 'bg', 'bn', 'bo', 'br', 'bs', 'ca', 'cs',
  'cy', 'da', 'de', 'el', 'en', 'es', 'et', 'eu', 'fa', 'fi', 'fo', 'fr', 'gl', 'gu',
  'ha', 'he', 'hi', 'hr', 'ht', 'hu', 'hy', 'id', 'is', 'it', 'ja', 'jw', 'ka', 'kk',
  'km', 'kn', 'ko', 'la', 'lb', 'ln', 'lo', 'lt', 'lv', 'mg', 'mi', 'mk', 'ml', 'mn',
  'mr', 'ms', 'mt', 'my', 'ne', 'nl', 'nn', 'no', 'oc', 'pa', 'pl', 'ps', 'pt', 'ro',
  'ru', 'sa', 'sd', 'si', 'sk', 'sl', 'sn', 'so', 'sq', 'sr', 'su', 'sv', 'sw', 'ta',
  'te', 'tg', 'th', 'tk', 'tl', 'tr', 'tt', 'uk', 'ur', 'uz', 'vi', 'yi', 'yo', 'zh',
]);

function resolveOpenAILanguage(language: string): Resolution {
  const base = primarySubtag(language);
  if (base.length === 0) return NO_LOCALE;
  return OPENAI_LANGUAGES.has(base) ? { code: base } : NO_LOCALE;
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
 *
 * Keeps the code REGION-QUALIFIED. Gemini is the one tier with no picker
 * narrowing (the catalog marks its code list `"unverified"` and there is no
 * `gemini` entry in STTCapabilities.swift), so `zh-TW`, `pt-PT` and `en-GB` all
 * reach it. Reducing the tag to its primary subtag here threw away
 * Traditional-vs-Simplified, pt-PT vs pt-BR and en-GB vs en-US spelling — the
 * old code interpolated the raw tag and did not.
 *
 * Falls back to the bare code when the language is unknown — no worse than the
 * old behaviour, and Gemini can still often read it.
 */
export function describeLanguage(language: string): string {
  const tag = canonicalBcp47(language.trim());
  const name = LANGUAGE_NAMES[primarySubtag(language)];
  return name ? `${name} (code "${tag}")` : `code "${tag}"`;
}

/** Exported for the parity test only. */
export const __tables = {
  GOOGLE_CHIRP_LOCALE_BY_BASE,
  GOOGLE_CHIRP_LOCALES,
  DEEPGRAM_MODEL_LANGUAGES,
  DEEPGRAM_REGIONAL_CODES,
  AZURE_MAI_LOCALES,
  AZURE_MAI_ALIASES,
  ELEVENLABS_LANGUAGES,
  ELEVENLABS_ALIASES,
  OPENAI_LANGUAGES,
  LANGUAGE_NAMES,
};
