import { notFound } from "next/navigation";
import { ArrowLeft } from "lucide-react";

import { Link } from "@/src/i18n/navigation";
import { getBlogPost, getBlogPosts } from "@/src/content/blog";
import { formatLongUTCDate } from "@/lib/format-date";
import { PROSE_TEXT_LINK } from "@/components/text-link";
import { serializeJsonLd } from "@/lib/security/json-ld";
import { WRAP } from "@/components/landing/dusk";
import { GLASS, LINK_LAV, SECTION } from "@/components/landing/dusk-pages";
import { DuskPage, PageHero } from "@/components/landing/DuskPage";
import {
  OG_IMAGES,
  SITE_NAME,
  SITE_URL,
  TWITTER_CARD,
  TWITTER_CREATOR,
  TWITTER_IMAGES,
} from "@/lib/site-metadata";

export const dynamic = "force-static";
export const dynamicParams = true;
export const revalidate = 60;

type Props = {
  params: Promise<{ locale: string; slug: string }>;
};

function toAbsoluteUrl(url: string) {
  if (url.startsWith("http://") || url.startsWith("https://")) return url;

  return `${SITE_URL}${url.startsWith("/") ? "" : "/"}${url}`;
}

export async function generateMetadata({ params }: Props) {
  const { locale, slug } = await params;

  // #815: throw the page's notFound() here too. Returning {} left the layout's
  // home-page title and "index, follow" in the RSC payload the browser renders.
  if (locale !== "en") notFound();
  const post = await getBlogPost(slug);

  if (!post) notFound();

  const title = post.frontMatter.title;
  const description = post.frontMatter.description;
  const canonical = `${SITE_URL}/en/blog/${post.slug}`;
  const image = post.frontMatter.image
    ? {
        url: toAbsoluteUrl(post.frontMatter.image),
        alt: post.frontMatter.imageAlt || title,
      }
    : null;

  return {
    title,
    description,
    alternates: {
      canonical,
    },
    openGraph: {
      type: "article",
      locale: "en_US",
      url: canonical,
      title,
      description,
      // This object replaces the layout's whole `openGraph` (shallow merge),
      // so it restates the site name (#1316).
      siteName: SITE_NAME,
      // A post with no front-matter image falls back to the site card (#844):
      // leaving it undefined sent no og:image at all under a
      // summary_large_image card.
      images: image ? [image] : OG_IMAGES,
    },
    // Same for `twitter`: the layout's creator is dropped unless restated (#1316).
    twitter: {
      card: TWITTER_CARD,
      title,
      description,
      creator: TWITTER_CREATOR,
      images: image ? [image.url] : TWITTER_IMAGES,
    },
  };
}

export async function generateStaticParams() {
  const posts = await getBlogPosts();

  return posts.map((post) => ({ locale: "en", slug: post.slug }));
}

export default async function BlogPostPage({ params }: Props) {
  const { locale, slug } = await params;

  if (locale !== "en") {
    notFound();
  }

  const post = await getBlogPost(slug);

  if (!post) {
    notFound();
  }

  const publishedDate = formatLongUTCDate(post.frontMatter.date);
  const canonicalUrl = `${SITE_URL}/en/blog/${post.slug}`;
  const jsonLd = {
    "@context": "https://schema.org",
    "@type": "Article",
    headline: post.frontMatter.title,
    description: post.frontMatter.description,
    datePublished: post.frontMatter.date,
    dateModified: post.frontMatter.date,
    author: {
      "@type": "Organization",
      name: "HyperWhisper",
    },
    publisher: {
      "@type": "Organization",
      name: "HyperWhisper",
      logo: {
        "@type": "ImageObject",
        url: `${SITE_URL}/icon/512.png`,
      },
    },
    mainEntityOfPage: {
      "@type": "WebPage",
      "@id": canonicalUrl,
    },
    image: post.frontMatter.image
      ? [toAbsoluteUrl(post.frontMatter.image)]
      : undefined,
  };

  return (
    <DuskPage>
      <article>
        <script
          dangerouslySetInnerHTML={{ __html: serializeJsonLd(jsonLd) }}
          type="application/ld+json"
        />
        <PageHero
          eyebrow="HyperWhisper Blog"
          lead={post.frontMatter.description || undefined}
          title={post.frontMatter.title}
        >
          <div className="flex flex-wrap items-center justify-center gap-3 text-sm text-white/[.8]">
            <time dateTime={post.frontMatter.date}>{publishedDate}</time>
            {post.frontMatter.tags?.slice(0, 3).map((tag) => (
              <span
                key={tag}
                className="rounded-full border border-white/[.16] bg-[rgba(10,10,24,.4)] px-3 py-1 text-xs text-white/[.85] backdrop-blur-[10px]"
              >
                {tag}
              </span>
            ))}
          </div>
        </PageHero>

        <div className={SECTION}>
          <div className={WRAP}>
            <div className="mx-auto max-w-[780px]">
              <Link
                className={`${LINK_LAV} inline-flex items-center gap-2`}
                href="/blog"
              >
                <ArrowLeft
                  aria-hidden="true"
                  className="size-4 rtl:-scale-x-100"
                />
                Blog
              </Link>

              {post.frontMatter.image ? (
                <div className={`${GLASS} mt-6 overflow-hidden`}>
                  <img
                    alt={post.frontMatter.imageAlt ?? post.frontMatter.title}
                    className="max-h-[480px] w-full object-cover"
                    src={post.frontMatter.image}
                  />
                </div>
              ) : null}

              <div
                dangerouslySetInnerHTML={{ __html: post.html }}
                className={`prose prose-invert mt-10 max-w-none text-[#c9cbe0] prose-p:leading-[1.75] prose-headings:tracking-[-0.02em] prose-headings:text-white prose-strong:text-white prose-li:marker:text-[var(--dusk-dim)] prose-hr:border-[var(--dusk-line)] prose-blockquote:border-[rgba(196,181,253,.35)] prose-blockquote:text-[var(--dusk-mute)] prose-pre:border prose-pre:border-[var(--dusk-line)] prose-pre:bg-[#070814] prose-th:text-white prose-td:border-[var(--dusk-line)] [&_iframe]:aspect-video [&_img]:outline-1 [&_img]:-outline-offset-1 [&_img]:outline-white/10 [&_:not(pre)>code]:break-words ${PROSE_TEXT_LINK}`}
              />
            </div>
          </div>
        </div>
      </article>
    </DuskPage>
  );
}
