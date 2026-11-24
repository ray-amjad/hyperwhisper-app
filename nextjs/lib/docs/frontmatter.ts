import YAML from "yaml";

// Every docs .mdx opens with a YAML header holding `title` and `description`.
// The page reads them for its <title> and meta description, /docs/llms.txt for
// its index, and the tests (with fs) to check the nav against the files.
// remark-frontmatter strips the same header from the rendered page.

export type DocFrontmatter = { title: string; description: string };

const HEADER = /^---\r?\n([\s\S]*?)\r?\n---\r?\n?/;

export function splitFrontmatter(
  raw: string,
  file = "docs page",
): {
  data: DocFrontmatter;
  body: string;
} {
  const match = raw.match(HEADER);

  if (!match) throw new Error(`${file}: no YAML frontmatter`);
  const data = YAML.parse(match[1]) as Partial<DocFrontmatter> | null;

  if (!data?.title || !data.description) {
    throw new Error(`${file}: frontmatter needs a title and a description`);
  }

  return {
    data: { title: String(data.title), description: String(data.description) },
    body: raw.slice(match[0].length).replace(/^\s+/, ""),
  };
}
