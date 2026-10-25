import "server-only";

import { desc } from "drizzle-orm";

import { db } from "@/src/db";
import { blogPosts, type BlogPostRow } from "@/src/db/schema/blog-posts";
import {
  lazyLoadBodyImages,
  scrollableBodyTables,
} from "@/src/content/blog-html";
import { sanitizeBlogHtml } from "@/src/content/sanitize-blog-html";
import { hyperwhisperVsHandyVsFluidvoice } from "@/src/content/code-posts/hyperwhisper-vs-handy-vs-fluidvoice";
import { hyperwhisperVsSpokenly } from "@/src/content/code-posts/hyperwhisper-vs-spokenly";

const BLOG_LOCALE = "en";

export type BlogPostFrontMatter = {
  title: string;
  description: string;
  date: string;
  image?: string;
  imageAlt?: string;
  tags?: string[];
  slug?: string;
};

export type BlogPostSummary = {
  locale: string;
  slug: string;
  frontMatter: BlogPostFrontMatter;
};

export type BlogPost = BlogPostSummary & {
  content: string;
  html: string;
};

function normalizeSlug(value: string) {
  return value.replace(/^\/+|\/+$/g, "");
}

function rowToPost(row: BlogPostRow): BlogPost {
  const frontMatter: BlogPostFrontMatter = {
    title: row.title,
    description: row.description ?? "",
    date: row.publishedAt.toISOString(),
    image: row.imageUrl ?? undefined,
    imageAlt: row.imageAlt ?? undefined,
    tags: row.tags ?? [],
    slug: row.slug,
  };

  return {
    locale: row.locale,
    slug: normalizeSlug(row.slug),
    frontMatter,
    content: row.contentMarkdown,
    html: row.contentHtml,
  };
}

/**
 * True while `next build` prerenders, false in the server that serves and
 * revalidates the built pages. The same test as src/content/latency.ts; read at
 * call time rather than module load, so both paths can be exercised in one
 * test process.
 */
function isBuildPrerender(): boolean {
  return process.env.NEXT_PHASE === "phase-production-build";
}

/**
 * A database failure is handled the way getLatencyMatrix handles it (#2072):
 *
 *  - During `next build` it is logged and the blog ships with only the code
 *    posts, so a deploy does not depend on Postgres being reachable from the
 *    builder. The first revalidation fills in the stored ones.
 *  - At runtime it propagates. Next caches a returned value as a good render,
 *    so an empty list during a background revalidation would replace the blog
 *    with "No posts yet.", 404 every post and drop them from the sitemap.
 *    Throwing keeps the last good page in the ISR cache.
 *
 * An empty TABLE is not a failure: it lists only the code posts on both.
 */
async function readAllPosts(): Promise<BlogPost[]> {
  try {
    const rows = await db
      .select()
      .from(blogPosts)
      .orderBy(desc(blogPosts.publishedAt));

    return withCodePosts(
      rows.filter((row) => row.locale === BLOG_LOCALE).map(rowToPost),
    );
  } catch (err) {
    if (!isBuildPrerender()) throw err;
    console.error(
      "[blog] database unreachable while prerendering; shipping only the code posts and letting revalidation fill in the rest:",
      err,
    );

    return withCodePosts([]);
  }
}

/**
 * Posts written in the repo rather than published by Outrank, such as a
 * comparison page that has to cite its sources. They render, list and enter
 * the sitemap exactly like a stored post. A stored row with the same slug
 * wins, so Outrank can still replace one.
 */
const CODE_POSTS: BlogPost[] = [
  hyperwhisperVsSpokenly,
  hyperwhisperVsHandyVsFluidvoice,
];

function withCodePosts(stored: BlogPost[]): BlogPost[] {
  const slugs = new Set(stored.map((post) => post.slug));

  return [
    ...stored,
    ...CODE_POSTS.filter((post) => !slugs.has(post.slug)),
  ].sort(
    (a, b) => Date.parse(b.frontMatter.date) - Date.parse(a.frontMatter.date),
  );
}

export async function getBlogPosts(): Promise<BlogPostSummary[]> {
  const posts = await readAllPosts();

  return posts.map((post) => ({
    locale: post.locale,
    slug: post.slug,
    frontMatter: post.frontMatter,
  }));
}

export async function getBlogPost(slug: string): Promise<BlogPost | null> {
  const normalizedSlug = normalizeSlug(slug);
  const posts = await readAllPosts();
  const stored = posts.find((item) => item.slug === normalizedSlug);

  if (!stored) return null;
  // Sanitise again on read, so a row stored before the webhook sanitised its
  // HTML is covered too (#2009). It runs first, so the lazy-load attributes
  // added below are never stripped.
  const post = { ...stored, html: sanitizeBlogHtml(stored.html) };

  // Only the post being shown needs its stored body rewritten (#919, #2112).
  return { ...post, html: scrollableBodyTables(lazyLoadBodyImages(post.html)) };
}

export async function getAllBlogPosts(): Promise<BlogPostSummary[]> {
  return getBlogPosts();
}
