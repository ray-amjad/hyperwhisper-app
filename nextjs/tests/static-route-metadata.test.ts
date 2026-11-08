/**
 * #1312: the 3 English-only `force-static` routes sent the home page as their
 * `og:url`, and `/en/blog` also sent it as its canonical and its `og:title`.
 *
 * Under `force-static` the layout's `headers()` is empty, so the layout falls
 * back to `https://www.hyperwhisper.com/en`. Each page now states its own canonical
 * and `openGraph`. Next merges metadata SHALLOWLY per top-level key, so a page
 * `openGraph` replaces the layout's whole object: this file also checks the
 * page still sends the og:image list, site name, type and locale. The same
 * holds for `twitter`, whose card, creator and images the layout sets.
 *
 * It calls the REAL `generateMetadata` of each page. The three content modules
 * they import read the database (and import "server-only"), so those are
 * replaced with `mock.module`. The 3 index routes' `generateMetadata` never
 * calls them; a blog post's reads its post through the mocked `getBlogPost`
 * (#1316).
 */
import assert from "node:assert/strict";
import test from "node:test";

import { moduleMock, moduleUrl } from "./module-mock";

const unexpected = (name: string) => () => {
  throw new Error(`unexpected ${name} call in a metadata test`);
};

// Two posts for the /en/blog/<slug> case (#1316): one with a front-matter
// image, one without, which must fall back to the site card.
const BLOG_POSTS: Record<string, unknown> = {
  "with-image": {
    slug: "with-image",
    frontMatter: {
      title: "A post with an image",
      description: "Has its own card.",
      image: "/blog/with-image.png",
      imageAlt: "Its own card",
    },
  },
  "no-image": {
    slug: "no-image",
    frontMatter: { title: "A post with no image", description: "No card." },
  },
};

moduleMock.module(moduleUrl("../src/content/blog.ts"), {
  namedExports: {
    getBlogPosts: unexpected("getBlogPosts"),
    getBlogPost: async (slug: string) => BLOG_POSTS[slug] ?? null,
  },
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
  twitter: {
    card: string;
    title: string;
    description: string;
    creator: string;
    images: string[];
  };
};

type PageModule = {
  dynamic: string;
  generateMetadata: (props: {
    params: Promise<{ locale: string }>;
  }) => Promise<PageMetadata>;
};

const ROUTES = [
  { route: "blog", title: "Blog - HyperWhisper" },
  { route: "latency", title: "Speech-to-text latency, measured - HyperWhisper" },
  {
    route: "choosing-a-model",
    title: "Choosing a speech-to-text model - HyperWhisper",
  },
];

const HOME = "https://www.hyperwhisper.com/en";

for (const { route, title } of ROUTES) {
  test(`/en/${route} states its own canonical and og:url, and keeps the layout's og fields`, async () => {
    // A VARIABLE specifier: a literal `.tsx` specifier is a TS5097 error here.
    const pagePath = `../app/[locale]/${route}/page.tsx`;
    const page = (await import(pagePath)) as PageModule;
    const metadata = await page.generateMetadata({
      params: Promise.resolve({ locale: "en" }),
    });
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
      ["https://www.hyperwhisper.com/og-image.png"],
    );

    // The page sends its whole `twitter` object, so the shallow merge keeps
    // the card, creator and images and X previews the link with this title.
    assert.equal(metadata.twitter.title, title);
    assert.equal(metadata.twitter.description, metadata.openGraph.description);
    assert.equal(metadata.twitter.card, "summary_large_image");
    assert.equal(metadata.twitter.creator, "@theramjad");
    assert.deepEqual(metadata.twitter.images, [
      "https://www.hyperwhisper.com/og-image.png",
    ]);
  });
}

type BlogPostModule = {
  generateMetadata: (props: {
    params: Promise<{ locale: string; slug: string }>;
  }) => Promise<
    Omit<PageMetadata, "openGraph"> & {
      openGraph: Omit<PageMetadata["openGraph"], "images"> & {
        images: { url: string; alt: string }[];
      };
    }
  >;
};

for (const { slug, ogImage, ogAlt } of [
  {
    slug: "with-image",
    ogImage: "https://www.hyperwhisper.com/blog/with-image.png",
    ogAlt: "Its own card",
  },
  {
    slug: "no-image",
    ogImage: "https://www.hyperwhisper.com/og-image.png",
    ogAlt: "HyperWhisper",
  },
]) {
  test(`/en/blog/${slug} keeps the layout's og:site_name, twitter:creator and an og:image (#1316)`, async () => {
    const pagePath = "../app/[locale]/blog/[slug]/page.tsx";
    const page = (await import(pagePath)) as BlogPostModule;
    const metadata = await page.generateMetadata({
      params: Promise.resolve({ locale: "en", slug }),
    });
    const expectedUrl = `${HOME}/blog/${slug}`;

    assert.equal(metadata.alternates.canonical, expectedUrl);
    assert.equal(metadata.openGraph.url, expectedUrl);
    assert.equal(metadata.openGraph.type, "article");
    assert.equal(metadata.openGraph.locale, "en_US");

    // The page's `openGraph` and `twitter` replace the layout's whole objects,
    // so these vanish unless the page restates them.
    assert.equal(metadata.openGraph.siteName, "HyperWhisper");
    assert.equal(metadata.twitter.creator, "@theramjad");
    assert.equal(metadata.twitter.card, "summary_large_image");

    assert.deepEqual(
      Array.from(metadata.openGraph.images, (image) => [image.url, image.alt]),
      [[ogImage, ogAlt]],
    );
    assert.deepEqual(metadata.twitter.images, [ogImage]);
  });
}
