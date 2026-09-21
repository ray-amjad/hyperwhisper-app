/**
 * The root layout seam: `RootLayout` in `app/layout.tsx` (#814).
 *
 * `tests/locale-direction.test.ts` covers the pure helpers in
 * `src/i18n/locales.ts`. It does NOT cover the thing issue #814 actually asks
 * for — that the root `<html>` SERVES a `dir` attribute. Review round 2 proved
 * the gap by mutation: deleting ` dir={localeDirection(locale)}` from
 * `app/layout.tsx` killed zero tests. This file closes that half.
 *
 * `RootLayout` is an async server component whose only input is `next/headers`,
 * so the whole seam is reachable with no DOM and no test framework: stub the one
 * header read, await the element, and render it to a string.
 *
 * What is asserted is the MARKUP, not the props of the returned element. A
 * `dir` prop React refused to serialise would still satisfy a prop assertion,
 * and the defect in #814 is about the bytes on the wire.
 */
import assert from "node:assert/strict";
import test, { mock } from "node:test";
import { renderToStaticMarkup } from "react-dom/server";

/**
 * `mock.module` is a real Node 22 API (behind `--experimental-test-module-mocks`,
 * which `npm test` passes) but this repo pins `@types/node@20`, which has no
 * declaration for it. Narrowed to the one method used here rather than bumping
 * the types in a test-only change — the same shape
 * `tests/magic-link-send.test.ts` uses.
 */
interface ModuleMocker {
  module(
    specifier: string,
    options: { namedExports: Record<string, unknown> },
  ): void;
}

const moduleMock = mock as unknown as ModuleMocker;

/**
 * What the stubbed `x-next-intl-locale` header answers. Each case sets it.
 *
 * `null` is the real absent-header answer from Next's `ReadonlyHeaders.get`,
 * which is what drives the `?? "en"` fallback in the layout.
 */
const header = { locale: null as string | null };

/**
 * The proxy sets `x-next-intl-locale`, and `app/layout.tsx` is the only reader.
 * Mocking `next/headers` is therefore the entire input surface of this
 * component — nothing else about the request reaches it.
 */
moduleMock.module("next/headers", {
  namedExports: {
    headers: async () => ({
      get: (name: string) =>
        name === "x-next-intl-locale" ? header.locale : null,
    }),
  },
});

// A VARIABLE specifier, and deferred: a static import would bind before the
// `mock.module` call above, and a literal `.tsx` specifier is a TS5097 error
// under this tsconfig. Same rule the mock.module tests in this folder document.
const LAYOUT_PATH = "../app/layout.tsx";

type RootLayout = (props: {
  children: React.ReactNode;
}) => Promise<React.ReactElement>;

/** Renders the real `RootLayout` as the locale header it would be served with. */
async function renderRootLayout(locale: string | null): Promise<string> {
  header.locale = locale;

  const { default: RootLayout } = (await import(LAYOUT_PATH)) as {
    default: RootLayout;
  };

  return renderToStaticMarkup(await RootLayout({ children: null }));
}

/** The opening `<html ...>` tag of a rendered document. */
function htmlTag(markup: string): string {
  const match = markup.match(/<html[^>]*>/);

  assert.ok(match, "the layout rendered no <html> element");

  return match[0];
}

test("the ar locale is served right-to-left", async () => {
  const tag = htmlTag(await renderRootLayout("ar"));

  assert.match(tag, /dir="rtl"/, `expected dir="rtl" in ${tag}`);
  assert.match(tag, /lang="ar"/, `expected lang="ar" in ${tag}`);
});

test("the he locale is served right-to-left", async () => {
  const tag = htmlTag(await renderRootLayout("he"));

  assert.match(tag, /dir="rtl"/, `expected dir="rtl" in ${tag}`);
  assert.match(tag, /lang="he"/, `expected lang="he" in ${tag}`);
});

test("a left-to-right locale is served with an explicit dir", async () => {
  // Explicit, not omitted. `dir` is inherited by everything below <html>, and
  // app/not-found.tsx now relies on the root stating its direction rather than
  // leaving it to the user agent default.
  for (const locale of ["en", "de", "ja", "zh-Hant"]) {
    const tag = htmlTag(await renderRootLayout(locale));

    assert.match(tag, /dir="ltr"/, `expected dir="ltr" in ${tag}`);
    assert.match(
      tag,
      new RegExp(`lang="${locale}"`),
      `expected lang="${locale}" in ${tag}`,
    );
  }
});

test("an absent locale header falls back to a left-to-right en document", async () => {
  // `headers().get()` answers null when the proxy did not run — every path the
  // matcher in proxy.ts excludes. The layout must still serve a valid document.
  const tag = htmlTag(await renderRootLayout(null));

  assert.match(tag, /lang="en"/, `expected lang="en" in ${tag}`);
  assert.match(tag, /dir="ltr"/, `expected dir="ltr" in ${tag}`);
});

test("an unrecognised locale header is served left-to-right, not undefined", async () => {
  // The header is untrusted at this call site. The failure this guards is a
  // `dir="undefined"` attribute reaching the browser, which is why the assertion
  // is on the rendered bytes rather than on the value of a prop.
  const tag = htmlTag(await renderRootLayout("xx-not-a-locale"));

  assert.match(tag, /dir="ltr"/, `expected dir="ltr" in ${tag}`);
  assert.doesNotMatch(tag, /dir="undefined"/, `dir leaked undefined in ${tag}`);
});
