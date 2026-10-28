/**
 * #1965: the `#features` h2 and the support h1 are painted with
 * `bg-clip-text text-transparent`. At 36px (`text-4xl`) a single long
 * translated word is wider than the 272px column of a 320px phone, so its
 * letters are cut off and the page scrolls sideways (bg, et, ru, is/support).
 * Below `sm` (640px) these headings are 30px (`text-3xl`); 640px and up is
 * unchanged.
 *
 * At 30px the Bulgarian "професионалисти" is still 277px, 5px wider than the
 * 272px box. The gradient is only painted inside the box, so the last letter
 * lost its right stroke. `-mx-2 px-2` on the features h2 gives the glyphs 8px
 * of painted padding on each side without moving the centred text.
 *
 * The heading's className literal is scraped from the source, the same way
 * `tests/card-body-rtl-alignment.test.ts` scrapes its sites.
 */
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { test } from "node:test";

const sites = [
  { file: "components/landing/FeaturesGrid.tsx", tag: "h2", bleed: true },
  { file: "app/[locale]/support/page.tsx", tag: "h1", bleed: false },
];

for (const { file, tag, bleed } of sites) {
  test(`${file} <${tag}> is text-3xl below sm, text-4xl from sm, text-5xl from md`, () => {
    const source = readFileSync(
      fileURLToPath(new URL(`../${file}`, import.meta.url)),
      "utf8",
    );
    const match = source.match(
      new RegExp(`<${tag} className="([^"]*bg-clip-text[^"]*)"`),
    );
    assert.ok(match, `no gradient <${tag}> found in ${file}`);
    const classes = match[1].split(/\s+/);

    const sizes = classes.filter((c) =>
      /^(\w+:)?text-(xs|sm|base|lg|\d?xl)$/.test(c),
    );
    assert.deepEqual(sizes, ["text-3xl", "sm:text-4xl", "md:text-5xl"]);
    if (bleed) {
      assert.ok(classes.includes("-mx-2") && classes.includes("px-2"));
    }
  });
}
