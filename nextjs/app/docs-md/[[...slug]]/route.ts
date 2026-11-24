import { loadDoc } from "@/lib/docs/content";
import { DOCS_ENTRIES, findDocBySlug, slugFromSegments } from "@/lib/docs/nav";

// The Markdown twin of every docs page. Reached through the rewrite of
// /docs/<slug>.md (config/docs.mjs), the URL Mintlify served it at; the
// canonical Link header points crawlers at the HTML page.
export const dynamic = "force-static";
export const dynamicParams = false;

type Context = { params: Promise<{ slug?: string[] }> };

export function generateStaticParams() {
  return DOCS_ENTRIES.map((entry) => ({
    slug: entry.slug === "index" ? [] : entry.slug.split("/"),
  }));
}

export async function GET(_request: Request, { params }: Context) {
  const entry = findDocBySlug(slugFromSegments((await params).slug));

  if (!entry) return new Response("Not found\n", { status: 404 });
  const doc = loadDoc(entry);

  return new Response(`# ${doc.title}\n\n${doc.description}\n\n${doc.body}`, {
    headers: {
      "content-type": "text/markdown; charset=utf-8",
      link: `<${doc.canonical}>; rel="canonical"`,
      "x-robots-tag": "noindex",
    },
  });
}
