/**
 * The 404 pages (#1133, #815).
 *
 * `app/[locale]/not-found.tsx`: a notFound() under [locale] resolves the
 * [locale] layout's metadata, whose robots is "index, follow", next to Next's
 * injected noindex. This boundary's robots must clear it, so Next's noindex is
 * the only robots tag (a noindex here would be a second one). It must also carry a real title, in the URL's locale, and links back into the
 * site, so a 404 is not a dead end (#815).
 *
 * `app/not-found.tsx` serves every unmatched URL (/de/zzz). It must wrap that
 * same content in the [locale] layout, so the page gets the site chrome, and
 * set its own noindex on Vercel only: next start injects one there, Vercel
 * does not.
 */
import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import path from "node:path";
import test, { mock } from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";

// See tests/root-layout-direction.test.ts: @types/node@20 has no declaration
// for mock.module, so it is narrowed to the one method used here.
interface ModuleMocker {
  module(
    specifier: string,
    options: {
      defaultExport?: unknown;
      namedExports?: Record<string, unknown>;
    },
  ): void;
}

const moduleMock = mock as unknown as ModuleMocker;

const MESSAGES_DIR = path.join(__dirname, "..", "messages");

function readCatalogue(file: string): { notFound?: Record<string, string> } {
  return JSON.parse(readFileSync(path.join(MESSAGES_DIR, file), "utf8"));
}

const en = readCatalogue("en.json").notFound!;
const de = readCatalogue("de.json").notFound!;
const ja = readCatalogue("ja.json").notFound!;

/** What the request locale (the proxy header) resolves to. Each case sets it. */
const request = { locale: "en" };

moduleMock.module("next-intl/server", {
  namedExports: {
    getLocale: async () => request.locale,
    getTranslations: async (
      arg: string | { locale: string; namespace: string },
    ) => {
      const { locale, namespace } =
        typeof arg === "string"
          ? { locale: request.locale, namespace: arg }
          : arg;

      assert.equal(namespace, "notFound");
      const catalogue = locale === "de" ? de : en;

      return (key: string) => catalogue[key];
    },
  },
});

/** The browser's path, which the 404 content reads its locale from. */
const browser: { pathname: string | null } = { pathname: null };

moduleMock.module("next/navigation", {
  namedExports: { usePathname: () => browser.pathname },
});

// The real next/link needs the router context. A plain anchor keeps the href,
// which is what is asserted.
moduleMock.module("next/link", {
  defaultExport: ({
    href,
    children,
  }: {
    href: string;
    children: React.ReactNode;
  }) => createElement("a", { href }, children),
});

// The real [locale] layout imports CSS and next/font, which node cannot load.
// The stand-in is a marker the root not-found test looks for.
function FakeLocaleLayout() {
  return null;
}

moduleMock.module("../app/[locale]/layout.tsx", {
  defaultExport: FakeLocaleLayout,
});

// Variable specifiers, imported after the mocks (see root-layout-direction).
const LOCALE_NOT_FOUND_PATH = "../app/[locale]/not-found.tsx";
const ROOT_NOT_FOUND_PATH = "../app/not-found.tsx";

type Metadata = { title: string; robots?: unknown };

type LocaleNotFoundModule = {
  default: () => Promise<React.ReactElement>;
  generateMetadata: (props?: {
    params?: Promise<{ locale?: string }>;
  }) => Promise<Metadata>;
};

type RootNotFoundModule = {
  default: () => Promise<
    React.ReactElement<{
      params: Promise<{ locale: string }>;
      children: React.ReactElement;
    }>
  >;
  generateMetadata: () => Promise<Metadata>;
};

test("[locale] not-found clears the layout robots and sets a title", async () => {
  request.locale = "en";
  const { generateMetadata } = (await import(
    LOCALE_NOT_FOUND_PATH
  )) as LocaleNotFoundModule;
  const metadata = await generateMetadata();

  // null, not absent: absent would inherit the layout's "index, follow".
  assert.ok("robots" in metadata);
  assert.equal(metadata.robots, null);
  assert.equal(metadata.title, en.title);
});

test("[locale] not-found titles a thrown 404 in the segment's locale", async () => {
  // A notFound() under [locale] resolves metadata before the request locale is
  // set, so the params are the only reliable locale there.
  request.locale = "en";
  const { generateMetadata } = (await import(
    LOCALE_NOT_FOUND_PATH
  )) as LocaleNotFoundModule;

  const metadata = await generateMetadata({
    params: Promise.resolve({ locale: "de" }),
  });

  assert.equal(metadata.title, de.title);
});

test("[locale] not-found links to the home page and the download page", async () => {
  request.locale = "en";
  browser.pathname = "/en/zzz";
  const { default: LocaleNotFound } = (await import(
    LOCALE_NOT_FOUND_PATH
  )) as LocaleNotFoundModule;
  const markup = renderToStaticMarkup(await LocaleNotFound());

  assert.match(markup, new RegExp(`<h1[^>]*>${en.heading}</h1>`));
  assert.match(markup, /<a href="\/en">/);
  assert.match(markup, /<a href="\/en\/download">/);
});

test("[locale] not-found speaks the URL's locale when the request locale is the default", async () => {
  // A notFound() thrown on a force-static page (/ja/blog/<missing>) renders
  // with no proxy locale header, so next-intl resolves "en". The copy and the
  // links must still follow the URL (#815).
  request.locale = "en";
  browser.pathname = "/ja/blog/missing-post";
  const { default: LocaleNotFound } = (await import(
    LOCALE_NOT_FOUND_PATH
  )) as LocaleNotFoundModule;
  const markup = renderToStaticMarkup(await LocaleNotFound());

  assert.match(markup, new RegExp(`<h1[^>]*>${ja.heading}</h1>`));
  assert.match(markup, new RegExp(`<a href="/ja">${ja.home}</a>`));
  assert.match(markup, new RegExp(`<a href="/ja/download">${ja.download}</a>`));
});

test("[locale] not-found falls back to the request locale off a locale path", async () => {
  request.locale = "de";
  browser.pathname = null;
  const { default: LocaleNotFound } = (await import(
    LOCALE_NOT_FOUND_PATH
  )) as LocaleNotFoundModule;
  const markup = renderToStaticMarkup(await LocaleNotFound());

  assert.match(markup, new RegExp(`<h1[^>]*>${de.heading}</h1>`));
  assert.match(markup, /<a href="\/de">/);
});

test("the English-only pages throw their 404 from generateMetadata too", () => {
  // Thrown only from the page, the browser kept the page's (or the layout's
  // home-page) title and "index, follow" on /ja/blog/<missing> (#815).
  for (const page of [
    "blog/page.tsx",
    "blog/[slug]/page.tsx",
    "latency/page.tsx",
    "choosing-a-model/page.tsx",
  ]) {
    const source = readFileSync(
      path.join(__dirname, "..", "app", "[locale]", page),
      "utf8",
    );
    const metadata = source.slice(
      source.indexOf("export async function generateMetadata"),
      source.indexOf("\n}\n", source.indexOf("export async function generateMetadata")),
    );

    assert.match(metadata, /if \(locale !== "en"\)[\s{]*notFound\(\)/, page);
  }
});

test("the root not-found wraps the [locale] 404 in the [locale] layout", async () => {
  request.locale = "de";
  const [{ default: RootNotFound, generateMetadata }, localeModule] =
    await Promise.all([
      import(ROOT_NOT_FOUND_PATH) as Promise<RootNotFoundModule>,
      import(LOCALE_NOT_FOUND_PATH) as Promise<LocaleNotFoundModule>,
    ]);
  const element = await RootNotFound();

  assert.equal(element.type, FakeLocaleLayout);
  assert.deepEqual(await element.props.params, { locale: "de" });
  assert.equal(element.props.children.type, localeModule.default);
  assert.equal((await generateMetadata()).title, `${de.title} - HyperWhisper`);
});

test("the root not-found marks an unmatched URL noindex on Vercel only", async () => {
  // Vercel served no noindex on an unmatched URL (/de/zzz); next start injects
  // one itself, so a second there would be a duplicate tag.
  request.locale = "de";
  const { generateMetadata } = (await import(
    ROOT_NOT_FOUND_PATH
  )) as RootNotFoundModule;
  const vercel = process.env.VERCEL;

  try {
    process.env.VERCEL = "1";
    assert.deepEqual((await generateMetadata()).robots, { index: false });

    delete process.env.VERCEL;
    assert.equal((await generateMetadata()).robots, undefined);
  } finally {
    if (vercel === undefined) delete process.env.VERCEL;
    else process.env.VERCEL = vercel;
  }
});

test("every locale catalogue has the notFound strings", () => {
  const keys = Object.keys(en).sort();
  const files = readdirSync(MESSAGES_DIR).filter((f) => f.endsWith(".json"));

  assert.equal(files.length, 40);
  for (const file of files) {
    const { notFound } = readCatalogue(file);

    assert.ok(notFound, `${file} has no notFound`);
    assert.deepEqual(Object.keys(notFound).sort(), keys, file);
    for (const key of keys) {
      assert.ok(notFound[key]?.trim(), `${file} notFound.${key}`);
    }
  }
});
