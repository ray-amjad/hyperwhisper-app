/**
 * #844: the share card every page declares is a real 1200x630 file under
 * 400 KB in `public/`. The metadata tests check the URL; this checks the file
 * behind it, so a swapped or bloated image fails here, not on a crawler.
 */

import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { test } from "node:test";

import { OG_IMAGES, SITE_URL } from "@/lib/site-metadata";

test("the declared og image is a 1200x630 PNG under 400 KB in public/", () => {
  assert.equal(OG_IMAGES.length, 1);
  const [image] = OG_IMAGES;
  assert.ok(image.url.startsWith(`${SITE_URL}/`), image.url);

  const file = readFileSync(
    join(process.cwd(), "public", image.url.slice(SITE_URL.length)),
  );
  // PNG signature, then the IHDR chunk: width and height are big-endian u32s.
  assert.equal(file.subarray(0, 8).toString("hex"), "89504e470d0a1a0a");
  assert.equal(file.subarray(12, 16).toString("ascii"), "IHDR");
  assert.equal(file.readUInt32BE(16), 1200);
  assert.equal(file.readUInt32BE(20), 630);
  assert.equal(image.width, 1200);
  assert.equal(image.height, 630);
  assert.ok(file.length < 400_000, `${file.length} bytes`);
});
