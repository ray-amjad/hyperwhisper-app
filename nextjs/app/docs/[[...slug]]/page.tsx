import type { Metadata } from "next";

import Link from "next/link";
import { notFound } from "next/navigation";

import { EndpointPage } from "@/components/docs/EndpointPage";
import { docsMdxComponents } from "@/components/docs/mdx-components";
import { loadDoc, loadPageComponent, loadSpec } from "@/lib/docs/content";
import {
  DOCS_BASE,
  DOCS_ENTRIES,
  findDocBySlug,
  prevNext,
  slugFromSegments,
} from "@/lib/docs/nav";
import { listOperations } from "@/lib/docs/openapi";
import { serializeJsonLd } from "@/lib/security/json-ld";
import { OG_IMAGES, SITE_NAME, SITE_URL } from "@/lib/site-metadata";
import { defaultLocale } from "@/src/i18n/locales";

// Every docs page, rendered at build time: one per entry in lib/docs/nav.ts
// (an .mdx page or an openapi.yaml operation). Anything else under /docs is a
// 404 without rendering, so the Worker never compiles a page at request time.
export const dynamic = "force-static";
export const dynamicParams = false;

type Props = { params: Promise<{ slug?: string[] }> };

export function generateStaticParams() {
  return DOCS_ENTRIES.map((entry) => ({
    slug: entry.slug === "index" ? [] : entry.slug.split("/"),
  }));
}

async function entryFor(params: Props["params"]) {
  const { slug } = await params;
  const entry = findDocBySlug(slugFromSegments(slug));

  if (!entry) notFound();

  return entry;
}

export async function generateMetadata({ params }: Props): Promise<Metadata> {
  const doc = loadDoc(await entryFor(params));

  return {
    title: doc.title,
    description: doc.description,
    alternates: {
      canonical: doc.canonical,
      types: { "text/markdown": `${doc.entry.href}.md` },
    },
    openGraph: {
      type: "article",
      siteName: SITE_NAME,
      locale: "en_US",
      url: doc.canonical,
      title: `${doc.title} - ${SITE_NAME}`,
      description: doc.description,
      images: OG_IMAGES,
    },
  };
}

function breadcrumbJsonLd(items: { name: string; url: string }[]) {
  return {
    "@context": "https://schema.org",
    "@type": "BreadcrumbList",
    itemListElement: items.map((item, i) => ({
      "@type": "ListItem",
      position: i + 1,
      name: item.name,
      item: item.url,
    })),
  };
}

export default async function DocsPage({ params }: Props) {
  const entry = await entryFor(params);
  const doc = loadDoc(entry);
  const { prev, next } = prevNext(entry.slug);
  const crumbs = [
    { name: "Home", url: `${SITE_URL}/${defaultLocale}` },
    { name: "Docs", url: `${SITE_URL}${DOCS_BASE}` },
    ...(entry.slug === "index"
      ? []
      : [{ name: doc.title, url: doc.canonical }]),
  ];

  let content;

  if (entry.kind === "page") {
    const { default: Content } = await loadPageComponent(entry);

    content = <Content components={docsMdxComponents} />;
  } else {
    const spec = loadSpec();
    const operation = listOperations(spec).find((op) => op.slug === entry.slug);

    if (!operation) notFound();
    content = <EndpointPage operation={operation} spec={spec} />;
  }

  return (
    <>
      <script
        dangerouslySetInnerHTML={{
          __html: serializeJsonLd(breadcrumbJsonLd(crumbs)),
        }}
        type="application/ld+json"
      />
      <article className="docs-article">
        <header className="docs-header">
          <nav aria-label="Breadcrumb" className="docs-crumbs">
            <ol>
              <li>
                <Link href={DOCS_BASE}>Docs</Link>
              </li>
              {entry.groups.map((group) => (
                <li key={group}>{group}</li>
              ))}
            </ol>
          </nav>
          <h1>{doc.title}</h1>
          {/* An endpoint's description is its body's first paragraph. */}
          {entry.kind === "page" ? (
            <p className="docs-lead">{doc.description}</p>
          ) : null}
        </header>
        <div className="docs-body">{content}</div>
      </article>
      {prev || next ? (
        <nav aria-label="Previous and next page" className="docs-pager">
          {prev ? (
            <Link className="docs-pager-prev" href={prev.href}>
              <span className="docs-pager-label">Previous</span>
              <span>{prev.title}</span>
            </Link>
          ) : (
            <span />
          )}
          {next ? (
            <Link className="docs-pager-next" href={next.href}>
              <span className="docs-pager-label">Next</span>
              <span>{next.title}</span>
            </Link>
          ) : (
            <span />
          )}
        </nav>
      ) : null}
    </>
  );
}
