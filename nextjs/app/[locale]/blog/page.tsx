import { notFound } from "next/navigation";
import { ArrowRight } from "lucide-react";

import { Link } from "@/src/i18n/navigation";
import { getBlogPosts } from "@/src/content/blog";
import { formatLongUTCDate } from "@/lib/format-date";
import { englishStaticPageMetadata } from "@/lib/site-metadata";
import { WRAP } from "@/components/landing/dusk";
import { GLASS, LABEL, SECTION } from "@/components/landing/dusk-pages";
import { DuskPage, PageHero, SKIES } from "@/components/landing/DuskPage";

export const dynamic = "force-static";
export const revalidate = 60;

type Props = {
  params: Promise<{ locale: string }>;
};

type Post = Awaited<ReturnType<typeof getBlogPosts>>[number];

/**
 * Decorative art for a post with no front-matter image, taken in turn. They are
 * the homepage skies and four abstract renders, so no post ever borrows a
 * picture that could be read as its own.
 */
const art = (name: string) => ({
  src: `/landing/art-${name}-800.webp`,
  srcSet: `/landing/art-${name}-480.webp 480w, /landing/art-${name}-800.webp 800w`,
});

const ART = [
  SKIES.dusk,
  SKIES.night,
  art("orb"),
  art("glass"),
  art("shield"),
  art("rocket"),
];

const CARD =
  "group block h-full overflow-hidden text-start transition-colors duration-200 hover:border-[rgba(196,181,253,.28)] hover:bg-white/[.06] focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--dusk-lav)] motion-reduce:transition-none";

/**
 * The card image. The box sets the size (an aspect ratio, or the featured
 * card's row height), so a late image never moves the text under it.
 */
function PostArt({
  post,
  index,
  sizes,
  eager = false,
}: {
  post: Post;
  index: number;
  sizes: string;
  eager?: boolean;
}) {
  const loading = eager ? "eager" : "lazy";

  if (post.frontMatter.image) {
    return (
      <img
        alt={post.frontMatter.imageAlt || post.frontMatter.title}
        className="h-full w-full object-cover"
        decoding="async"
        loading={loading}
        src={post.frontMatter.image}
      />
    );
  }
  const { src, srcSet } = ART[index % ART.length];

  return (
    <img
      alt=""
      aria-hidden="true"
      className="h-full w-full object-cover"
      decoding="async"
      loading={loading}
      sizes={sizes}
      src={src}
      srcSet={srcSet}
    />
  );
}

function PostMeta({ post }: { post: Post }) {
  const tag = post.frontMatter.tags?.[0];

  return (
    <p className={LABEL}>
      {formatLongUTCDate(post.frontMatter.date)}
      {tag ? ` · ${tag}` : null}
    </p>
  );
}

// #815: the English-only check runs here too. A notFound() thrown only from the
// page left this English metadata (and the layout's "index, follow") in the RSC
// payload, which is what the browser shows on /de/blog. Thrown here, Next
// resolves the [locale] 404's metadata instead.
export async function generateMetadata({ params }: Props) {
  const { locale } = await params;

  if (locale !== "en") {
    notFound();
  }

  return englishStaticPageMetadata({
    path: "/blog",
    title: "Blog",
    description: "Guides, comparisons, and updates about HyperWhisper.",
  });
}

export default async function BlogPage({ params }: Props) {
  const { locale } = await params;

  if (locale !== "en") {
    notFound();
  }

  const posts = await getBlogPosts();
  const [featuredPost, ...otherPosts] = posts;

  return (
    <DuskPage>
      <PageHero
        eyebrow="HyperWhisper Blog"
        lead="Practical comparisons, workflow notes, and product updates for people using speech-to-text every day."
        title="Voice AI guides for faster, cleaner writing"
      />

      <section className={SECTION}>
        <div className={WRAP}>
          {posts.length === 0 ? (
            <p className="text-center text-[var(--dusk-mute)]">No posts yet.</p>
          ) : null}

          {featuredPost ? (
            <article>
              <Link
                className={`${GLASS} ${CARD} grid md:grid-cols-[1.1fr_1fr]`}
                href={`/blog/${featuredPost.slug}`}
              >
                <div className="relative aspect-[16/9] overflow-hidden bg-[#0b0d1c] md:aspect-auto md:min-h-[340px]">
                  <div className="absolute inset-0">
                    <PostArt
                      eager
                      index={0}
                      post={featuredPost}
                      sizes="(min-width: 768px) 640px, 100vw"
                    />
                  </div>
                </div>
                <div className="flex flex-col p-6 md:p-10">
                  <PostMeta post={featuredPost} />
                  <h2 className="mt-3 text-2xl font-semibold leading-[1.15] tracking-[-0.035em] text-white [overflow-wrap:anywhere] md:text-[34px]">
                    {featuredPost.frontMatter.title}
                  </h2>
                  <p className="mt-3 leading-[1.55] text-[var(--dusk-mute)]">
                    {featuredPost.frontMatter.description}
                  </p>
                  <span className="mt-5 inline-flex items-center gap-2 text-[14.5px] text-[var(--dusk-lav)] md:mt-auto md:pt-5">
                    Read article
                    <ArrowRight
                      aria-hidden="true"
                      className="size-4 transition-transform duration-200 group-hover:translate-x-0.5 motion-reduce:transition-none rtl:-scale-x-100"
                    />
                  </span>
                </div>
              </Link>
            </article>
          ) : null}

          {otherPosts.length > 0 ? (
            <div className="mt-4 grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
              {otherPosts.map((post, i) => (
                <article key={`${post.locale}-${post.slug}`}>
                  <Link
                    className={`${GLASS} ${CARD} pb-[22px]`}
                    href={`/blog/${post.slug}`}
                  >
                    <div className="relative mb-[18px] aspect-[16/9] overflow-hidden bg-[#0b0d1c]">
                      <div className="absolute inset-0">
                        <PostArt
                          index={i + 1}
                          post={post}
                          sizes="(min-width: 1024px) 380px, (min-width: 640px) 50vw, 100vw"
                        />
                      </div>
                    </div>
                    <div className="px-[22px]">
                      <p className={LABEL}>
                        {formatLongUTCDate(post.frontMatter.date)}
                      </p>
                      <h2 className="mt-2 text-lg font-semibold leading-[1.35] text-white [overflow-wrap:anywhere] transition-colors group-hover:text-[var(--dusk-lav)] motion-reduce:transition-none">
                        {post.frontMatter.title}
                      </h2>
                      <p className="mt-2 line-clamp-3 text-sm leading-6 text-[var(--dusk-mute)]">
                        {post.frontMatter.description}
                      </p>
                      {post.frontMatter.tags?.length ? (
                        <div className="mt-4 flex flex-wrap gap-2">
                          {post.frontMatter.tags.slice(0, 3).map((tag) => (
                            <span
                              key={tag}
                              className="rounded-full border border-[var(--dusk-line)] px-3 py-1 text-xs text-[#dcdcf0]"
                            >
                              {tag}
                            </span>
                          ))}
                        </div>
                      ) : null}
                    </div>
                  </Link>
                </article>
              ))}
            </div>
          ) : null}
        </div>
      </section>
    </DuskPage>
  );
}
