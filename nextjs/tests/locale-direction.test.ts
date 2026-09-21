import assert from "node:assert/strict";
import test from "node:test";

// A static, extensionless import: this test mocks nothing, so it does not need
// the variable-specifier indirection the mock.module tests in this folder use.
// Extensionless is also the only form that passes both `tsc --noEmit` and the
// runner. Being static is the point — a rename or a signature change in
// locales.ts is now a typecheck failure here, not a runtime surprise.
import {
  applyHtmlLocaleAttributes,
  localeDirection,
  localeDirections,
  locales,
} from "../src/i18n/locales";

test("the two right-to-left locales report rtl", () => {
  assert.equal(localeDirection("ar"), "rtl");
  assert.equal(localeDirection("he"), "rtl");
});

test("left-to-right locales report ltr, hyphenated tags included", () => {
  for (const locale of ["en", "de", "ja", "zh", "zh-Hant", "hi", "ru", "th"]) {
    assert.equal(localeDirection(locale), "ltr", `expected ltr for ${locale}`);
  }
});

test("every shipped locale is classified, and the helper reads the table", () => {
  // Cross-checks the helper against the declaration table rather than against a
  // snapshot of today's answer. `localeDirections` is Record<Locale, …>, so the
  // compiler already refuses a locale added to `locales` with no direction
  // stated; this proves the lookup is total at runtime too, so adding a locale
  // can never silently resolve to undefined.
  for (const locale of locales) {
    const declared = localeDirections[locale];

    assert.ok(
      declared === "rtl" || declared === "ltr",
      `${locale} has no direction declared`,
    );
    assert.equal(
      localeDirection(locale),
      declared,
      `localeDirection disagrees with localeDirections for ${locale}`,
    );
  }

  assert.equal(
    Object.keys(localeDirections).length,
    locales.length,
    "localeDirections and locales hold a different number of entries",
  );
});

test("unrecognised input falls back to ltr", () => {
  // The root layout reads a header and falls back to "en", so the helper must
  // be total over plain strings rather than over the Locale union.
  assert.equal(localeDirection(""), "ltr");
  assert.equal(localeDirection("xx"), "ltr");
  assert.equal(localeDirection("AR"), "ltr");
  assert.equal(localeDirection("ar-EG"), "ltr");
});

test("applyHtmlLocaleAttributes writes lang and dir together", () => {
  const element = { lang: "en", dir: "ltr" };

  applyHtmlLocaleAttributes(element, "ar");
  assert.deepEqual(element, { lang: "ar", dir: "rtl" });

  // Switching back must clear dir as well, not leave rtl behind.
  applyHtmlLocaleAttributes(element, "en");
  assert.deepEqual(element, { lang: "en", dir: "ltr" });

  applyHtmlLocaleAttributes(element, "xx");
  assert.deepEqual(element, { lang: "xx", dir: "ltr" });
});
