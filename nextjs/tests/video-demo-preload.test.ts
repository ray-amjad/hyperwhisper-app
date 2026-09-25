/**
 * #759 — `preload=true` on the home page's Bunny Stream embed downloaded
 * ~640 KB of HLS segments and started a never-ending RUM edge probe on every
 * home page view, before any click. PR #1020 flipped it to `preload=false`.
 *
 * Nothing else in the repo pins that flag: a later re-paste of Bunny's own
 * embed snippet (whose default is `preload=true`) would silently restore the
 * eager download, and `npm test` would stay green. This scrapes the iframe
 * `src` out of the component source and parses it as a real URL, so it fails
 * the moment the query string regresses.
 */
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { fileURLToPath } from "node:url";

const source = readFileSync(
  fileURLToPath(
    new URL("../components/landing/VideoDemo.tsx", import.meta.url),
  ),
  "utf8",
);

const matches = Array.from(
  source.matchAll(/src="(https:\/\/iframe\.mediadelivery\.net\/embed\/[^"]*)"/g),
);

test("the home demo embed pins preload=false and autoplay=false", () => {
  assert.equal(
    matches.length,
    1,
    `expected exactly one mediadelivery.net embed src, found ${matches.length}`,
  );

  const url = new URL(matches[0][1]);
  assert.equal(
    url.searchParams.get("preload"),
    "false",
    "preload must stay false — true re-downloads ~640 KB of HLS segments and starts a RUM edge probe before any click (#759)",
  );
  assert.equal(url.searchParams.get("autoplay"), "false");
});
