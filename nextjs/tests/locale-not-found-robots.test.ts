/**
 * The 404 pages (#1133, #815).
 *
 * `app/[locale]/not-found.tsx`: a notFound() under [locale] resolves the
 * [locale] layout's metadata, whose robots is "index, follow", next to Next's
 * injected noindex. This boundary's robots must replace it with a noindex. It
 * must also carry a real title and links back into the site, in every locale,
 * so a 404 is not a dead end (#815).
 *
 * `app/[locale]/[...rest]/page.tsx` routes an unmatched /<locale>/<path> to
 * that boundary by throwing notFound().
 *
 * `app/not-found.tsx` is the last-resort shell and must still link home.
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
    options: { namedExports: Record<string, unknown> },
  ): void;
}

const moduleMock = mock as unknown as ModuleMocker;

const MESSAGES_DIR = path.join(__dirname, "..", "messages");
const en = JSON.parse(
  readFileSync(path.join(MESSAGES_DIR, "en.json"), "utf8"),
) as { notFound: Record<string, string> };

moduleMock.module("next-intl/server", {
  namedExports: {
    getTranslations: async (namespace: string) => {
      assert.equal(namespace, "notFound");

      return (key: string) => en.notFound[key];
    },
  },
});

// The real next-intl Link needs the request context. A plain anchor keeps the
// href, which is what is asserted.
moduleMock.module("@/src/i18n/navigation", {
  namedExports: {
    Link: ({ href, children }: { href: string; children: React.ReactNode }) =>
      createElement("a", { href }, children),
  },
});

// Variable specifiers, imported after the mocks (see root-layout-direction).
const LOCALE_NOT_FOUND_PATH = "../app/[locale]/not-found.tsx";
const CATCH_ALL_PATH = "../app/[locale]/[...rest]/page.tsx";
const ROOT_NOT_FOUND_PATH = "../app/not-found.tsx";

type LocaleNotFoundModule = {
  default: () => Promise<React.ReactElement>;
  generateMetadata: () => Promise<{ title: string; robots: unknown }>;
};

test("[locale] not-found overrides the layout robots with noindex and sets a title", async () => {
  const { generateMetadata } = (await import(
    LOCALE_NOT_FOUND_PATH
  )) as LocaleNotFoundModule;
  const metadata = await generateMetadata();

  assert.deepEqual(metadata.robots, { index: false });
  assert.equal(metadata.title, en.notFound.title);
});

test("[locale] not-found links to the home page and the download page", async () => {
  const { default: LocaleNotFound } = (await import(
    LOCALE_NOT_FOUND_PATH
  )) as LocaleNotFoundModule;
  const markup = renderToStaticMarkup(await LocaleNotFound());

  assert.match(markup, /<h1[^>]*>This page does not exist<\/h1>/);
  assert.match(markup, /<a href="\/">/);
  assert.match(markup, /<a href="\/download">/);
});

test("an unmatched /<locale>/<path> throws notFound()", async () => {
  const { default: CatchAll } = (await import(CATCH_ALL_PATH)) as {
    default: () => never;
  };

  assert.throws(
    () => CatchAll(),
    (error: unknown) =>
      (error as { digest?: string }).digest === "NEXT_HTTP_ERROR_FALLBACK;404",
  );
});

test("the root not-found shell links home and is noindex", async () => {
  const { default: RootNotFound, metadata } = (await import(
    ROOT_NOT_FOUND_PATH
  )) as {
    default: () => React.ReactElement;
    metadata: { title: string; robots: unknown };
  };
  const markup = renderToStaticMarkup(RootNotFound());

  assert.match(markup, /<a [^>]*href="\/"/);
  assert.match(markup, /background:#000/);
  assert.ok(metadata.title);
  assert.deepEqual(metadata.robots, { index: false });
});

test("every locale catalogue has the notFound strings", () => {
  const keys = Object.keys(en.notFound).sort();
  const files = readdirSync(MESSAGES_DIR).filter((f) => f.endsWith(".json"));

  assert.equal(files.length, 40);
  for (const file of files) {
    const messages = JSON.parse(
      readFileSync(path.join(MESSAGES_DIR, file), "utf8"),
    ) as { notFound?: Record<string, string> };

    assert.ok(messages.notFound, `${file} has no notFound`);
    assert.deepEqual(Object.keys(messages.notFound).sort(), keys, file);
    for (const key of keys) {
      assert.ok(messages.notFound[key]?.trim(), `${file} notFound.${key}`);
    }
  }
});
