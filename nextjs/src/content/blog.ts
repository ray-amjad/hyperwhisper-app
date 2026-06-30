import "server-only";

import { desc } from "drizzle-orm";

import { db } from "@/src/db";
import { blogPosts, type BlogPostRow } from "@/src/db/schema/blog-posts";

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
    console.error("[blog] failed to load posts from DB:", err);
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
  return posts.find((item) => item.slug === normalizedSlug) ?? null;
}

export async function getAllBlogPosts(): Promise<BlogPostSummary[]> {
  return getBlogPosts();
}
