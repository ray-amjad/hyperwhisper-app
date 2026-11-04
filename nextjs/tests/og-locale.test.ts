/**
 * #2004: og:locale was the language code glued to itself in upper case
 * (`ko` -> `ko_KO`) for every locale without an override, which names a wrong
 * or non-existent country for 14 of the 40 locales. `toOpenGraphLocale` now
 * reads an exhaustive map; this pins the 14 corrections, the deliberate
 * overrides, and the rule that `xx_XX` appears only where it is the real code.
 */
import assert from "node:assert/strict";
import test from "node:test";

import { locales, toOpenGraphLocale } from "../src/i18n/locales";

const corrected: Record<string, string> = {
  ca: "ca_ES",
  cs: "cs_CZ",
  da: "da_DK",
  el: "el_GR",
  et: "et_EE",
  he: "he_IL",
  hi: "hi_IN",
  ko: "ko_KR",
  ms: "ms_MY",
  sl: "sl_SI",
  sr: "sr_RS",
  sv: "sv_SE",
  uk: "uk_UA",
  vi: "vi_VN",
};

// Locales whose language code doubled really is their Open Graph locale. The
// issue's list plus `es` and `pt`: es_ES and pt_PT are deliberate overrides
// the issue says to keep.
const doubledAllowed = new Set([
  "es",
  "pt",
  "de",
  "fr",
  "it",
  "nl",
  "fi",
  "pl",
  "tr",
  "ro",
  "hu",
  "sk",
  "bg",
  "hr",
  "lt",
  "lv",
  "is",
  "ru",
  "th",
  "id",
  "ar",
]);

test("the 14 locales #2004 found wrong send their real country", () => {
  for (const [locale, expected] of Object.entries(corrected)) {
    assert.equal(toOpenGraphLocale(locale), expected, locale);
  }
});

test("the deliberate overrides are unchanged", () => {
  assert.equal(toOpenGraphLocale("en"), "en_US");
  assert.equal(toOpenGraphLocale("ja"), "ja_JP");
  assert.equal(toOpenGraphLocale("es"), "es_ES");
  assert.equal(toOpenGraphLocale("zh"), "zh_CN");
  assert.equal(toOpenGraphLocale("zh-Hant"), "zh_TW");
  assert.equal(toOpenGraphLocale("pt"), "pt_PT");
  assert.equal(toOpenGraphLocale("nb"), "nb_NO");
});

test("every locale is language_COUNTRY, and doubled only where allowed", () => {
  for (const locale of locales) {
    const value = toOpenGraphLocale(locale);
    assert.match(value, /^[a-z]{2,3}_[A-Z]{2}$/, locale);
    if (!doubledAllowed.has(locale)) {
      assert.notEqual(value, `${locale}_${locale.toUpperCase()}`, locale);
    } else {
      assert.equal(value, `${locale}_${locale.toUpperCase()}`, locale);
    }
  }
});

test("an unsupported value still falls back to en_US", () => {
  assert.equal(toOpenGraphLocale("xx"), "en_US");
  assert.equal(toOpenGraphLocale(""), "en_US");
  assert.equal(toOpenGraphLocale("toString"), "en_US");
});
