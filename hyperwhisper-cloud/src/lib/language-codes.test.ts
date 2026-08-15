// Tests for the per-provider language-code resolvers.
//
// Two jobs. The behaviour tests pin the specific bugs these resolvers exist to
// fix, so a future "simplification" back to a bare `language.toLowerCase()`
// fails loudly instead of silently restoring the 400.
//
// The parity tests hold the tables to `shared-app-classification/cloud-stt-catalog.json`,
// which is the source of truth for what each upstream accepts. The catalog is
// read at RUNTIME rather than imported, because it sits outside this package —
// the Fly image only copies `src/`, so a static import would resolve here and
// not in the container.

import { describe, expect, test } from 'bun:test';
import {
  __tables,
  describeLanguage,
  openaiLanguageField,
  primarySubtag,
  resolveAzureMaiLocale,
  resolveDeepgramLanguage,
  resolveElevenLabsLanguage,
  resolveGoogleChirpLocale,
} from './language-codes';

const CATALOG_PATH = `${import.meta.dir}/../../../shared-app-classification/cloud-stt-catalog.json`;

interface CatalogProvider {
  id: string;
  languages?: { codes?: string[] | string };
}

async function catalogCodes(providerId: string): Promise<string[]> {
  const catalog = (await Bun.file(CATALOG_PATH).json()) as { providers: CatalogProvider[] };
  const provider = catalog.providers.find(p => p.id === providerId);
  if (!provider) throw new Error(`catalog has no provider "${providerId}"`);
  const codes = provider.languages?.codes;
  if (!Array.isArray(codes)) throw new Error(`catalog provider "${providerId}" has no code list`);
  return codes;
}

describe('primarySubtag', () => {
  test('strips region and script subtags', () => {
    expect(primarySubtag('en-US')).toBe('en');
    expect(primarySubtag('pt_BR')).toBe('pt');
    expect(primarySubtag('cmn-Hans-CN')).toBe('cmn');
    expect(primarySubtag('EN')).toBe('en');
  });
});

describe('resolveGoogleChirpLocale', () => {
  // The bug this whole module exists for: the picker sends `en`, Chirp 3 wants
  // a locale, and the raw forward was a permanent 400 that the client retried
  // four times before surfacing.
  test('expands a bare subtag to a locale', () => {
    expect(resolveGoogleChirpLocale('en')).toBe('en-US');
    expect(resolveGoogleChirpLocale('fr')).toBe('fr-FR');
    expect(resolveGoogleChirpLocale('pt')).toBe('pt-BR');
  });

  test('maps the picker code space, not the ISO one', () => {
    // Google still uses the retired `iw` for Hebrew, and the picker inherited
    // Whisper's `jw` for Javanese.
    expect(resolveGoogleChirpLocale('he')).toBe('iw-IL');
    expect(resolveGoogleChirpLocale('jw')).toBe('jv-ID');
    expect(resolveGoogleChirpLocale('zh')).toBe('cmn-Hans-CN');
    expect(resolveGoogleChirpLocale('yue')).toBe('yue-Hant-HK');
  });

  test('forwards a locale the client already region-qualified', () => {
    expect(resolveGoogleChirpLocale('en-GB')).toBe('en-GB');
    expect(resolveGoogleChirpLocale('pt-PT')).toBe('pt-PT');
    expect(resolveGoogleChirpLocale('es-MX')).toBe('es-MX');
  });

  test('repairs casing, including the script subtag', () => {
    expect(resolveGoogleChirpLocale('en-us')).toBe('en-US');
    expect(resolveGoogleChirpLocale('CMN-HANS-CN')).toBe('cmn-Hans-CN');
    expect(resolveGoogleChirpLocale('pa-guru-in')).toBe('pa-Guru-IN');
  });

  test('returns null for an unmappable code so the caller falls back to auto', () => {
    expect(resolveGoogleChirpLocale('zz')).toBeNull();
    expect(resolveGoogleChirpLocale('klingon')).toBeNull();
    expect(resolveGoogleChirpLocale('')).toBeNull();
  });
});

describe('resolveDeepgramLanguage', () => {
  test('the medical models are English-only', () => {
    expect(resolveDeepgramLanguage('nova-3-medical', 'en')).toBe('en');
    expect(resolveDeepgramLanguage('nova-3-medical', 'en-GB')).toBe('en-gb');
    expect(resolveDeepgramLanguage('nova-3-medical', 'es')).toBeNull();
    expect(resolveDeepgramLanguage('nova-2-medical', 'ja')).toBeNull();
  });

  test('nova-2 lacks the languages nova-3 added', () => {
    expect(resolveDeepgramLanguage('nova-3-general', 'ta')).toBe('ta');
    expect(resolveDeepgramLanguage('nova-2-general', 'ta')).toBeNull();
    expect(resolveDeepgramLanguage('nova-2-general', 'de')).toBe('de');
  });

  test('passes Deepgram\'s own multilingual sentinel through', () => {
    expect(resolveDeepgramLanguage('nova-3-general', 'multi')).toBe('multi');
  });
});

describe('resolveAzureMaiLocale', () => {
  test('folds Norwegian onto the code Azure documents', () => {
    // The picker offers `no`; Azure's table lists only `nb`.
    expect(resolveAzureMaiLocale('no')).toBe('nb');
    expect(resolveAzureMaiLocale('nn')).toBe('nb');
    expect(resolveAzureMaiLocale('nb')).toBe('nb');
  });

  test('strips the region', () => {
    expect(resolveAzureMaiLocale('en-US')).toBe('en');
  });

  test('returns null for a language Azure does not list', () => {
    expect(resolveAzureMaiLocale('zz')).toBeNull();
    expect(resolveAzureMaiLocale('cy')).toBeNull();
  });
});

describe('resolveElevenLabsLanguage', () => {
  test('maps the codes where stripping alone lands off the Scribe list', () => {
    expect(resolveElevenLabsLanguage('tl')).toBe('fil');
    expect(resolveElevenLabsLanguage('zh')).toBe('cmn');
    expect(resolveElevenLabsLanguage('jw')).toBe('jav');
  });

  test('passes an ISO-639-1 code through, region stripped', () => {
    expect(resolveElevenLabsLanguage('en')).toBe('en');
    expect(resolveElevenLabsLanguage('en-US')).toBe('en');
  });
});

describe('openaiLanguageField', () => {
  test('the gpt-transcribe family reads the plural field', () => {
    expect(openaiLanguageField('gpt-transcribe')).toBe('languages');
    expect(openaiLanguageField('gpt-live-transcribe')).toBe('languages');
  });

  test('every older model reads the singular field', () => {
    expect(openaiLanguageField('whisper-1')).toBe('language');
    expect(openaiLanguageField('gpt-4o-transcribe')).toBe('language');
    expect(openaiLanguageField('gpt-4o-mini-transcribe')).toBe('language');
  });
});

describe('describeLanguage', () => {
  test('names the codes that are also English words', () => {
    // A bare `no` in a prompt reads as the word "no", not as Norwegian.
    expect(describeLanguage('no')).toContain('Norwegian');
    expect(describeLanguage('is')).toContain('Icelandic');
    expect(describeLanguage('as')).toContain('Assamese');
    expect(describeLanguage('so')).toContain('Somali');
  });

  test('keeps the code alongside the name as a tiebreaker', () => {
    expect(describeLanguage('de')).toBe('German (code "de")');
  });

  test('falls back to the bare code rather than inventing a name', () => {
    expect(describeLanguage('zz')).toBe('code "zz"');
  });
});

describe('catalog parity', () => {
  test('the Chirp locale set matches the catalog exactly', async () => {
    const codes = await catalogCodes('googleChirp3');
    const fromCatalog = new Set(codes.map(c => c.toLowerCase()));
    expect([...__tables.GOOGLE_CHIRP_LOCALES].sort()).toEqual([...fromCatalog].sort());
  });

  test('every Chirp base code maps to a locale the catalog lists', async () => {
    const codes = await catalogCodes('googleChirp3');
    const fromCatalog = new Set(codes.map(c => c.toLowerCase()));
    const strays = Object.entries(__tables.GOOGLE_CHIRP_LOCALE_BY_BASE)
      .filter(([, locale]) => !fromCatalog.has(locale.toLowerCase()))
      .map(([base, locale]) => `${base} → ${locale}`);
    expect(strays).toEqual([]);
  });

  test('every Chirp language the catalog lists is reachable from a base code', async () => {
    // Guards the other direction: a language Google adds should not stay
    // unreachable just because nobody extended the base table.
    const codes = await catalogCodes('googleChirp3');
    const reachable = new Set(Object.values(__tables.GOOGLE_CHIRP_LOCALE_BY_BASE).map(l => l.toLowerCase()));
    const unreachableLanguages = [
      ...new Set(codes.map(c => primarySubtag(c))),
    ].filter(base => ![...reachable].some(locale => primarySubtag(locale) === base));
    expect(unreachableLanguages).toEqual([]);
  });

  test('the Azure locale set matches the catalog exactly', async () => {
    const codes = await catalogCodes('azureMaiTranscribe');
    expect([...__tables.AZURE_MAI_LOCALES].sort()).toEqual([...codes].sort());
  });

  test('every Chirp base code has a display name for the Gemini prompt', () => {
    const unnamed = Object.keys(__tables.GOOGLE_CHIRP_LOCALE_BY_BASE)
      .filter(base => !__tables.LANGUAGE_NAMES[base]);
    expect(unnamed).toEqual([]);
  });
});
