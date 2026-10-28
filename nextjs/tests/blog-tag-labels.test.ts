/**
 * #1065: blog tag chips are lowercase keywords with proper nouns cased, on
 * the /en/blog cards and in a post's header. The raw tag stays the React key.
 *
 * The helper is tested on the issue's cases and on the live chip vocabulary;
 * then both real pages are rendered to static markup, with the blog loader
 * and the next-intl Link mocked (neither resolves under node --test).
 */
import assert from "node:assert/strict";
import { before, describe, test } from "node:test";
import { createElement, type ReactElement, type ReactNode } from "react";
import { renderToStaticMarkup } from "react-dom/server";

import { formatTagLabel } from "../src/content/blog-tags";
import { moduleMock, moduleUrl } from "./module-mock";

describe("formatTagLabel", () => {
  const cases: [string, string][] = [
    // The issue's Done-when.
    ["best ai writing assistant", "best AI writing assistant"],
    ["hyperwhisper", "HyperWhisper"],
    ["wispr-flow", "Wispr Flow"],
    ["dictation mac not working", "dictation Mac not working"],
    ["word error rate", "word error rate"],
    // Slug chips from the live census.
    ["aqua-voice", "Aqua Voice"],
    ["willow-voice", "Willow Voice"],
    ["real-time-transcription", "real time transcription"],
    // Lowercased names from the live census.
    ["ios transcription", "iOS transcription"],
    ["macos voice typing", "macOS voice typing"],
    ["ehr dictation mac", "EHR dictation Mac"],
    ["whisper ai", "Whisper AI"],
    ["wpm test", "WPM test"],
    ["hipaa phi asr", "HIPAA PHI ASR"],
    ["imessage on iphone", "iMessage on iPhone"],
    ["openai python", "OpenAI Python"],
    [
      "voiceink vs superwhisper vs macwhisper",
      "VoiceInk vs Superwhisper vs MacWhisper",
    ],
    // Already cased by Outrank: kept.
    ["AI writing tools", "AI writing tools"],
    ["EHR dictation", "EHR dictation"],
    ["macOS troubleshooting", "macOS troubleshooting"],
    // Hand-set Title Case on the code posts: lowercase, names kept.
    ["Comparison", "comparison"],
    ["Pricing", "pricing"],
    ["Spokenly", "Spokenly"],
    ["Handy", "Handy"],
    ["FluidVoice", "FluidVoice"],
    ["Windows", "Windows"],
    // Common words are never names; pair words only change as a pair.
    ["voice to text", "voice to text"],
    ["workflow tips", "workflow tips"],
    ["microsoft teams", "microsoft teams"],
    // Not a word match inside a longer word.
    ["aim for accuracy", "aim for accuracy"],
    ["  hyperwhisper  ", "HyperWhisper"],
  ];

  for (const [tag, label] of cases) {
    test(`${JSON.stringify(tag)} -> ${JSON.stringify(label)}`, () => {
      assert.equal(formatTagLabel(tag), label);
    });
  }
});

const post = {
  locale: "en",
  slug: "a-post",
  frontMatter: {
    title: "A post",
    description: "About a thing",
    date: "2026-01-01T00:00:00.000Z",
    tags: ["best ai writing assistant", "hyperwhisper", "wispr-flow"],
  },
  content: "",
  html: "<p>Body</p>",
};

moduleMock.module(moduleUrl("../src/content/blog.ts"), {
  namedExports: {
    getBlogPost: async (slug: string) => (slug === post.slug ? post : null),
    // The first post is the featured card, which draws no chips.
    getBlogPosts: async () => [{ ...post, slug: "featured" }, post],
  },
});
moduleMock.module(moduleUrl("../src/i18n/navigation.ts"), {
  namedExports: {
    Link: ({ children, href }: { children: ReactNode; href: string }) =>
      createElement("a", { href }, children),
  },
});

type Page = (props: {
  params: Promise<{ locale: string; slug?: string }>;
}) => Promise<ReactElement>;

function chips(html: string): string[] {
  return Array.from(
    html.matchAll(/<span class="rounded-full[^"]*">([^<]*)<\/span>/g),
    (m) => m[1],
  );
}

describe("the chips on both pages", () => {
  let index: Page;
  let postPage: Page;

  before(async () => {
    ({ default: index } = (await import("../app/[locale]/blog/page")) as {
      default: Page;
    });
    ({ default: postPage } = (await import(
      "../app/[locale]/blog/[slug]/page"
    )) as { default: Page });
  });

  const expected = ["best AI writing assistant", "HyperWhisper", "Wispr Flow"];

  test("/en/blog cards", async () => {
    const html = renderToStaticMarkup(
      await index({ params: Promise.resolve({ locale: "en" }) }),
    );

    assert.deepEqual(chips(html), expected);
  });

  test("a post's header", async () => {
    const html = renderToStaticMarkup(
      await postPage({
        params: Promise.resolve({ locale: "en", slug: post.slug }),
      }),
    );

    assert.deepEqual(chips(html), expected);
  });
});
