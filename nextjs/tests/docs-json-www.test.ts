import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { join } from "node:path";
import test from "node:test";

const ROOT = fileURLToPath(new URL("../..", import.meta.url));
const docs = JSON.parse(
  readFileSync(join(ROOT, "mintlify-help/docs.json"), "utf8"),
) as {
  seo?: {
    metatags?: { canonical?: string };
    organization?: { url?: string };
  };
  navbar?: { primary?: { href?: string } };
  footer?: { links?: Array<{ items?: Array<{ href?: string }> }> };
};

test("docs.json names www as the canonical origin", () => {
  assert.equal(docs.seo?.metatags?.canonical, "https://www.hyperwhisper.com");
  assert.equal(docs.seo?.organization?.url, "https://www.hyperwhisper.com");
});

test("docs.json outbound links use www, not the redirecting apex", () => {
  const hrefs = [
    docs.navbar?.primary?.href,
    ...(docs.footer?.links ?? []).flatMap((column) =>
      (column.items ?? []).map((item) => item.href),
    ),
  ].filter((href): href is string => typeof href === "string");

  assert.ok(hrefs.length > 0);
  for (const href of hrefs) {
    assert.doesNotMatch(href, /^https:\/\/hyperwhisper\.com(\/|$)/, href);
  }
  assert.ok(
    hrefs.some((href) => href.includes("/en/download")),
    "no download landing link",
  );
});
