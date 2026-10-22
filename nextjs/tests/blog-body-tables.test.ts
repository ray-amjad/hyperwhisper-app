// #2112: Outrank's comparison tables arrive as a bare <table>, and on a phone a
// wide one pushed the whole page sideways. getBlogPost() passes the body of the
// post it returns through scrollableBodyTables(), which wraps each table in its
// own focusable horizontal scroller. The transform is tested here; the real
// getBlogPost() wiring is tested in blog-html-sanitize.test.ts.
import assert from "node:assert/strict";
import test from "node:test";

import { scrollableBodyTables } from "../src/content/blog-html";
import { sanitizeBlogHtml } from "../src/content/sanitize-blog-html";

const SCROLLER =
  '<div class="my-[1.75em] overflow-x-auto [&>table]:my-0" role="region" aria-label="Table" tabindex="0">';

test("every table is wrapped in its own scroller, and nothing else moves", () => {
  const html = [
    "<p>Intro</p>",
    "<table><thead><tr><th>App</th></tr></thead><tbody><tr><td>A</td></tr></tbody></table>",
    "<p>Between</p>",
    '<TABLE class="x"><tr><td>B</td></tr></TABLE >',
    "<p>tables are great</p>",
  ].join("\n");

  assert.equal(
    scrollableBodyTables(html),
    [
      "<p>Intro</p>",
      `${SCROLLER}<table><thead><tr><th>App</th></tr></thead><tbody><tr><td>A</td></tr></tbody></table></div>`,
      "<p>Between</p>",
      `${SCROLLER}<TABLE class="x"><tr><td>B</td></tr></TABLE ></div>`,
      "<p>tables are great</p>",
    ].join("\n"),
  );
});

test("HTML with no table is returned unchanged", () => {
  const html =
    "<p>No tables</p><pre>&lt;table&gt;</pre><tablet>not one</tablet>";
  assert.equal(scrollableBodyTables(html), html);
});

test("a sanitised body keeps its tables, so the wrapper has something to wrap", () => {
  const html = sanitizeBlogHtml(
    "<table><thead><tr><th>App</th></tr></thead><tbody><tr><td>A</td></tr></tbody></table>",
  );
  assert.match(
    scrollableBodyTables(html),
    /^<div class="[^"]*overflow-x-auto[^"]*"[^>]*><table>.*<\/table><\/div>$/,
  );
});
