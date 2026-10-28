/**
 * #1335: images and YouTube embeds inside a blog post's body get the same
 * frame as the cover above them (8px corner + 1px gray-800 border), and the
 * cover no longer stacks a second white/10 outline inside that border.
 *
 * Renders the real `[slug]` page to static markup. The blog loader and the
 * next-intl Link are mocked: neither resolves under node --test (the loader
 * imports "server-only" and the database).
 *
 * WHAT IT DOES NOT PROVE. That Tailwind compiles `prose-img:` and
 * `[&_iframe]:` into rules a browser applies to the post HTML; the verify
 * round reads the computed style in Chromium.
 */
import assert from "node:assert/strict";
import { before, test } from "node:test";
import { createElement, type ReactElement, type ReactNode } from "react";
import { renderToStaticMarkup } from "react-dom/server";

import { moduleMock, moduleUrl } from "./module-mock";

const post = {
  locale: "en",
  slug: "a-post",
  frontMatter: {
    title: "A post",
    description: "About a thing",
    date: "2026-01-01T00:00:00.000Z",
    image: "https://example.com/cover.png",
    tags: [],
  },
  content: "",
  html: '<p>Body</p><img src="https://example.com/a.png" alt="a"><iframe src="https://www.youtube.com/embed/x"></iframe>',
};

moduleMock.module(moduleUrl("../src/content/blog.ts"), {
  namedExports: {
    getBlogPost: async (slug: string) => (slug === post.slug ? post : null),
    getBlogPosts: async () => [post],
  },
});
moduleMock.module(moduleUrl("../src/i18n/navigation.ts"), {
  namedExports: {
    Link: ({ children, href }: { children: ReactNode; href: string }) =>
      createElement("a", { href }, children),
  },
});

type Page = (props: {
  params: Promise<{ locale: string; slug: string }>;
}) => Promise<ReactElement>;
let BlogPostPage: Page;
let html: string;

before(async () => {
  ({ default: BlogPostPage } = (await import(
    "../app/[locale]/blog/[slug]/page"
  )) as { default: Page });
  html = renderToStaticMarkup(
    await BlogPostPage({
      params: Promise.resolve({ locale: "en", slug: post.slug }),
    }),
  );
});

function classOf(fragment: string): string[] {
  const match = new RegExp(`class="([^"]*)"[^>]*>${fragment}`).exec(html);
  assert.ok(match, `no element before ${fragment}`);
  // The markup escapes the "&" of an arbitrary variant like `[&_iframe]:`.
  return match[1].replaceAll("&amp;", "&").split(/\s+/);
}

test("the prose wrapper frames body images and embeds like the cover", () => {
  const prose = classOf("<p>Body</p>");
  for (const token of [
    "prose-img:rounded-lg",
    "prose-img:border",
    "prose-img:border-gray-800",
    "[&_iframe]:rounded-lg",
    "[&_iframe]:border",
    "[&_iframe]:border-gray-800",
  ]) {
    assert.ok(prose.includes(token), `prose wrapper lacks ${token}`);
  }
  // One edge recipe, not two: no outline stacked on the body images.
  assert.deepEqual(
    prose.filter((c) => c.startsWith("[&_img]:outline")),
    [],
  );
});

test("the cover keeps its frame and draws no second edge", () => {
  const wrapper = classOf('<img alt="A post"');
  for (const token of ["rounded-lg", "border", "border-gray-800"]) {
    assert.ok(wrapper.includes(token), `cover wrapper lacks ${token}`);
  }
  const cover = /<img alt="A post" class="([^"]*)"/.exec(html);
  assert.ok(cover);
  assert.doesNotMatch(cover[1], /outline/);
});
