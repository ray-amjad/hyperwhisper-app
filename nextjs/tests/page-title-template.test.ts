/**
 * #707: the [locale] layout brands every page title with `%s - HyperWhisper`.
 * Four pages also wrote "| HyperWhisper" into their own title, so the tab read
 * "Privacy Policy | HyperWhisper - HyperWhisper", and /credits set no title at
 * all, so it showed the home page's title and description.
 *
 * Calls the REAL `generateMetadata` of the five pages with every locale's real
 * catalogue, then applies the layout's template the way Next does (a string
 * title gets it, an `{ absolute }` title skips it).
 */
import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";
import test, { mock } from "node:test";
import { fileURLToPath } from "node:url";

// `mock.module` is real in Node 22 (behind `--experimental-test-module-mocks`,
// which `npm test` passes), but the pinned @types/node@20 does not declare it.
interface ModuleMocker {
  module(
    specifier: string,
    options: {
      namedExports?: Record<string, unknown>;
      defaultExport?: unknown;
    },
  ): void;
}

const moduleMock = mock as unknown as ModuleMocker;

const ROOT = fileURLToPath(new URL("..", import.meta.url));

type Catalogue = Record<string, Record<string, unknown>>;

const LOCALES = readdirSync(join(ROOT, "messages"))
  .filter((f) => f.endsWith(".json"))
  .map((f) => f.replace(/\.json$/, ""));

const catalogues = new Map<string, Catalogue>(
  LOCALES.map((locale) => [
    locale,
    JSON.parse(
      readFileSync(join(ROOT, `messages/${locale}.json`), "utf8"),
    ) as Catalogue,
  ]),
);

moduleMock.module("next-intl/server", {
  namedExports: {
    setRequestLocale: () => {},
    getTranslations: async ({
      locale,
      namespace,
    }: {
      locale: string;
      namespace: string;
    }) => {
      const ns = catalogues.get(locale)?.[namespace] ?? {};

      return (key: string) => {
        const value = ns[key];

        assert.equal(typeof value, "string", `${locale} ${namespace}.${key}`);

        return value as string;
      };
    },
  },
});

// The purchase form is a client component with its own imports; the metadata
// never renders it.
moduleMock.module(
  new URL("../components/credits/CreditsPurchase.tsx", import.meta.url).href,
  { defaultExport: () => null },
);

// The layout's template, as `app/[locale]/layout.tsx` sets it.
const TEMPLATE = "%s - HyperWhisper";

type Title = string | { absolute: string } | undefined;

function resolveTitle(title: Title): string | undefined {
  if (title === undefined) return undefined;
  if (typeof title === "string") return TEMPLATE.replace("%s", title);

  return title.absolute;
}

type PageModule = {
  generateMetadata: (props: {
    params: Promise<{ locale: string }>;
  }) => Promise<{ title?: Title; description?: string }>;
};

const PAGES = [
  { route: "open-source", en: "Open Source | HyperWhisper" },
  { route: "legal/privacy-policy", en: "Privacy Policy - HyperWhisper" },
  { route: "legal/terms-of-service", en: "Terms of Service - HyperWhisper" },
  { route: "legal/refund-policy", en: "Refund Policy - HyperWhisper" },
  { route: "credits", en: "Cloud Credits - HyperWhisper" },
];

// The home page's own title and description, which a page with none inherits.
const HOME_TITLE = (catalogues.get("en")!.metadata as Record<string, string>)
  .title;

for (const { route, en } of PAGES) {
  test(`/${route}: its own title, with the brand once, in all ${LOCALES.length} locales`, async () => {
    // A VARIABLE specifier: a literal `.tsx` specifier is a TS5097 error here.
    const pagePath = `../app/[locale]/${route}/page.tsx`;
    const page = (await import(pagePath)) as PageModule;

    assert.equal(LOCALES.length, 40);
    for (const locale of LOCALES) {
      const metadata = await page.generateMetadata({
        params: Promise.resolve({ locale }),
      });
      const title = resolveTitle(metadata.title);

      assert.ok(title, `${locale} /${route} has no title`);
      assert.notEqual(title, HOME_TITLE, `${locale} /${route}`);
      assert.equal(
        title.split("HyperWhisper").length - 1,
        1,
        `${locale} /${route}: "${title}"`,
      );
      assert.ok(metadata.description, `${locale} /${route} has no description`);
      if (locale === "en") assert.equal(title, en);
    }
  });
}
