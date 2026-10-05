/**
 * #1312: the 3 English-only `force-static` routes sent the home page as their
 * `og:url`, and `/en/blog` also sent it as its canonical and its `og:title`.
 *
 * Under `force-static` the layout's `headers()` is empty, so the layout falls
 * back to `https://hyperwhisper.com/en`. Each page now states its own canonical
 * and `openGraph`. Next merges metadata SHALLOWLY per top-level key, so a page
 * `openGraph` replaces the layout's whole object: this file also checks the
 * page still sends the og:image list, site name, type and locale.
 *
 * It calls the REAL `generateMetadata` of each page. The three content modules
 * they import read the database (and import "server-only"), so those are
 * replaced with `mock.module`; `generateMetadata` never calls them.
 */
import assert from "node:assert/strict";
import test, { mock } from "node:test";

/**
 * `mock.module` is a real Node 22 API (behind `--experimental-test-module-mocks`,
 * which `npm test` passes) but this repo pins `@types/node@20`, which has no
 * declaration for it. Narrowed to the one option used here.
 */
interface ModuleMocker {
  module(
    specifier: string,
    options: { namedExports: Record<string, unknown> },
  ): void;
}

const moduleMock = mock as unknown as ModuleMocker;

function moduleUrl(relative: string): string {
  return new URL(relative, import.meta.url).href;
}

const unexpected = (name: string) => () => {
  throw new Error(`unexpected ${name} call in a metadata test`);
};

moduleMock.module(moduleUrl("../src/content/blog.ts"), {
  namedExports: { getBlogPosts: unexpected("getBlogPosts") },
});
moduleMock.module(moduleUrl("../src/content/latency.ts"), {
  namedExports: { getAllLatencyMatrices: unexpected("getAllLatencyMatrices") },
});
moduleMock.module(moduleUrl("../src/content/choosing-a-model.ts"), {
  namedExports: { getMeasuredLatency: unexpected("getMeasuredLatency") },
});

type PageMetadata = {
  title: string;
  alternates: { canonical: string; languages?: unknown };
  openGraph: {
    type: string;
    locale: string;
    url: string;
    title: string;
    description: string;
    siteName: string;
    images: { url: string; width: number; height: number; alt: string }[];
  };
};

type PageModule = {
  dynamic: string;
  generateMetadata: () => Promise<PageMetadata>;
};

const ROUTES = [
  { route: "blog", title: "Blog - HyperWhisper" },
  { route: "latency", title: "Speech-to-text latency, measured - HyperWhisper" },
  {
    route: "choosing-a-model",
    title: "Choosing a speech-to-text model - HyperWhisper",
  },
];

const HOME = "https://hyperwhisper.com/en";

for (const { route, title } of ROUTES) {
  test(`/en/${route} states its own canonical and og:url, and keeps the layout's og fields`, async () => {
    // A VARIABLE specifier: a literal `.tsx` specifier is a TS5097 error here.
    const pagePath = `../app/[locale]/${route}/page.tsx`;
    const page = (await import(pagePath)) as PageModule;
    const metadata = await page.generateMetadata();
    const expectedUrl = `${HOME}/${route}`;

    // #843: these are the only cached routes; the fix must not undo that.
    assert.equal(page.dynamic, "force-static");

    assert.equal(metadata.alternates.canonical, expectedUrl);
    assert.equal(metadata.openGraph.url, expectedUrl);
    assert.notEqual(metadata.openGraph.url, HOME);

    // English-only: every other locale 404s, so no hreflang map.
    assert.equal(metadata.alternates.languages, undefined);

    // The layout's "%s - HyperWhisper" template does not reach og:title.
    assert.equal(metadata.openGraph.title, title);
    assert.ok(metadata.openGraph.description.length > 0);

    // The shallow-merge trap: these would vanish if the page sent only `url`.
    assert.equal(metadata.openGraph.type, "website");
    assert.equal(metadata.openGraph.locale, "en_US");
    assert.equal(metadata.openGraph.siteName, "HyperWhisper");
    assert.deepEqual(
      Array.from(metadata.openGraph.images, (image) => image.url),
      [1024, 512, 256, 128].map(
        (size) => `https://hyperwhisper.com/icon/${size}.png`,
      ),
    );
  });
}
