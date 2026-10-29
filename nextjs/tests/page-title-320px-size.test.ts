/**
 * #1965: the `#features` h2 and the support h1 are painted with
 * `bg-clip-text text-transparent`. At 36px (`text-4xl`) a single long
 * translated word is wider than the 272px column of a 320px phone, so its
 * letters are cut off and the page scrolls sideways (bg, et, ru, is/support).
 * Below `sm` (640px) these headings are 30px (`text-3xl`); 640px and up is
 * unchanged. #2714 brought the four other page titles with the same gradient
 * class string (pricing, FAQ, /credits, /older-versions) to the same sizes.
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
  { file: "app/[locale]/support/page.tsx", tag: "h1", bleed: false },
  // #2714: the siblings PR #2710 left out.
  { file: "components/credits/CreditsPurchase.tsx", tag: "h1", bleed: false },
  { file: "app/[locale]/older-versions/page.tsx", tag: "h1", bleed: false },
];

const read = (file: string) =>
  readFileSync(fileURLToPath(new URL(`../${file}`, import.meta.url)), "utf8");

/*
  The homepage section titles (features, pricing, FAQ and the rest) share one
  class string, `H2` in components/landing/dusk.ts. They are solid ink now,
  not a clipped gradient, so no glyph can lose its stroke at the box edge and
  the old `-mx-2 px-2` bleed is not needed; `[overflow-wrap:anywhere]` still
  breaks a word wider than a 320px phone's column instead of scrolling.
*/
test("the homepage H2 is text-3xl below sm, text-4xl from sm, text-5xl from md", () => {
  const match = read("components/landing/dusk.ts").match(
    /export const H2 =\s*"([^"]*)"/,
  );

  assert.ok(match, "no H2 constant in components/landing/dusk.ts");
  const classes = match[1].split(/\s+/);

  assert.deepEqual(
    classes.filter((c) => /^(\w+:)?text-(xs|sm|base|lg|\d?xl)$/.test(c)),
    ["text-3xl", "sm:text-4xl", "md:text-5xl"],
  );
  assert.ok(classes.includes("[overflow-wrap:anywhere]"));
});

for (const file of [
  "components/landing/FeaturesGrid.tsx",
  "components/landing/PricingSection.tsx",
  "components/landing/FAQSection.tsx",
]) {
  test(`${file} titles its section with the shared H2`, () => {
    assert.match(read(file), /<h2 className=\{`\$\{H2\}[^`]*`\}>/);
  });
}

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
