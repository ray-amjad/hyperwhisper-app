import { MetadataRoute } from "next";
import { getAllBlogPosts } from "@/src/content/blog";
import {
  buildAlternateLanguageMap,
  locales,
} from "@/src/i18n/locales";

export default async function sitemap(): Promise<MetadataRoute.Sitemap> {
  const baseUrl = "https://hyperwhisper.com";

  // Each path must be app/[locale]<path>/page.tsx and render on every locale;
  // tests/sitemap-routes.test.ts fails the build when one does not.
  const pages = [
    "", // home page
    "/download",
    "/support",
    "/credits",
    "/open-source",
    "/older-versions",
    "/legal/privacy-policy",
    "/legal/terms-of-service",
    "/legal/refund-policy",
  ];

  const sitemap: MetadataRoute.Sitemap = [];

  // Generate entries for each page in each locale
  pages.forEach((page) => {
    locales.forEach((locale) => {
      sitemap.push({
        url: `${baseUrl}/${locale}${page}`,
        lastModified: new Date(),
        changeFrequency: "weekly",
        priority: page === "" ? 1 : 0.8,
        alternates: {
          languages: buildAlternateLanguageMap(baseUrl, page),
        },
      });
    });
  });

  // English only, like blog posts: these pages 404 on every other locale, so
  // they must stay out of the 40-locale loop above and carry no `alternates`.
  sitemap.push({
    url: `${baseUrl}/en/blog`,
    lastModified: new Date(),
    changeFrequency: "weekly",
    priority: 0.8,
  });
  sitemap.push({
    url: `${baseUrl}/en/choosing-a-model`,
    lastModified: new Date(),
    changeFrequency: "weekly",
    priority: 0.8,
  });
  sitemap.push({
    url: `${baseUrl}/en/latency`,
    lastModified: new Date(),
    changeFrequency: "daily",
    priority: 0.6,
  });

  // A vercel.json rewrite with no locale form, so it is listed once, unprefixed.
  sitemap.push({
    url: `${baseUrl}/docs`,
    lastModified: new Date(),
    changeFrequency: "weekly",
    priority: 0.8,
  });

  const blogPosts = await getAllBlogPosts();
  blogPosts.forEach((post) => {
    if (post.locale !== "en") return;
    const url = `${baseUrl}/en/blog/${post.slug}`;
    const parsedDate = post.frontMatter.date
      ? new Date(post.frontMatter.date)
      : null;
    const lastModified =
      parsedDate && !Number.isNaN(parsedDate.getTime())
        ? parsedDate
        : new Date();

    sitemap.push({
      url,
      lastModified,
      changeFrequency: "monthly",
      priority: 0.7,
    });
  });

  return sitemap;
}
