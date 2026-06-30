import { notFound } from "next/navigation";
import { ArrowLeft } from "lucide-react";

import { Link } from "@/src/i18n/navigation";
import { getBlogPost, getBlogPosts } from "@/src/content/blog";

const BASE_URL = "https://hyperwhisper.com";

export const dynamic = "force-static";
export const dynamicParams = true;
export const revalidate = 60;

type Props = {
  params: Promise<{ locale: string; slug: string }>;
};

function formatDate(dateString: string) {
  const date = new Date(dateString);
  if (Number.isNaN(date.getTime())) return dateString;
  return new Intl.DateTimeFormat("en", {
    year: "numeric",
    month: "long",
    day: "numeric",
    timeZone: "UTC",
  }).format(date);
}

function toAbsoluteUrl(url: string) {
  if (url.startsWith("http://") || url.startsWith("https://")) return url;
  return `${BASE_URL}${url.startsWith("/") ? "" : "/"}${url}`;
}

export async function generateMetadata({ params }: Props) {
  const { locale, slug } = await params;
  if (locale !== "en") return {};
  const post = await getBlogPost(slug);

  if (!post) return {};

  const title = post.frontMatter.title;
  const description = post.frontMatter.description;
  const canonical = `${BASE_URL}/en/blog/${post.slug}`;
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
      images: image ? [image] : undefined,
    },
    twitter: {
      card: "summary_large_image",
      title,
      description,
      images: image ? [image.url] : undefined,
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

  const publishedDate = formatDate(post.frontMatter.date);
  const canonicalUrl = `${BASE_URL}/en/blog/${post.slug}`;
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
        url: `${BASE_URL}/icon/512.png`,
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
    <article className="mx-auto w-full max-w-4xl">
      <script
        type="application/ld+json"
        dangerouslySetInnerHTML={{ __html: JSON.stringify(jsonLd) }}
      />
      <Link
        className="mb-8 inline-flex items-center gap-2 text-sm font-medium text-gray-400 transition-colors hover:text-white"
        href="/blog"
      >
        <ArrowLeft className="h-4 w-4" />
        Blog
      </Link>

      <div className="space-y-5 text-center">
        <p className="text-sm uppercase tracking-[0.3em] text-purple-300">
          HyperWhisper Blog
        </p>
        <h1 className="mx-auto max-w-3xl text-4xl font-semibold leading-tight text-white md:text-5xl">
          {post.frontMatter.title}
        </h1>
        <div className="flex flex-wrap items-center justify-center gap-3 text-sm text-gray-400">
          <span>{publishedDate}</span>
          {post.frontMatter.tags?.slice(0, 3).map((tag) => (
            <span
              key={tag}
              className="rounded-full border border-gray-700 px-3 py-1 text-xs text-gray-300"
            >
              {tag}
            </span>
          ))}
        </div>
        {post.frontMatter.description ? (
          <p className="mx-auto max-w-2xl text-lg leading-8 text-gray-300">
            {post.frontMatter.description}
          </p>
        ) : null}
      </div>

      {post.frontMatter.image ? (
        <div className="mt-10 overflow-hidden rounded-lg border border-gray-800 bg-gray-900">
          <img
            alt={post.frontMatter.imageAlt ?? post.frontMatter.title}
            className="max-h-[480px] w-full object-cover"
            src={post.frontMatter.image}
          />
        </div>
      ) : null}

      <div
        className="prose prose-invert mx-auto mt-10 max-w-3xl prose-headings:text-white prose-a:text-purple-300 prose-a:no-underline hover:prose-a:text-purple-200 prose-strong:text-white"
        dangerouslySetInnerHTML={{ __html: post.html }}
      />
    </article>
  );
}
