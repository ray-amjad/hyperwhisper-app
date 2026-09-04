// Tests for the per-provider language-code resolvers.
//
// Three jobs.
//
// The BEHAVIOUR tests pin the specific bugs these resolvers exist to fix, so a
// future "simplification" back to a bare `language.toLowerCase()` fails loudly
// instead of silently restoring the 400.
//
// The ENTRY-POINT tests pin the contract every adapter depends on: auto-detect
// is silent, an unmappable language logs exactly ONE `language_unmappable`
// event, and that event carries a `reason` so one Axiom query answers "how often
// are we discarding a user's language?".
//
// The PARITY tests hold the mirrored tables to the two shared catalogs:
//
//   * `shared-models/models-catalog.json` — `supportedLanguages` per model, in
//     the picker code space. This is the KEY space the resolvers receive.
//   * `shared-app-classification/cloud-stt-catalog.json` — the upstream-native
//     code space. This is the VALUE space the resolvers emit.
//
// Both directions are checked, deliberately. The previous suite compared only
// the tables' VALUES to the catalog, and that is exactly why three tables had
// already drifted while CI stayed green: `tl` was missing from the Chirp table
// (so every Filipino dictation silently went out as auto-detect),
// `DEEPGRAM_NOVA3_ONLY` had drifted from the catalog in three directions at
// once, and `resolveElevenLabsLanguage` had no allow-list at all so it could
// never return null. A test that cannot see a missing KEY cannot catch any of
// those.
//
// Both catalogs are read at RUNTIME rather than imported, because they sit
// outside this package — the Fly image only copies `src/`, so a static import
// would resolve here and not in the container. That is also why the tables are
// mirrored into the source at all rather than derived at startup.

import { afterEach, describe, expect, test } from 'bun:test';
import {
  __tables,
  describeLanguage,
  primarySubtag,
  resolveProviderLanguage,
  type LanguageMappedProvider,
} from './language-codes';

const STT_CATALOG_PATH = `${import.meta.dir}/../../../shared-app-classification/cloud-stt-catalog.json`;
const MODELS_CATALOG_PATH = `${import.meta.dir}/../../../shared-models/models-catalog.json`;

interface CatalogProvider {
  id: string;
  languages?: { codes?: string[] | string };
}

interface CatalogModel {
  id: string;
  provider?: string;
  supportedLanguages?: string[];
}

/** Upstream-native code list for a provider, from cloud-stt-catalog.json. */
async function upstreamCodes(providerId: string): Promise<string[]> {
  const catalog = (await Bun.file(STT_CATALOG_PATH).json()) as { providers: CatalogProvider[] };
  const provider = catalog.providers.find(p => p.id === providerId);
  if (!provider) throw new Error(`catalog has no provider "${providerId}"`);
  const codes = provider.languages?.codes;
  if (!Array.isArray(codes)) throw new Error(`catalog provider "${providerId}" has no code list`);
  return codes;
}

/** Picker-space language list for one model, from models-catalog.json. */
async function pickerCodes(modelId: string): Promise<string[]> {
  const catalog = (await Bun.file(MODELS_CATALOG_PATH).json()) as { models: CatalogModel[] };
  const model = catalog.models.find(m => m.id === modelId);
  if (!model) throw new Error(`models-catalog has no model "${modelId}"`);
  if (!Array.isArray(model.supportedLanguages)) {
    throw new Error(`models-catalog model "${modelId}" has no supportedLanguages`);
  }
  return model.supportedLanguages;
}

/** Every model id models-catalog.json ships for one provider key. */
async function modelIdsFor(providerKey: string): Promise<string[]> {
  const catalog = (await Bun.file(MODELS_CATALOG_PATH).json()) as { models: CatalogModel[] };
  return catalog.models
    .filter(m => m.provider === providerKey && Array.isArray(m.supportedLanguages))
    .map(m => m.id);
}

/** Resolve without the logging side effect getting in the way of a plain read. */
function resolve(provider: LanguageMappedProvider, model: string, language: string | undefined): string | null {
  return resolveProviderLanguage({ provider, model, language });
}

// --- log capture -----------------------------------------------------------

type LoggedEvent = { event: string; details: Record<string, unknown> };

function captureEvents(fn: () => void): LoggedEvent[] {
  const events: LoggedEvent[] = [];
  const original = console.log;
  console.log = (...args: unknown[]) => {
    if (typeof args[0] === 'string' && args[0].startsWith('provider.')) {
      events.push({ event: args[0].slice('provider.'.length), details: (args[1] ?? {}) as Record<string, unknown> });
    }
  };
  try {
    fn();
  } finally {
    console.log = original;
  }
  return events;
}

afterEach(() => {
  // captureEvents restores in a finally, but a throw inside a spy would not.
  expect(typeof console.log).toBe('function');
});

describe('primarySubtag', () => {
  test('strips region and script subtags', () => {
    expect(primarySubtag('en-US')).toBe('en');
    expect(primarySubtag('pt_BR')).toBe('pt');
    expect(primarySubtag('cmn-Hans-CN')).toBe('cmn');
    expect(primarySubtag('EN')).toBe('en');
  });
});

describe('resolveProviderLanguage — the shared contract', () => {
  test('auto-detect is silent: no event, no code', () => {
    for (const language of [undefined, '', 'auto', 'AUTO']) {
      const events = captureEvents(() => {
        expect(resolve('google-chirp', 'chirp_3', language)).toBeNull();
      });
      expect(events).toEqual([]);
    }
  });

  test('an unmappable language logs exactly ONE event, under ONE name', () => {
    const events = captureEvents(() => {
      expect(resolveProviderLanguage({ provider: 'google-chirp', model: 'chirp_3', language: 'zz' })).toBeNull();
    });
    expect(events.length).toBe(1);
    expect(events[0]!.event).toBe('language_unmappable');
    expect(events[0]!.details.requested).toBe('zz');
    expect(events[0]!.details.reason).toBe('no_locale');
    // One fallback value across every provider, so the event is summable.
    expect(events[0]!.details.fallback).toBe('auto_detect');
  });

  test('the reason separates "vendor never had it" from "this model cannot"', () => {
    // Deepgram has Tamil; nova-2 does not.
    const modelDrift = captureEvents(() => {
      resolveProviderLanguage({ provider: 'deepgram', model: 'nova-2-general', language: 'ta' });
    });
    expect(modelDrift[0]!.details.reason).toBe('model_unsupported');

    // Deepgram has never had Welsh on any model.
    const neverHad = captureEvents(() => {
      resolveProviderLanguage({ provider: 'deepgram', model: 'nova-3-general', language: 'cy' });
    });
    expect(neverHad[0]!.details.reason).toBe('no_locale');
  });

  test('a successful resolution logs nothing', () => {
    const events = captureEvents(() => {
      expect(resolveProviderLanguage({ provider: 'deepgram', model: 'nova-3-general', language: 'de' })).toBe('de');
    });
    expect(events).toEqual([]);
  });

  test('the Deepgram vocabulary loss rides on the same event', () => {
    // GROUP D: nova-3 honours keyterm only in monolingual mode, so a language
    // fallback silently voids the user's custom vocabulary as well. That second
    // loss has to be countable, not invisible.
    const events = captureEvents(() => {
      resolveProviderLanguage({
        provider: 'deepgram', model: 'nova-3-medical', language: 'es', vocabularyTermCount: 7,
      });
    });
    expect(events.length).toBe(1);
    expect(events[0]!.details.vocabularyTermsAtRisk).toBe(7);
  });
});

describe('meta', () => {
  test('maps picker codes and region-qualified codes to Muse language names', () => {
    expect(resolve('meta', 'muse-voice-transcribe-1.0', 'en-US')).toBe('English');
    expect(resolve('meta', 'muse-voice-transcribe-1.0', 'zh-TW')).toBe('Mandarin Chinese');
    expect(resolve('meta', 'muse-voice-transcribe-1.0', 'tl')).toBe('Tagalog');
  });

  test('maps legacy saved-mode aliases', () => {
    expect(resolve('meta', 'muse-voice-transcribe-1.0', 'iw')).toBe('Hebrew');
    expect(resolve('meta', 'muse-voice-transcribe-1.0', 'in')).toBe('Indonesian');
    expect(resolve('meta', 'muse-voice-transcribe-1.0', 'cmn-Hans')).toBe('Mandarin Chinese');
  });

  test('falls back to auto-detect with the shared telemetry contract', () => {
    const events = captureEvents(() => {
      expect(resolve('meta', 'muse-voice-transcribe-1.0', 'zz')).toBeNull();
    });
    expect(events).toHaveLength(1);
    expect(events[0]).toMatchObject({
      event: 'language_unmappable',
      details: {
        model: 'muse-voice-transcribe-1.0',
        requested: 'zz',
        reason: 'no_locale',
        fallback: 'auto_detect',
      },
    });
  });
});

describe('google-chirp', () => {
  // The bug this whole module exists for: the picker sends `en`, Chirp 3 wants
  // a locale, and the raw forward was a permanent 400 that the client retried
  // four times before surfacing.
  test('expands a bare subtag to a locale', () => {
    expect(resolve('google-chirp', 'chirp_3', 'en')).toBe('en-US');
    expect(resolve('google-chirp', 'chirp_3', 'fr')).toBe('fr-FR');
    expect(resolve('google-chirp', 'chirp_3', 'pt')).toBe('pt-BR');
  });

  test('maps the picker code space, not the ISO one', () => {
    // Google still uses the retired `iw` for Hebrew, the picker inherited
    // Whisper's `jw` for Javanese, and Filipino is `tl` in the picker.
    expect(resolve('google-chirp', 'chirp_3', 'he')).toBe('iw-IL');
    expect(resolve('google-chirp', 'chirp_3', 'jw')).toBe('jv-ID');
    expect(resolve('google-chirp', 'chirp_3', 'zh')).toBe('cmn-Hans-CN');
    expect(resolve('google-chirp', 'chirp_3', 'yue')).toBe('yue-Hant-HK');
  });

  test('Filipino resolves — it used to be the one unmappable picker code', () => {
    // `CloudSttCatalog.Iso6392ToIso6391["fil"] = "tl"`, so the Mode stores `tl`
    // and forwards it. With no `tl` row every Filipino dictation went out as
    // auto-detect and nothing said so.
    expect(resolve('google-chirp', 'chirp_3', 'tl')).toBe('fil-PH');
    expect(resolve('google-chirp', 'chirp_3', 'fil')).toBe('fil-PH');
  });

  test('forwards a locale the client already region-qualified', () => {
    expect(resolve('google-chirp', 'chirp_3', 'en-GB')).toBe('en-GB');
    expect(resolve('google-chirp', 'chirp_3', 'pt-PT')).toBe('pt-PT');
    expect(resolve('google-chirp', 'chirp_3', 'es-MX')).toBe('es-MX');
  });

  test('honours a qualified code through the picker→Google base alias', () => {
    // GROUP C: `zh-TW` is a real picker code and is not literally in the Chirp
    // locale set (Google spells Mandarin `cmn`). Flattening it to the base
    // table's default handed the user SIMPLIFIED output under a 200 OK.
    expect(resolve('google-chirp', 'chirp_3', 'zh-TW')).toBe('cmn-Hant-TW');
    expect(resolve('google-chirp', 'chirp_3', 'zh-Hant')).toBe('cmn-Hant-TW');
    expect(resolve('google-chirp', 'chirp_3', 'zh-CN')).toBe('cmn-Hans-CN');
    expect(resolve('google-chirp', 'chirp_3', 'he-IL')).toBe('iw-IL');
  });

  test('falls back to the default region only when no script is at stake', () => {
    // Chirp has no Canadian English; en-US differs by accent model at worst.
    expect(resolve('google-chirp', 'chirp_3', 'en-CA')).toBe('en-US');
    // But guessing a SCRIPT is a wrong answer dressed as a right one, so these
    // return null and log rather than silently transcribing in the wrong script.
    expect(resolve('google-chirp', 'chirp_3', 'zh-HK')).toBeNull();
    expect(resolve('google-chirp', 'chirp_3', 'pa-PK')).toBeNull();
  });

  test('repairs casing, including the script subtag', () => {
    expect(resolve('google-chirp', 'chirp_3', 'en-us')).toBe('en-US');
    expect(resolve('google-chirp', 'chirp_3', 'CMN-HANS-CN')).toBe('cmn-Hans-CN');
    expect(resolve('google-chirp', 'chirp_3', 'pa-guru-in')).toBe('pa-Guru-IN');
  });

  test('returns null for an unmappable code so the caller falls back to auto', () => {
    expect(resolve('google-chirp', 'chirp_3', 'zz')).toBeNull();
    expect(resolve('google-chirp', 'chirp_3', 'klingon')).toBeNull();
    expect(resolve('google-chirp', 'chirp_3', '   ')).toBeNull();
  });
});

describe('deepgram', () => {
  test('the medical models are English-only', () => {
    expect(resolve('deepgram', 'nova-3-medical', 'en')).toBe('en');
    expect(resolve('deepgram', 'nova-3-medical', 'en-GB')).toBe('en-gb');
    expect(resolve('deepgram', 'nova-3-medical', 'es')).toBeNull();
    expect(resolve('deepgram', 'nova-2-medical', 'ja')).toBeNull();
  });

  test('nova-2 lacks the languages nova-3 added', () => {
    expect(resolve('deepgram', 'nova-3-general', 'ta')).toBe('ta');
    expect(resolve('deepgram', 'nova-2-general', 'ta')).toBeNull();
    expect(resolve('deepgram', 'nova-2-general', 'de')).toBe('de');
  });

  test('nova-2 keeps the languages the old hand-typed table wrongly took away', () => {
    // GROUP B1: `lt` was listed as nova-3-only, so Lithuanian on nova-2 was
    // silently downgraded to auto-detect even though nova-2 supports it.
    expect(resolve('deepgram', 'nova-2-general', 'lt')).toBe('lt');
  });

  test('nova-2 drops the languages the old table wrongly let through', () => {
    // GROUP B1: `ar` and `hr` were missing from the nova-3-only set, so they
    // were still forwarded to nova-2 — the exact case the table existed for.
    expect(resolve('deepgram', 'nova-2-general', 'ar')).toBeNull();
    expect(resolve('deepgram', 'nova-2-general', 'hr')).toBeNull();
  });

  test('the nova-2-ONLY languages are not forwarded to nova-3', () => {
    // GROUP B1: `th`/`zh` exist on nova-2 and NOT on nova-3. A nova-3-only
    // difference set is structurally blind to that direction.
    expect(resolve('deepgram', 'nova-2-general', 'th')).toBe('th');
    expect(resolve('deepgram', 'nova-2-general', 'zh')).toBe('zh');
    expect(resolve('deepgram', 'nova-3-general', 'th')).toBeNull();
    expect(resolve('deepgram', 'nova-3-general', 'zh')).toBeNull();
  });

  test('keeps a regional variant Deepgram itself lists', () => {
    // Dropping `en-GB` to `en` would quietly move British dictation onto the
    // US spelling model.
    expect(resolve('deepgram', 'nova-3-general', 'en-GB')).toBe('en-gb');
    expect(resolve('deepgram', 'nova-2-general', 'pt-PT')).toBe('pt-pt');
    // A region Deepgram does not list degrades to the base code, not to null.
    expect(resolve('deepgram', 'nova-3-general', 'de-AT')).toBe('de');
  });

  test('the multi sentinel is scoped, not short-circuited', () => {
    expect(resolve('deepgram', 'nova-3-general', 'multi')).toBe('multi');
    // GROUP D: the old `if (code === 'multi') return 'multi'` ran BEFORE the
    // medical clamp, so the sentinel bypassed the English-only guard. The
    // language param is an unvalidated raw query param in transcribe.ts.
    expect(resolve('deepgram', 'nova-3-medical', 'multi')).toBeNull();
    expect(resolve('deepgram', 'nova-2-general', 'multi')).toBeNull();
  });
});

describe('azure-mai', () => {
  test('folds Norwegian onto the code Azure documents', () => {
    // The picker offers `no`; Azure's table lists only `nb`.
    expect(resolve('azure-mai', 'mai-transcribe-1.5', 'no')).toBe('nb');
    expect(resolve('azure-mai', 'mai-transcribe-1.5', 'nn')).toBe('nb');
    expect(resolve('azure-mai', 'mai-transcribe-1.5', 'nb')).toBe('nb');
  });

  test('strips the region', () => {
    expect(resolve('azure-mai', 'mai-transcribe-1.5', 'en-US')).toBe('en');
  });

  test('returns null for a language Azure does not list', () => {
    expect(resolve('azure-mai', 'mai-transcribe-1.5', 'zz')).toBeNull();
    expect(resolve('azure-mai', 'mai-transcribe-1.5', 'cy')).toBeNull();
    expect(resolve('azure-mai', 'mai-transcribe-2', 'zz')).toBeNull();
    expect(resolve('azure-mai', 'mai-transcribe-2', 'cy')).toBeNull();
  });

  test('scopes the locale table to the model that ran', () => {
    // Hebrew is on v2's 60-code table and not on 1.5's 42-code one. Sending it
    // on 1.5 would pin that model to a locale it does not document, so it has
    // to fall to auto-detect instead.
    expect(resolve('azure-mai', 'mai-transcribe-2', 'he')).toBe('he');
    expect(resolve('azure-mai', 'mai-transcribe-1.5', 'he')).toBeNull();
    // Same split for the other v2-only additions.
    expect(resolve('azure-mai', 'mai-transcribe-2', 'af')).toBe('af');
    expect(resolve('azure-mai', 'mai-transcribe-1.5', 'af')).toBeNull();
    expect(resolve('azure-mai', 'mai-transcribe-2', 'yue')).toBe('yue');
    expect(resolve('azure-mai', 'mai-transcribe-1.5', 'yue')).toBeNull();
  });

  test('unfolds the picker code for Filipino onto the code Azure documents', () => {
    // The picker lists Filipino as `tl`; Azure lists `fil` (v2 only).
    expect(resolve('azure-mai', 'mai-transcribe-2', 'tl')).toBe('fil');
    expect(resolve('azure-mai', 'mai-transcribe-1.5', 'tl')).toBeNull();
  });

  test('Norwegian folds the same way on both models', () => {
    for (const model of ['mai-transcribe-1.5', 'mai-transcribe-2']) {
      expect(resolve('azure-mai', model, 'no')).toBe('nb');
      expect(resolve('azure-mai', model, 'nn')).toBe('nb');
      expect(resolve('azure-mai', model, 'nb')).toBe('nb');
    }
  });

  test('an unknown model id falls back to the narrower 1.5 table', () => {
    // Fail-closed: an id we do not carry must not be allowed to send a locale
    // the model behind it may not support.
    expect(resolve('azure-mai', 'mai-transcribe-99', 'en')).toBe('en');
    expect(resolve('azure-mai', 'mai-transcribe-99', 'he')).toBeNull();
  });
});

describe('elevenlabs', () => {
  test('maps the codes where stripping alone lands off the Scribe list', () => {
    expect(resolve('elevenlabs', 'scribe_v2', 'tl')).toBe('fil');
    expect(resolve('elevenlabs', 'scribe_v2', 'zh')).toBe('cmn');
    expect(resolve('elevenlabs', 'scribe_v2', 'jw')).toBe('jav');
  });

  test('passes a listed code through, region stripped', () => {
    expect(resolve('elevenlabs', 'scribe_v2', 'en')).toBe('en');
    expect(resolve('elevenlabs', 'scribe_v2', 'en-US')).toBe('en');
  });

  test('folds the codes that HAVE a Scribe equivalent even off the picker list', () => {
    // GROUP G: `nn` and `zh-TW` are foldable, so a saved Mode on either must
    // keep working rather than being reset.
    expect(resolve('elevenlabs', 'scribe_v2', 'nn')).toBe('nor');
    expect(resolve('elevenlabs', 'scribe_v2', 'zh-TW')).toBe('cmn');
  });

  test('returns null for a language Scribe does not have', () => {
    // GROUP B3: without an allow-list this resolver could never return null, so
    // a saved Mode on Basque forwarded `eu`, Scribe 4xx'd, and transcribe.ts
    // walked the fallback chain off ElevenLabs entirely — silently.
    for (const code of ['eu', 'la', 'yi', 'haw', 'si', 'tt', 'ba', 'br', 'fo', 'ht', 'mg', 'sa', 'su', 'bo', 'tk']) {
      expect(resolve('elevenlabs', 'scribe_v2', code)).toBeNull();
    }
  });
});

describe('openai', () => {
  test('forwards an ISO-639-1 code, region stripped', () => {
    expect(resolve('openai', 'gpt-4o-transcribe', 'en')).toBe('en');
    expect(resolve('openai', 'gpt-4o-transcribe', 'pt-BR')).toBe('pt');
  });

  test('omits the hint for a code the field cannot express', () => {
    // Codex P2: the picker can supply `haw` and `yue`, which are ISO-639-3.
    expect(resolve('openai', 'gpt-4o-transcribe', 'haw')).toBeNull();
    expect(resolve('openai', 'gpt-4o-transcribe', 'yue')).toBeNull();
    expect(resolve('openai', 'gpt-transcribe', 'zz')).toBeNull();
  });

  test('keeps the Whisper-ism the tokenizer does accept', () => {
    expect(resolve('openai', 'gpt-4o-transcribe', 'jw')).toBe('jw');
  });

  test('resolves identically on every model — the field name is not per-model', () => {
    // GROUP F: the plural `languages` field is an unverified doc reading and a
    // 400 there is terminal on a self-only chain. Nothing here may vary by
    // model until scripts/language-code-probe.ts has actually been run.
    for (const model of ['whisper-1', 'gpt-4o-transcribe', 'gpt-4o-mini-transcribe', 'gpt-transcribe', 'gpt-live-transcribe']) {
      expect(resolve('openai', model, 'de')).toBe('de');
    }
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

  test('keeps the REGION, which the prompt used to carry', () => {
    // GROUP E: Gemini is the one tier with no picker narrowing, so `zh-TW`,
    // `pt-PT` and `en-GB` all reach it. Reducing to the primary subtag threw
    // away Traditional-vs-Simplified and pt-PT vs pt-BR — a silent output
    // regression against the old `language code "zh-tw"` interpolation.
    expect(describeLanguage('zh-TW')).toBe('Mandarin Chinese (code "zh-TW")');
    expect(describeLanguage('zh-tw')).toBe('Mandarin Chinese (code "zh-TW")');
    expect(describeLanguage('pt-PT')).toBe('Portuguese (code "pt-PT")');
    expect(describeLanguage('en-GB')).toBe('English (code "en-GB")');
  });

  test('falls back to the bare code rather than inventing a name', () => {
    expect(describeLanguage('zz')).toBe('code "zz"');
  });
});

// Chirp is the one mapped provider with no catalog entry left to check against.
// Catalog v8 replaced `googleChirp3` with `geminiTranscribe` as Google's cloud
// tier; Chirp's upstream-native locale list moved into `GOOGLE_CHIRP_LOCALES` in
// language-codes.ts, which is now its only copy. The provider itself is very much
// alive — clients shipped before v8 still send `X-STT-Provider: google-chirp` into
// a fail-closed registry — so the tables still have to be held to something. What
// remains checkable is (a) the two tables against each other, and (b) the KEY
// space against `chirp_3` in models-catalog.json, which the parity block below
// already does in both directions.
describe('google-chirp locale tables (no longer catalog-backed)', () => {
  test('cloud-stt-catalog.json really has retired the googleChirp3 entry', async () => {
    // A tripwire, not a preference: if someone re-adds the entry, the three
    // assertions this block replaced should come back rather than these weaker
    // self-consistency ones.
    const catalog = (await Bun.file(STT_CATALOG_PATH).json()) as { providers: CatalogProvider[] };
    expect(catalog.providers.map(p => p.id)).not.toContain('googleChirp3');
  });

  test('every Chirp base code maps to a locale the allow-list accepts', () => {
    // `resolveGoogleChirpLocale` gates region/script-qualified codes on
    // GOOGLE_CHIRP_LOCALES, so a default that is not in it is a locale we would
    // emit for a bare code but reject for the qualified form of the same language.
    const allowed = new Set([...__tables.GOOGLE_CHIRP_LOCALES].map(c => c.toLowerCase()));
    const strays = Object.entries(__tables.GOOGLE_CHIRP_LOCALE_BY_BASE)
      .filter(([, locale]) => !allowed.has(locale.toLowerCase()))
      .map(([base, locale]) => `${base} → ${locale}`);
    expect(strays).toEqual([]);
  });

  test('every allow-listed Chirp locale is reachable from a base code', () => {
    // Guards the other direction: a language in the allow-list should not stay
    // unreachable from the picker just because nobody extended the base table.
    const reachable = new Set(Object.values(__tables.GOOGLE_CHIRP_LOCALE_BY_BASE).map(l => primarySubtag(l)));
    const unreachable = [...new Set([...__tables.GOOGLE_CHIRP_LOCALES].map(c => primarySubtag(c)))].filter(
      base => !reachable.has(base),
    );
    expect(unreachable).toEqual([]);
  });
});

describe('catalog parity — VALUE space (cloud-stt-catalog.json)', () => {
  test('the Azure locale set matches the catalog exactly', async () => {
    const codes = await upstreamCodes('azureMaiTranscribe');
    expect([...__tables.AZURE_MAI_LOCALES].sort()).toEqual([...codes].sort());
  });

  test('every Azure alias target is a code some Azure model lists', () => {
    // Checked against the per-model tables, not against the catalog provider
    // row: the aliases exist to make a PICKER code resolve, and what decides
    // that is the model table `resolveAzureMaiLocale` consults. An alias whose
    // target is in no model's table can never produce a locale, which is the
    // bug this guards. (The catalog row is a provider-level union and is
    // asserted separately, above.)
    const listed = new Set(
      Object.values(__tables.AZURE_MAI_MODEL_LOCALES).flatMap(set => [...set]),
    );
    const strays = Object.entries(__tables.AZURE_MAI_ALIASES)
      .filter(([, target]) => !listed.has(target))
      .map(([from, to]) => `${from} → ${to}`);
    expect(strays).toEqual([]);
  });

  test('every ElevenLabs alias target is a code Scribe lists', async () => {
    const codes = new Set(await upstreamCodes('elevenLabsScribeV2'));
    const strays = Object.entries(__tables.ELEVENLABS_ALIASES)
      .filter(([, target]) => !codes.has(target))
      .map(([from, to]) => `${from} → ${to}`);
    expect(strays).toEqual([]);
  });

  test('the Deepgram regional set matches the catalog exactly', async () => {
    const codes = await upstreamCodes('deepgramNova3');
    const regional = codes.filter(c => c.includes('-')).map(c => c.toLowerCase());
    expect([...__tables.DEEPGRAM_REGIONAL_CODES].sort()).toEqual([...new Set(regional)].sort());
  });

  test('the OpenAI allow-list is exactly the ISO-639-1 subset the catalog lists', async () => {
    const codes = await upstreamCodes('openaiWhisper');
    const twoLetter = codes.filter(c => /^[a-z]{2}$/.test(c));
    expect([...__tables.OPENAI_LANGUAGES].sort()).toEqual([...new Set(twoLetter)].sort());
    // And the ones deliberately excluded really are the non-639-1 ones.
    expect(codes.filter(c => !/^[a-z]{2}$/.test(c)).sort()).toEqual(['haw', 'yue']);
  });
});

describe('catalog parity — KEY space (models-catalog.json)', () => {
  // This whole block is the gap the previous suite had: it compared what the
  // tables EMIT to the catalog, and never what they ACCEPT. A picker code with
  // no row silently degrades to auto-detect, which is invisible in a
  // value-space test.

  test('every Chirp picker code has a row in the base table', async () => {
    const codes = await pickerCodes('chirp_3');
    const missing = codes.filter(code => !(code in __tables.GOOGLE_CHIRP_LOCALE_BY_BASE));
    expect(missing).toEqual([]);
  });

  test('every Chirp picker code resolves to a locale', async () => {
    const codes = await pickerCodes('chirp_3');
    const unresolved = codes.filter(code => resolve('google-chirp', 'chirp_3', code) === null);
    expect(unresolved).toEqual([]);
  });

  test('the Deepgram per-model table covers exactly the catalog models', async () => {
    const ids = await modelIdsFor('deepgram');
    expect(Object.keys(__tables.DEEPGRAM_MODEL_LANGUAGES).sort()).toEqual([...ids].sort());
  });

  test('each Deepgram model list matches the catalog exactly', async () => {
    for (const [model, set] of Object.entries(__tables.DEEPGRAM_MODEL_LANGUAGES)) {
      const codes = await pickerCodes(model);
      expect({ model, codes: [...set].sort() }).toEqual({ model, codes: [...codes].sort() });
    }
  });

  test('every Deepgram picker code resolves on its own model', async () => {
    for (const model of Object.keys(__tables.DEEPGRAM_MODEL_LANGUAGES)) {
      const codes = await pickerCodes(model);
      const unresolved = codes.filter(code => resolve('deepgram', model, code) === null);
      expect({ model, unresolved }).toEqual({ model, unresolved: [] });
    }
  });

  test('the ElevenLabs allow-list matches the catalog model exactly', async () => {
    const codes = await pickerCodes('scribe_v2');
    expect([...__tables.ELEVENLABS_LANGUAGES].sort()).toEqual([...codes].sort());
  });

  test('every ElevenLabs picker code resolves', async () => {
    const codes = await pickerCodes('scribe_v2');
    const unresolved = codes.filter(code => resolve('elevenlabs', 'scribe_v2', code) === null);
    expect(unresolved).toEqual([]);
  });

  test('the Azure per-model table covers exactly the catalog models', async () => {
    const ids = await modelIdsFor('microsoftAzureSpeech');
    expect(Object.keys(__tables.AZURE_MAI_MODEL_LOCALES).sort()).toEqual([...ids].sort());
  });

  test('each Azure model list folds onto the catalog picker list exactly', async () => {
    // The Azure tables are UPSTREAM codes; models-catalog holds PICKER codes.
    // The fold between them is the rule documented in
    // hw-catalog/src/cloud_stt/lang.rs and in each model's catalog `notes`:
    // `nb` → `no`, `fil` → `tl`, `yue` kept, and `or` (Odia) dropped because
    // the shared language catalog has no row for it.
    const UPSTREAM_TO_PICKER: Record<string, string | null> = {
      nb: 'no',
      fil: 'tl',
      or: null,
    };
    for (const [model, set] of Object.entries(__tables.AZURE_MAI_MODEL_LOCALES)) {
      const folded = [...set]
        .map(code => (code in UPSTREAM_TO_PICKER ? UPSTREAM_TO_PICKER[code] : code))
        .filter((code): code is string => code !== null)
        .sort();
      const codes = await pickerCodes(model);
      expect({ model, codes: folded }).toEqual({ model, codes: [...codes].sort() });
    }
  });

  test('every Azure picker code resolves on its own model', async () => {
    for (const model of Object.keys(__tables.AZURE_MAI_MODEL_LOCALES)) {
      const codes = await pickerCodes(model);
      const unresolved = codes.filter(code => resolve('azure-mai', model, code) === null);
      expect({ model, unresolved }).toEqual({ model, unresolved: [] });
    }
  });

  test('every OpenAI picker code either resolves or is a known non-639-1 code', async () => {
    const codes = await upstreamCodes('openaiWhisper');
    const dropped = codes.filter(code => resolve('openai', 'gpt-4o-transcribe', code) === null);
    expect(dropped.sort()).toEqual(['haw', 'yue']);
  });

  test('every picker code any provider can send has a Gemini display name', async () => {
    // Gemini has no picker narrowing at all, so it receives the union of every
    // other tier's codes and turns them into prose.
    const all = new Set<string>();
    for (const model of ['chirp_3', 'scribe_v2', 'mai-transcribe-1.5', 'mai-transcribe-2', 'nova-3-general', 'nova-2-general']) {
      for (const code of await pickerCodes(model)) all.add(primarySubtag(code));
    }
    for (const code of await upstreamCodes('openaiWhisper')) all.add(primarySubtag(code));
    const unnamed = [...all].filter(base => !__tables.LANGUAGE_NAMES[base]);
    expect(unnamed).toEqual([]);
  });
});
