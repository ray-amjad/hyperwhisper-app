import {
  pgTable,
  uuid,
  text,
  timestamp,
  jsonb,
  index,
  unique,
} from "drizzle-orm/pg-core";

export const blogPosts = pgTable(
  "blog_posts",
  {
    id: uuid("id").defaultRandom().primaryKey(),
    externalId: text("external_id").notNull().unique(),
    source: text("source").notNull().default("outrank"),
    locale: text("locale").notNull().default("en"),
    slug: text("slug").notNull(),
    title: text("title").notNull(),
    description: text("description"),
    contentMarkdown: text("content_markdown").notNull(),
    contentHtml: text("content_html").notNull(),
    imageUrl: text("image_url"),
    imageAlt: text("image_alt"),
    tags: jsonb("tags").$type<string[]>().notNull().default([]),
    publishedAt: timestamp("published_at", { withTimezone: true })
      .notNull()
      .defaultNow(),
    createdAt: timestamp("created_at", { withTimezone: true })
      .notNull()
      .defaultNow(),
    updatedAt: timestamp("updated_at", { withTimezone: true })
      .notNull()
      .defaultNow(),
  },
  (t) => ({
    localeSlugUnique: unique("blog_posts_locale_slug_unique").on(
      t.locale,
      t.slug,
    ),
    publishedAtIdx: index("blog_posts_published_at_idx").on(t.publishedAt),
  }),
);

export type BlogPostRow = typeof blogPosts.$inferSelect;
export type NewBlogPostRow = typeof blogPosts.$inferInsert;
