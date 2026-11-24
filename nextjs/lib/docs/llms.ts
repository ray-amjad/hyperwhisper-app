import "server-only";

import { loadAllDocs } from "./content";
import { DOCS_NAV, type DocNode } from "./nav";

import { SITE_URL } from "@/lib/site-metadata";

// /docs/llms.txt and /docs/llms-full.txt, which Mintlify served: an index of
// every page (linking its Markdown twin) and the whole set in one file.

const INTRO = `# HyperWhisper

> HyperWhisper is a speech-to-text app for macOS, Windows and Linux: on-device
> and cloud transcription, AI post-processing, custom vocabulary and a local
> API. These are its docs. Every page is also available as Markdown by adding
> \`.md\` to its URL, and the whole set is at ${SITE_URL}/docs/llms-full.txt`;

export function llmsTxt(): string {
  const docs = new Map(loadAllDocs().map((doc) => [doc.entry.slug, doc]));
  const lines: string[] = [INTRO, ""];

  const walk = (nodes: DocNode[], depth: number) => {
    for (const node of nodes) {
      if (node.kind === "group") {
        lines.push("", `${"#".repeat(depth)} ${node.title}`, "");
        walk(node.items, depth + 1);
      } else {
        const doc = docs.get(node.slug);

        lines.push(
          `- [${node.title}](${SITE_URL}${node.href}.md): ${doc?.description ?? ""}`,
        );
      }
    }
  };

  walk(DOCS_NAV, 2);

  return `${lines
    .join("\n")
    .replace(/\n{3,}/g, "\n\n")
    .trim()}\n`;
}

export function llmsFullTxt(): string {
  return loadAllDocs()
    .map(
      (doc) =>
        `# ${doc.title}\n\nSource: ${doc.canonical}\n\n${doc.description}\n\n${doc.body.trimEnd()}\n`,
    )
    .join("\n---\n\n");
}
