/**
 * #2265: every page built with `localizedPageMetadata` sent the HOME page's
 * og:title and og:description, and the [locale] layout's `twitter` block sent
 * the home title, so a shared /download link unfurled as the home page.
 *
 * Calls the REAL `generateMetadata` of the [locale] layout and of each page,
 * with every locale's real catalogue, then merges them with Next's own
 * `accumulateMetadata`, the function that builds the <head> tags. The fix
 * relies on Next filling og/twitter title and description from the page's
 * resolved <title>, so only Next's real merge can prove it.
 */
import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";
import Module, { createRequire } from "node:module";
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

// Next's metadata resolver requires "server-only", which a Next build aliases
// to Next's own empty copy and this repo does not install. Find it at that
// copy, the one thing a build would do differently.
const serverOnly = createRequire(import.meta.url).resolve(
  "next/dist/compiled/server-only/empty.js",
);
const finder = Module as unknown as {
  _findPath: (request: string, ...rest: unknown[]) => string | false;
};
const findPath = finder._findPath;

finder._findPath = (request, ...rest) =>
  request === "server-only" ? serverOnly : findPath(request, ...rest);

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

function translate(locale: string, namespace: string, key: string): string {
  const value = catalogues.get(locale)?.[namespace]?.[key];

  assert.equal(typeof value, "string", `${locale} ${namespace}.${key}`);

  return value as string;
}

moduleMock.module("next-intl/server", {
  namedExports: {
    setRequestLocale: () => {},
    getMessages: async () => ({}),
    getTranslations: async ({
      locale,
      namespace,
    }: {
      locale: string;
      namespace: string;
    }) => {
      return (key: string) => translate(locale, namespace, key);
    },
  },
});

const stub = () => null;
const local = (relative: string) => new URL(relative, import.meta.url).href;

// The layout's render imports CSS, next/font and client providers, which node
// cannot load; its metadata reads none of them.
moduleMock.module(local("../styles/globals.css"), { defaultExport: {} });
moduleMock.module(local("../config/fonts.ts"), {
  namedExports: { fontSans: { variable: "" } },
});
moduleMock.module(local("../app/[locale]/providers.tsx"), {
  namedExports: { Providers: stub },
});
moduleMock.module(local("../components/layout/HtmlDocument.tsx"), {
  defaultExport: stub,
});
moduleMock.module(local("../components/layout/LayoutWrapper.tsx"), {
  defaultExport: stub,
});
moduleMock.module("next-intl", {
  namedExports: { NextIntlClientProvider: stub },
});
// The purchase form is a client component with its own imports; the metadata
// never renders it.
moduleMock.module(local("../components/credits/CreditsPurchase.tsx"), {
  defaultExport: stub,
});

type Metadata = Record<string, unknown>;

type MetadataModule = {
  generateMetadata: (props: {
    params: Promise<{ locale: string }>;
  }) => Promise<Metadata>;
};

type Resolved = {
  title: { absolute: string } | null;
  description: string | null;
  openGraph: {
    title: { absolute: string };
    description?: string;
    type: string;
    locale: string;
    siteName: string;
    url: URL;
    images: { url: URL }[];
  };
  twitter: {
    title: { absolute: string };
    description?: string;
    card: string;
    creator: string;
    images: { url: URL }[];
  };
};

type AccumulateMetadata = (
  route: string,
  items: [Metadata | null, null][],
  pathname: Promise<string>,
  context: { trailingSlash: boolean; isStaticMetadataRouteFile: boolean },
) => Promise<Resolved>;

// The function Next calls to build a route's metadata. It takes one item per
// segment of the route tree, root first, and stashes a layout's title template
// for the segments below it (never for the leaf layout and page), so the items
// mirror the real tree: the root layout (no metadata), the [locale] layout,
// one per path segment (its layout.tsx, or null), then the page.
async function nextResolve(
  route: string,
  items: (Metadata | null)[],
): Promise<Resolved> {
  // A VARIABLE specifier, so tsc does not type Next's internal module.
  const internal = "next/dist/lib/metadata/resolve-metadata.js";
  const { accumulateMetadata } = (await import(internal)) as {
    accumulateMetadata: AccumulateMetadata;
  };

  return accumulateMetadata(
    route,
    items.map((item) => [item, null]),
    Promise.resolve(route),
    { trailingSlash: false, isStaticMetadataRouteFile: false },
  );
}

async function load(file: string): Promise<MetadataModule> {
  // A VARIABLE specifier: a literal `.tsx` specifier is a TS5097 error here.
  const modulePath = `../app/[locale]/${file}`;

  return (await import(modulePath)) as MetadataModule;
}

const PAGES = [
  { file: "page.tsx", path: "", en: undefined },
  {
    file: "credits/page.tsx",
    path: "/credits",
    en: "Cloud Credits - HyperWhisper",
  },
  {
    file: "download/layout.tsx",
    path: "/download",
    en: "Download - HyperWhisper",
  },
  {
    file: "older-versions/layout.tsx",
    path: "/older-versions",
    en: "Older Versions - HyperWhisper",
  },
  {
    file: "open-source/page.tsx",
    path: "/open-source",
    en: "Open Source | HyperWhisper",
  },
  {
    file: "purchase-success/layout.tsx",
    path: "/purchase-success",
    en: "Purchase Successful - HyperWhisper",
  },
  {
    file: "support/layout.tsx",
    path: "/support",
    en: "Support - HyperWhisper",
  },
  {
    file: "user/sign-in/layout.tsx",
    path: "/user/sign-in",
    en: "Sign In - HyperWhisper",
  },
  {
    file: "legal/privacy-policy/page.tsx",
    path: "/legal/privacy-policy",
    en: "Privacy Policy - HyperWhisper",
  },
  {
    file: "legal/terms-of-service/page.tsx",
    path: "/legal/terms-of-service",
    en: "Terms of Service - HyperWhisper",
  },
  {
    file: "legal/refund-policy/page.tsx",
    path: "/legal/refund-policy",
    en: "Refund Policy - HyperWhisper",
  },
];

for (const { file, path, en } of PAGES) {
  const route = path === "" ? "/ (home)" : path;

  test(`${route}: og and twitter title and description are the page's own, in all ${LOCALES.length} locales`, async () => {
    const layout = await load("layout.tsx");
    const page = await load(file);

    assert.equal(LOCALES.length, 40);
    for (const locale of LOCALES) {
      const params = Promise.resolve({ locale });
      const leaf = await page.generateMetadata({ params });
      const segments = path
        .split("/")
        .filter(Boolean)
        .map(() => null);
      const resolved = await nextResolve(
        `/${locale}${path}`,
        file.endsWith("/layout.tsx")
          ? [
              null,
              await layout.generateMetadata({ params }),
              ...segments.slice(1),
              leaf,
              null,
            ]
          : [
              null,
              await layout.generateMetadata({ params }),
              ...segments,
              leaf,
            ],
      );
      const where = `${locale} ${route}`;
      const title = resolved.title?.absolute;
      const homeTitle = translate(locale, "metadata", "title");
      const homeDescription = translate(locale, "metadata", "description");

      assert.ok(title, `${where} has no <title>`);
      if (locale === "en" && en) assert.equal(title, en, where);
      if (path === "") assert.equal(title, homeTitle, where);
      else assert.notEqual(title, homeTitle, where);

      // The bug: these were the home page's strings on every page.
      assert.equal(
        resolved.openGraph.title.absolute,
        title,
        `${where} og:title`,
      );
      assert.equal(
        resolved.twitter.title.absolute,
        title,
        `${where} twitter:title`,
      );
      assert.equal(
        resolved.openGraph.description,
        resolved.description ?? undefined,
        `${where} og:description`,
      );
      assert.equal(
        resolved.twitter.description,
        resolved.description ?? undefined,
        `${where} twitter:description`,
      );
      // Sign-in has no description of its own, so it keeps the layout's.
      if (path !== "" && path !== "/user/sign-in") {
        assert.notEqual(resolved.description, homeDescription, where);
      }

      // Unchanged: the og image list, site name, type, locale and url.
      assert.equal(resolved.openGraph.type, "website", where);
      assert.equal(resolved.openGraph.siteName, "HyperWhisper", where);
      assert.match(resolved.openGraph.locale, /^[a-z]{2,3}_[A-Z]{2}$/, where);
      assert.equal(
        String(resolved.openGraph.url),
        `https://www.hyperwhisper.com/${locale}${path}`,
        where,
      );
      assert.deepEqual(
        resolved.openGraph.images.map((image) => String(image.url)),
        [1024, 512, 256, 128].map(
          (size) => `https://www.hyperwhisper.com/icon/${size}.png`,
        ),
        where,
      );
      assert.equal(resolved.twitter.card, "summary", where);
      assert.equal(resolved.twitter.creator, "@theramjad", where);
      assert.deepEqual(
        resolved.twitter.images.map((image) => String(image.url)),
        ["https://www.hyperwhisper.com/icon/256.png"],
        where,
      );
    }
  });
}
