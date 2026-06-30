import { notFound } from "next/navigation";
import { ArrowRight } from "lucide-react";

import { Link } from "@/src/i18n/navigation";
import { getBlogPosts } from "@/src/content/blog";

export const dynamic = "force-static";
export const revalidate = 60;

type Props = {
  params: Promise<{ locale: string }>;
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

export async function generateMetadata() {
  return {
    title: "Blog",
    description: "Guides, comparisons, and updates about HyperWhisper.",
  };
}

export default async function BlogPage({ params }: Props) {
  const { locale } = await params;
  if (locale !== "en") {
    notFound();
  }

  const posts = await getBlogPosts();
  const [featuredPost, ...otherPosts] = posts;

  return (
    <div className="w-full">
      <header className="mx-auto max-w-4xl text-center">
        <div className="mb-6 flex justify-center">
          <img
            alt="HyperWhisper"
            className="h-16 w-16 rounded-lg shadow-2xl shadow-purple-900/30"
            src="/icon/128.png"
          />
        </div>
        <p className="text-sm uppercase tracking-[0.3em] text-purple-300">
          HyperWhisper Blog
        </p>
        <h1 className="mt-4 bg-gradient-to-r from-white to-gray-400 bg-clip-text text-4xl font-bold text-transparent md:text-6xl">
          Voice AI guides for faster, cleaner writing
        </h1>
        <p className="mx-auto mt-5 max-w-2xl text-lg text-gray-400">
          Practical comparisons, workflow notes, and product updates for people
          using speech-to-text every day.
        </p>
      </header>

      {posts.length === 0 ? (
        <p className="mt-12 text-center text-gray-400">No posts yet.</p>
      ) : null}

      {featuredPost ? (
        <article className="mt-12 overflow-hidden rounded-lg border border-gray-800 bg-gray-900/60 shadow-2xl shadow-purple-950/20">
          <Link
            className="grid gap-0 md:grid-cols-[1.08fr_0.92fr]"
            href={`/blog/${featuredPost.slug}`}
          >
            <div className="flex min-h-[320px] flex-col justify-between p-7 md:p-9">
              <div>
                <p className="text-sm text-gray-500">
                  {formatDate(featuredPost.frontMatter.date)}
                </p>
                <h2 className="mt-4 text-3xl font-semibold leading-tight text-white md:text-4xl">
                  {featuredPost.frontMatter.title}
                </h2>
                <p className="mt-4 text-base leading-7 text-gray-300">
                  {featuredPost.frontMatter.description}
                </p>
              </div>

              <div className="mt-8 flex items-center gap-3 text-sm font-semibold text-purple-300">
                Read article
                <ArrowRight className="h-4 w-4" />
              </div>
            </div>

            <div className="relative min-h-[260px] border-t border-gray-800 bg-gray-950 md:border-l md:border-t-0">
              {featuredPost.frontMatter.image ? (
                <img
                  alt={
                    featuredPost.frontMatter.imageAlt ??
                    featuredPost.frontMatter.title
                  }
                  className="h-full w-full object-cover"
                  src={featuredPost.frontMatter.image}
                />
              ) : (
                <div className="flex h-full min-h-[260px] items-center justify-center bg-gradient-to-br from-gray-900 via-purple-950/50 to-cyan-950/50 p-8 text-center">
                  <span className="text-2xl font-semibold text-white">
                    HyperWhisper
                  </span>
                </div>
              )}
            </div>
          </Link>
        </article>
      ) : null}

      {otherPosts.length > 0 ? (
        <div className="mt-8 grid gap-5 md:grid-cols-2">
          {otherPosts.map((post) => (
            <article
              key={`${post.locale}-${post.slug}`}
              className="rounded-lg border border-gray-800 bg-gray-900/40 p-6 transition hover:border-purple-500/50 hover:bg-gray-900/70"
            >
              <p className="text-sm text-gray-500">
                {formatDate(post.frontMatter.date)}
              </p>
              <h2 className="mt-3 text-xl font-semibold leading-snug text-white">
                <Link
                  className="transition-colors hover:text-purple-300"
                  href={`/blog/${post.slug}`}
                >
                  {post.frontMatter.title}
                </Link>
              </h2>
              <p className="mt-3 line-clamp-3 text-sm leading-6 text-gray-300">
                {post.frontMatter.description}
              </p>
              {post.frontMatter.tags?.length ? (
                <div className="mt-5 flex flex-wrap gap-2">
                  {post.frontMatter.tags.slice(0, 3).map((tag) => (
                    <span
                      key={tag}
                      className="rounded-full border border-gray-700 px-3 py-1 text-xs text-gray-300"
                    >
                      {tag}
                    </span>
                  ))}
                </div>
              ) : null}
            </article>
          ))}
        </div>
      ) : null}
    </div>
  );
}
