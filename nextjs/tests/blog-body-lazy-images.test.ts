// #919: a post body is stored as raw Outrank HTML, so its <img> tags loaded
// eagerly and a phone fetched every full-size picture on page load. getBlogPost()
// passes every stored body through lazyLoadBodyImages(). src/content/blog.ts
// imports "server-only", which resolves only inside a Next build, so the
// transform is tested here and the page itself in the PR's verify run.
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

import { lazyLoadBodyImages } from "../src/content/blog-html";

const storedHtml = [
  "<p>Intro</p>",
  '<img src="https://cdnimg.co/a/schedule.jpg" alt="A flow chart, loading=eager in prose">',
  "<IMG SRC='https://cdnimg.co/a/checklist.jpg' ALT='checklist'/>",
  '<img src="https://cdnimg.co/a/kept.jpg" loading="eager" alt="author choice">',
  "<imgur>not an image</imgur>",
].join("\n");

const imgTags = (html: string) => html.match(/<img\b[^>]*>/gi) ?? [];

test("every stored body image comes back lazy and async", () => {
  const html = lazyLoadBodyImages(storedHtml);
  const [schedule, checklist, kept, ...rest] = imgTags(html);
  assert.deepEqual(rest, []);
  assert.equal(
    schedule,
    '<img src="https://cdnimg.co/a/schedule.jpg" alt="A flow chart, loading=eager in prose" loading="lazy" decoding="async">',
  );
  assert.equal(
    checklist,
    "<img SRC='https://cdnimg.co/a/checklist.jpg' ALT='checklist' loading=\"lazy\" decoding=\"async\" />",
  );
  // An attribute the stored HTML already sets is the author's: leave it.
  assert.equal(
    kept,
    '<img src="https://cdnimg.co/a/kept.jpg" loading="eager" alt="author choice" decoding="async">',
  );
  assert.match(html, /<p>Intro<\/p>/);
  assert.match(html, /<imgur>not an image<\/imgur>/);
});

test("a trailing slash in an unquoted src stays in the URL", () => {
  assert.equal(
    lazyLoadBodyImages("<img src=https://cdnimg.co/a/>"),
    '<img src=https://cdnimg.co/a/ loading="lazy" decoding="async">',
  );
});

test("HTML with no image is returned unchanged", () => {
  const html = "<p>No pictures</p><pre>&lt;img src=x&gt;</pre>";
  assert.equal(lazyLoadBodyImages(html), html);
});

test("src/content/blog.ts sends the stored body through the transform", async () => {
  const source = await readFile(new URL("../src/content/blog.ts", import.meta.url), "utf8");
  assert.match(source, /html:\s*lazyLoadBodyImages\(row\.contentHtml\)/);
});
