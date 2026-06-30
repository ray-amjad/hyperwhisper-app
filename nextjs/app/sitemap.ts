import { MetadataRoute } from "next";
import { getAllBlogPosts } from "@/src/content/blog";
import {
  buildAlternateLanguageMap,
  locales,
} from "@/src/i18n/locales";

export default async function sitemap(): Promise<MetadataRoute.Sitemap> {
  const baseUrl = "https://hyperwhisper.com";

  // Define all your pages here
  const pages = [
    "", // home page
    "/about",
    "/blog",
    "/pricing",
    "/docs",
    "/support",
    "/legal/privacy",
    "/legal/terms",
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
