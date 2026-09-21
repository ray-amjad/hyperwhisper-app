import assert from "node:assert/strict";
import test from "node:test";

// Imported through a variable specifier inside each test, the way the other
// tests in this folder do: a static `.ts` import path is a type error under this
// tsconfig, but `node --test --experimental-strip-types` needs the extension.
const MODULE_PATH = "../src/i18n/locales.ts";
const load = () => import(MODULE_PATH);

test("the two right-to-left locales report rtl", async () => {
  const { localeDirection } = await load();
  assert.equal(localeDirection("ar"), "rtl");
  assert.equal(localeDirection("he"), "rtl");
});

test("left-to-right locales report ltr, hyphenated tags included", async () => {
  const { localeDirection } = await load();
  for (const locale of ["en", "de", "ja", "zh", "zh-Hant", "hi", "ru", "th"]) {
    assert.equal(localeDirection(locale), "ltr", `expected ltr for ${locale}`);
  }
});

test("every shipped locale is classified, and exactly ar and he are rtl", async () => {
  const { locales, localeDirection } = await load();
  const rtl: string[] = [];

  for (const locale of locales) {
    const direction = localeDirection(locale);
    assert.ok(
      direction === "rtl" || direction === "ltr",
      `unexpected direction ${direction} for ${locale}`,
    );
    if (direction === "rtl") rtl.push(locale);
  }

  assert.deepEqual(rtl.sort(), ["ar", "he"]);
});

test("unrecognised input falls back to ltr", async () => {
  const { localeDirection } = await load();
  // The root layout reads a header and falls back to "en", so the helper must
  // be total over plain strings rather than over the Locale union.
  assert.equal(localeDirection(""), "ltr");
  assert.equal(localeDirection("xx"), "ltr");
  assert.equal(localeDirection("AR"), "ltr");
  assert.equal(localeDirection("ar-EG"), "ltr");
});
