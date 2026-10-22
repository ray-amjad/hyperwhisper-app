/**
 * The document shell seam: `HtmlDocument` in `components/layout/HtmlDocument.tsx`
 * (#814), which `app/[locale]/layout.tsx` renders with its `params.locale`.
 *
 * `tests/locale-direction.test.ts` covers the pure helpers in
 * `src/i18n/locales.ts`. It does NOT cover the thing issue #814 actually asks
 * for — that the `<html>` SERVES a `dir` attribute. Review round 2 proved the
 * gap by mutation: deleting ` dir={localeDirection(locale)}` killed zero tests.
 * This file closes that half.
 *
 * The shell used to live in `app/layout.tsx` and read the locale from
 * `headers()`, which made every route dynamic (#843). It now takes the locale
 * as a prop, so the whole seam is reachable with no DOM, no mocks and no test
 * framework: render the element to a string.
 *
 * What is asserted is the MARKUP, not the props of the returned element. A
 * `dir` prop React refused to serialise would still satisfy a prop assertion,
 * and the defect in #814 is about the bytes on the wire.
 */
import assert from "node:assert/strict";
import test from "node:test";
import { renderToStaticMarkup } from "react-dom/server";

// A VARIABLE specifier: a literal `.tsx` specifier is a TS5097 error under this
// tsconfig. Same rule the other tests in this folder document.
const DOCUMENT_PATH = "../components/layout/HtmlDocument.tsx";

type HtmlDocument = (props: {
  locale: string;
  children: React.ReactNode;
}) => React.ReactElement;

/** Renders the real `HtmlDocument` for the locale the [locale] layout passes. */
async function renderDocument(locale: string): Promise<string> {
  const { default: HtmlDocument } = (await import(DOCUMENT_PATH)) as {
    default: HtmlDocument;
  };

  return renderToStaticMarkup(HtmlDocument({ locale, children: null }));
}

/** The opening `<html ...>` tag of a rendered document. */
function htmlTag(markup: string): string {
  const match = markup.match(/<html[^>]*>/);

  assert.ok(match, "the layout rendered no <html> element");

  return match[0];
}

test("the ar locale is served right-to-left", async () => {
  const tag = htmlTag(await renderDocument("ar"));

  assert.match(tag, /dir="rtl"/, `expected dir="rtl" in ${tag}`);
  assert.match(tag, /lang="ar"/, `expected lang="ar" in ${tag}`);
});

test("the he locale is served right-to-left", async () => {
  const tag = htmlTag(await renderDocument("he"));

  assert.match(tag, /dir="rtl"/, `expected dir="rtl" in ${tag}`);
  assert.match(tag, /lang="he"/, `expected lang="he" in ${tag}`);
});

test("a left-to-right locale is served with an explicit dir", async () => {
  // Explicit, not omitted. `dir` is inherited by everything below <html>, and
  // app/not-found.tsx relies on the shell stating its direction rather than
  // leaving it to the user agent default.
  for (const locale of ["en", "de", "ja", "zh-Hant"]) {
    const tag = htmlTag(await renderDocument(locale));

    assert.match(tag, /dir="ltr"/, `expected dir="ltr" in ${tag}`);
    assert.match(
      tag,
      new RegExp(`lang="${locale}"`),
      `expected lang="${locale}" in ${tag}`,
    );
  }
});

test("an unrecognised locale is served left-to-right, not undefined", async () => {
  // The [locale] layout 404s an unknown locale before it renders, but the
  // failure this guards is a `dir="undefined"` attribute reaching the browser,
  // which is why the assertion is on the rendered bytes.
  const tag = htmlTag(await renderDocument("xx-not-a-locale"));

  assert.match(tag, /dir="ltr"/, `expected dir="ltr" in ${tag}`);
  assert.doesNotMatch(tag, /dir="undefined"/, `dir leaked undefined in ${tag}`);
});

test("every locale is served with the dark class on <html> (#1151)", async () => {
  // The site is dark-only, and with JavaScript off nothing on the client adds
  // `dark` after load. The class has to be in the served bytes, so the
  // assertion is on the rendered tag, for an RTL and an LTR locale alike.
  for (const locale of ["ar", "en"]) {
    const tag = htmlTag(await renderDocument(locale));
    const classAttr = tag.match(/\sclass="([^"]*)"/);

    assert.ok(classAttr, `expected a class attribute in ${tag}`);
    assert.ok(
      classAttr[1].split(/\s+/).includes("dark"),
      `expected the dark class in ${tag}`,
    );
  }
});
