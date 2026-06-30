import { NextRequest, NextResponse } from "next/server";
import { revalidatePath } from "next/cache";
import { createHmac, timingSafeEqual } from "node:crypto";
import { and, eq, ne } from "drizzle-orm";
import { timingSafeEqualSecret } from "@/lib/security/timing-safe-secret";
import { env } from "@/src/env/server.mjs";
import { db } from "@/src/db";
import { blogPosts } from "@/src/db/schema/blog-posts";

/**
 * Outrank "publish_articles" webhook.
 *
 * Auth (issue #842): Outrank itself can only authenticate with a static
 * `Authorization: Bearer <OUTRANK_WEBHOOK_TOKEN>` — its webhook spec has no
 * request signing, so it never sends a signature header. The bearer token is
 * therefore the operative, permanent auth and must be treated as a long-lived
 * shared secret: keep it secret and rotate it if it may have leaked.
 *
 * The optional HMAC path (OUTRANK_WEBHOOK_SIGNING_SECRET) only engages when a
 * signing-capable proxy sits in front of this endpoint and sends a signature
 * header. When that secret is unset (the expected state for direct Outrank
 * deliveries) the bearer token is the sole auth — the HMAC branch is then
 * intentionally inert, not dead code, and exists so an integrity-binding proxy
 * can be added later without a code change.
 *
 * Upserts each article into blog_posts (keyed on external_id) and revalidates
 * the blog routes so new posts appear immediately.
 */

export const runtime = "nodejs";
export const dynamic = "force-dynamic";

// Reject oversized payloads before buffering them into memory. Outrank batches
// are small; a few MB is generous headroom for a legitimate publish.
const MAX_BODY_BYTES = 5 * 1024 * 1024; // 5 MB
// Cap the number of articles processed in a single delivery so one request
// cannot tie up the function for an unbounded number of serial DB round-trips.
const MAX_ARTICLES = 100;

type OutrankArticle = {
  id?: string;
  title?: string;
  content_markdown?: string;
  content_html?: string;
  meta_description?: string;
  description?: string;
  created_at?: string;
  published_at?: string;
  image_url?: string;
  image_alt?: string;
  slug?: string;
  tags?: string[];
  url?: string;
};

type OutrankPayload = {
  event_type?: string;
  timestamp?: string;
  data?: { articles?: OutrankArticle[] };
};

/**
 * Verify a per-request HMAC-SHA256 signature over the raw body. Returns false
 * when no signing secret is configured (so the caller can fall back to the
 * static bearer token).
 */
function isSignatureValid(rawBody: string, header: string | null): boolean {
  const secret = env.OUTRANK_WEBHOOK_SIGNING_SECRET;
  if (!secret || !header) return false;

  // Accept either a bare hex digest or a `sha256=<hex>` prefixed value.
  const provided = header.startsWith("sha256=")
    ? header.slice("sha256=".length).trim()
    : header.trim();

  const expected = createHmac("sha256", secret).update(rawBody).digest("hex");

  const encoder = new TextEncoder();
  const providedBuf = encoder.encode(provided.toLowerCase());
  const expectedBuf = encoder.encode(expected);
  if (providedBuf.length !== expectedBuf.length) return false;
  return timingSafeEqual(providedBuf, expectedBuf);
}

function isBearerAuthorized(req: NextRequest): boolean {
  const expected = env.OUTRANK_WEBHOOK_TOKEN;
  if (!expected) return false;
  const header = req.headers.get("authorization");
  if (!header || !header.startsWith("Bearer ")) return false;
  return timingSafeEqualSecret(header.slice("Bearer ".length).trim(), expected);
}

function slugify(input: string): string {
  return input
    .toString()
    .toLowerCase()
    .normalize("NFKD")
    .replace(/[̀-ͯ]/g, "")
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "")
    .slice(0, 120);
}

const LOCALE = "en";

/**
 * Return true when `slug` is free within the locale, i.e. not already claimed
 * by a DIFFERENT external article. (A row owned by `externalId` itself is fine
 * — the upsert resolves that on the externalId conflict target.)
 */
async function isSlugFree(slug: string, externalId: string): Promise<boolean> {
  const existing = await db
    .select({ id: blogPosts.id })
    .from(blogPosts)
    .where(
      and(
        eq(blogPosts.locale, LOCALE),
        eq(blogPosts.slug, slug),
        ne(blogPosts.externalId, externalId),
      ),
    )
    .limit(1);
  return existing.length === 0;
}

/**
 * Resolve a slug that is unique within the locale. If the desired slug is
 * already taken by a DIFFERENT external article, append a suffix derived from
 * the external id so the new article still persists instead of throwing a
 * (locale, slug) unique violation. The first candidate is verified too — and if
 * it is also taken (e.g. duplicate-title articles whose external ids share the
 * leading 8 alphanumerics) we widen the suffix and finally fall back to the
 * full external id, which is globally unique, so a free slug is guaranteed.
 */
async function resolveUniqueSlug(
  desired: string,
  externalId: string,
): Promise<string> {
  if (await isSlugFree(desired, externalId)) return desired;

  const sanitizedId = externalId.replace(/[^a-z0-9]+/gi, "").toLowerCase();

  // Try progressively longer slices of the external id, then the full id, so
  // articles that collide on a short shared prefix still resolve to distinct
  // slugs. Each candidate is verified before use.
  const suffixLengths = [8, 16, sanitizedId.length];
  for (const len of suffixLengths) {
    const suffix = sanitizedId.slice(0, len);
    if (!suffix) continue;
    const base = desired.slice(0, 120 - (suffix.length + 1));
    const candidate = `${base}-${suffix}`;
    if (await isSlugFree(candidate, externalId)) return candidate;
  }

  // Last resort: a timestamp keeps the slug unique even when the external id
  // alone somehow still collides, so the article persists rather than being
  // dropped on a unique violation.
  const suffix = `${sanitizedId || "post"}-${Date.now().toString(36)}`;
  const base = desired.slice(0, 120 - (suffix.length + 1));
  return `${base}-${suffix}`;
}

function escapeHtml(value: string): string {
  return value
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#39;");
}

function markdownToHtml(markdown: string): string {
  return markdown
    .trim()
    .split(/\n{2,}/)
    .map((block) => {
      const text = escapeHtml(block.trim());
      if (!text) return "";

      const heading = text.match(/^(#{1,3})\s+(.+)$/);
      if (heading) {
        const level = heading[1].length;
        return `<h${level}>${heading[2]}</h${level}>`;
      }

      return `<p>${text.replace(/\n/g, "<br />")}</p>`;
    })
    .filter(Boolean)
    .join("\n");
}

function parsePublishedAt(article: OutrankArticle): {
  publishedAt?: Date;
  error?: string;
} {
  const candidate = article.published_at ?? article.created_at;
  if (!candidate) return {};

  const parsed = new Date(candidate);
  if (Number.isNaN(parsed.getTime())) {
    const field = article.published_at ? "published_at" : "created_at";
    return { error: `invalid ${field}: ${candidate}` };
  }

  return { publishedAt: parsed };
}

function normalizeTags(tags: unknown): string[] {
  if (!Array.isArray(tags)) return [];

  return tags
    .filter((tag): tag is string => typeof tag === "string")
    .map((tag) => tag.trim())
    .filter(Boolean)
    .slice(0, 12);
}

export async function POST(req: NextRequest) {
  const contentLength = Number(req.headers.get("content-length"));
  if (Number.isFinite(contentLength) && contentLength > MAX_BODY_BYTES) {
    return NextResponse.json({ error: "Payload too large" }, { status: 413 });
  }

  // Signature verification needs the raw bytes, so we can only HMAC-check after
  // buffering the body. The bearer token, by contrast, is independent of the
  // payload — so when HMAC signing is disabled (the normal direct-Outrank
  // setup) reject a bad bearer token BEFORE allocating the body, instead of
  // buffering an unsigned, unauthenticated request.
  const signature =
    req.headers.get("x-outrank-signature") ??
    req.headers.get("outrank-signature");
  const signingEnabled = Boolean(env.OUTRANK_WEBHOOK_SIGNING_SECRET);
  if (!signingEnabled && !isBearerAuthorized(req)) {
    return NextResponse.json(
      { error: "Invalid access token" },
      { status: 401 },
    );
  }

  // Read the raw body once so we can both size-check it and verify an HMAC
  // signature over the exact bytes.
  let rawBody: string;
  try {
    rawBody = await req.text();
  } catch {
    return NextResponse.json({ error: "Invalid body" }, { status: 400 });
  }

  if (Buffer.byteLength(rawBody, "utf8") > MAX_BODY_BYTES) {
    return NextResponse.json({ error: "Payload too large" }, { status: 413 });
  }

  // When signing is enabled, prefer HMAC signature verification but still fall
  // back to the static bearer token for callers that have not migrated to
  // signing. (When signing is disabled the bearer token was already verified
  // above before the body was buffered.)
  if (signingEnabled) {
    const authorized =
      isSignatureValid(rawBody, signature) || isBearerAuthorized(req);
    if (!authorized) {
      return NextResponse.json(
        { error: "Invalid access token" },
        { status: 401 },
      );
    }
  }

  let payload: OutrankPayload;
  try {
    payload = JSON.parse(rawBody) as OutrankPayload;
  } catch {
    return NextResponse.json({ error: "Invalid JSON" }, { status: 400 });
  }

  if (payload.event_type !== "publish_articles") {
    return NextResponse.json(
      { error: `Unsupported event_type: ${payload.event_type}` },
      { status: 400 },
    );
  }

  const articles = payload.data?.articles;
  if (!Array.isArray(articles) || articles.length === 0) {
    return NextResponse.json(
      { error: "No articles in payload" },
      { status: 400 },
    );
  }

  if (articles.length > MAX_ARTICLES) {
    return NextResponse.json(
      {
        error: `Too many articles: ${articles.length} (max ${MAX_ARTICLES})`,
      },
      { status: 413 },
    );
  }

  const processed: Array<{ id: string; slug: string }> = [];
  const skipped: Array<{ index: number; reason: string }> = [];
  // Track whether any skip was a TRANSIENT failure (e.g. a DB upsert error)
  // rather than a permanent validation error. Only transient failures justify
  // a non-2xx that asks the sender to retry; a payload whose articles are all
  // permanently invalid must not be retried forever.
  let hadTransientFailure = false;

  for (let i = 0; i < articles.length; i++) {
    const article = articles[i];
    const markdown = article?.content_markdown?.trim() ?? "";
    const html = article?.content_html?.trim() || markdownToHtml(markdown);
    const description = article?.meta_description ?? article?.description;

    if (!article?.id || !article.title?.trim() || (!markdown && !html)) {
      skipped.push({
        index: i,
        reason: "missing id/title/content",
      });
      continue;
    }

    const title = article.title.trim();
    const slugSource = article.slug?.trim() || title;
    const baseSlug = slugify(slugSource);
    if (!baseSlug) {
      skipped.push({ index: i, reason: "slug resolved to empty string" });
      continue;
    }

    // Validate published_at/created_at before either reaches the driver: an Invalid Date
    // makes node-postgres throw RangeError on serialization, which would
    // otherwise be swallowed as a silent skip.
    const { publishedAt, error: publishedAtError } = parsePublishedAt(article);
    if (publishedAtError) {
      skipped.push({
        index: i,
        reason: publishedAtError,
      });
      continue;
    }

    const tags = normalizeTags(article.tags);

    try {
      // Avoid a (locale, slug) unique violation when a different article has
      // already claimed this slug.
      const slug = await resolveUniqueSlug(baseSlug, article.id);

      const [row] = await db
        .insert(blogPosts)
        .values({
          externalId: article.id,
          source: "outrank",
          locale: LOCALE,
          slug,
          title,
          description: description?.trim() || null,
          contentMarkdown: markdown,
          contentHtml: html,
          imageUrl: article.image_url ?? null,
          imageAlt: article.image_alt ?? title,
          tags,
          // Falls back to the column default (now()) on first insert.
          ...(publishedAt ? { publishedAt } : {}),
        })
        .onConflictDoUpdate({
          target: blogPosts.externalId,
          set: {
            slug,
            title,
            description: description?.trim() || null,
            contentMarkdown: markdown,
            contentHtml: html,
            imageUrl: article.image_url ?? null,
            imageAlt: article.image_alt ?? title,
            tags,
            // Only overwrite publishedAt when the payload supplied a date, so
            // a re-publish without created_at does not reset ordering state.
            ...(publishedAt ? { publishedAt } : {}),
            updatedAt: new Date(),
          },
        })
        .returning({ id: blogPosts.id, slug: blogPosts.slug });

      processed.push({ id: row.id, slug: row.slug });

      revalidatePath(`/en/blog/${row.slug}`);
    } catch (err) {
      console.error("[add-blog-post] upsert failed", {
        externalId: article.id,
        err,
      });
      // A thrown upsert is treated as transient (DB outage, constraint, etc.)
      // so the delivery is retryable.
      hadTransientFailure = true;
      skipped.push({
        index: i,
        reason: err instanceof Error ? err.message : "upsert failed",
      });
    }
  }

  if (processed.length > 0) {
    revalidatePath("/en/blog");
    revalidatePath("/sitemap.xml");
  }

  // If nothing was persisted because of a TRANSIENT failure (a DB upsert
  // threw), return a non-2xx so the sender retries instead of treating the
  // silent failure as success. Validation-only skips are permanent client/data
  // errors — retrying them never succeeds — so those return 2xx below.
  if (processed.length === 0 && hadTransientFailure) {
    return NextResponse.json(
      {
        message: "Webhook failed: no articles were persisted",
        processed: 0,
        skipped: skipped.length,
        skippedDetails: skipped,
      },
      { status: 500 },
    );
  }

  return NextResponse.json({
    message: "Webhook processed successfully",
    processed: processed.length,
    skipped: skipped.length,
    articles: processed,
    ...(skipped.length > 0 ? { skippedDetails: skipped } : {}),
  });
}
