import "server-only";

import { desc } from "drizzle-orm";

import { db } from "@/src/db";
import { blogPosts, type BlogPostRow } from "@/src/db/schema/blog-posts";
import {
  lazyLoadBodyImages,
  scrollableBodyTables,
} from "@/src/content/blog-html";
import { sanitizeBlogHtml } from "@/src/content/sanitize-blog-html";

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
 *  - During `next build` it is logged and the blog ships empty, so a deploy
 *    does not depend on Postgres being reachable from the builder. The first
 *    revalidation fills it in.
 *  - At runtime it propagates. Next caches a returned value as a good render,
 *    so an empty list during a background revalidation would replace the blog
 *    with "No posts yet.", 404 every post and drop them from the sitemap.
 *    Throwing keeps the last good page in the ISR cache.
 *
 * An empty TABLE is not a failure: no rows renders "No posts yet." on both.
 */
async function readAllPosts(): Promise<BlogPost[]> {
  try {
    const rows = await db
      .select()
      .from(blogPosts)
      .orderBy(desc(blogPosts.publishedAt));
    return rows
      .filter((row) => row.locale === BLOG_LOCALE)
      .map(rowToPost);
  } catch (err) {
    if (!isBuildPrerender()) throw err;
    console.error(
      "[blog] database unreachable while prerendering; shipping no posts and letting revalidation fill them in:",
      err,
    );
    return [];
  }
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
