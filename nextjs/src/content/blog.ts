import "server-only";

import { and, desc, eq } from "drizzle-orm";

import { db } from "@/src/db";
import { blogPosts, type BlogPostRow } from "@/src/db/schema/blog-posts";
import {
  lazyLoadBodyImages,
  scrollableBodyTables,
} from "@/src/content/blog-html";
import { sanitizeBlogHtml } from "@/src/content/sanitize-blog-html";
import { bestDictationSoftware } from "@/src/content/code-posts/best-dictation-software";
import { bestDictationSoftwareForWindows } from "@/src/content/code-posts/best-dictation-software-for-windows";
import { bestDictationSoftwareForWriters } from "@/src/content/code-posts/best-dictation-software-for-writers";
import { freeSpeechToTextApp } from "@/src/content/code-posts/free-speech-to-text-app";
import { hyperwhisperVsHandyVsFluidvoice } from "@/src/content/code-posts/hyperwhisper-vs-handy-vs-fluidvoice";
import { hyperwhisperVsSpokenly } from "@/src/content/code-posts/hyperwhisper-vs-spokenly";
import { macVoiceDictationSoftware } from "@/src/content/code-posts/mac-voice-dictation-software";

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
  /**
   * The source text of a code post. Never read from the database: the stored
   * markdown is not rendered anywhere, and selecting it moved about 2 MB per
   * query (#2989).
   */
  content?: string;
  html: string;
};

function normalizeSlug(value: string) {
  return value.replace(/^\/+|\/+$/g, "");
}

/**
 * The columns a list, the sitemap or generateStaticParams needs. Neither body
 * column is in it: together they are most of the table's bytes (#2989).
 */
const SUMMARY_COLUMNS = {
  locale: blogPosts.locale,
  slug: blogPosts.slug,
  title: blogPosts.title,
  description: blogPosts.description,
  imageUrl: blogPosts.imageUrl,
  imageAlt: blogPosts.imageAlt,
  tags: blogPosts.tags,
  publishedAt: blogPosts.publishedAt,
};

/** One post's page: the summary columns and the stored HTML, never the markdown. */
const POST_COLUMNS = { ...SUMMARY_COLUMNS, contentHtml: blogPosts.contentHtml };

type SummaryRow = Pick<BlogPostRow, keyof typeof SUMMARY_COLUMNS>;
type PostRow = Pick<BlogPostRow, keyof typeof POST_COLUMNS>;

function rowToSummary(row: SummaryRow): BlogPostSummary {
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
  };
}

function rowToPost(row: PostRow): BlogPost {
  return { ...rowToSummary(row), html: row.contentHtml };
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
 * An empty TABLE is not a failure: it lists only the code posts on both. Both
 * the list query and the single-post query go through here.
 */
async function readStoredRows<Row>(
  query: () => Promise<Row[]>,
): Promise<Row[]> {
  try {
    return await query();
  } catch (err) {
    if (!isBuildPrerender()) throw err;
    console.error(
      "[blog] database unreachable while prerendering; shipping only the code posts and letting revalidation fill in the rest:",
      err,
    );

    return [];
  }
}

/** Every stored English post, newest first, without either body column. */
async function readPostSummaries(): Promise<BlogPostSummary[]> {
  const rows = await readStoredRows(() =>
    db
      .select(SUMMARY_COLUMNS)
      .from(blogPosts)
      .where(eq(blogPosts.locale, BLOG_LOCALE))
      .orderBy(desc(blogPosts.publishedAt)),
  );

  return withCodePosts(rows.map(rowToSummary));
}

/**
 * Posts written in the repo rather than published by Outrank, such as a
 * comparison page that has to cite its sources. They render, list and enter
 * the sitemap exactly like a stored post. A code post with the same slug
 * replaces a stored row, so fact-checked copy in git is what the site serves.
 */
const CODE_POSTS: BlogPost[] = [
  bestDictationSoftware,
  bestDictationSoftwareForWindows,
  bestDictationSoftwareForWriters,
  freeSpeechToTextApp,
  hyperwhisperVsSpokenly,
  hyperwhisperVsHandyVsFluidvoice,
  macVoiceDictationSoftware,
];

/**
 * Outrank slugs that next.config 308s onto a code post. Drop them from the
 * index and the sitemap so crawlers never see a URL that only redirects.
 * getBlogPost also refuses them, so a missed redirect cannot serve the old
 * body as a duplicate of the canonical page.
 */
export const REDIRECTED_BLOG_SLUGS = new Set(["good-dictation-software"]);

function withCodePosts(stored: BlogPostSummary[]): BlogPostSummary[] {
  const codeSlugs = new Set(CODE_POSTS.map((post) => post.slug));

  return [
    ...stored.filter(
      (post) =>
        !codeSlugs.has(post.slug) && !REDIRECTED_BLOG_SLUGS.has(post.slug),
    ),
    ...CODE_POSTS.map(({ locale, slug, frontMatter }) => ({
      locale,
      slug,
      frontMatter,
    })),
  ].sort(
    (a, b) => Date.parse(b.frontMatter.date) - Date.parse(a.frontMatter.date),
  );
}

export async function getBlogPosts(): Promise<BlogPostSummary[]> {
  return readPostSummaries();
}

/**
 * One stored English post, found through the blog_posts_locale_slug_unique
 * index. Every stored slug comes from the webhook's slugify(), which leaves no
 * "/" in it, so matching the normalised slug exactly finds the same row the old
 * whole-table .find() over normalizeSlug(row.slug) did.
 */
async function readStoredPost(slug: string): Promise<BlogPost | undefined> {
  const [row] = await readStoredRows(() =>
    db
      .select(POST_COLUMNS)
      .from(blogPosts)
      .where(and(eq(blogPosts.locale, BLOG_LOCALE), eq(blogPosts.slug, slug)))
      .limit(1),
  );

  return row ? rowToPost(row) : undefined;
}

export async function getBlogPost(slug: string): Promise<BlogPost | null> {
  const normalizedSlug = normalizeSlug(slug);
  if (REDIRECTED_BLOG_SLUGS.has(normalizedSlug)) return null;
  // A code post replaces a stored row with the same slug, so a code slug never
  // reaches the database.
  const stored =
    CODE_POSTS.find((item) => item.slug === normalizedSlug) ??
    (await readStoredPost(normalizedSlug));

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
