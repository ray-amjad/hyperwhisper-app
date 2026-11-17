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

/*
  The support, /credits and /older-versions titles now render in the shared
  sky band (`PageHero` in components/landing/DuskPage.tsx), whose h1 takes the
  `PAGE_H1` class string from components/landing/dusk-pages.ts. It is solid
  white, not a clipped gradient, so the sizes and the overflow-wrap are pinned
  on that one constant instead of on three literals.
*/
const sites = [
  "app/[locale]/support/page.tsx",
  "components/credits/CreditsPurchase.tsx",
  "app/[locale]/older-versions/page.tsx",
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

test("PAGE_H1 is text-3xl below sm, text-4xl from sm, text-5xl from md", () => {
  const match = read("components/landing/dusk-pages.ts").match(
    /export const PAGE_H1 =\s*"([^"]*)"/,
  );

  assert.ok(match, "no PAGE_H1 constant in components/landing/dusk-pages.ts");
  const classes = match[1].split(/\s+/);

  assert.deepEqual(
    classes.filter((c) => /^(\w+:)?text-(xs|sm|base|lg|\d?xl)$/.test(c)),
    ["text-3xl", "sm:text-4xl", "md:text-5xl"],
  );
  assert.ok(classes.includes("[overflow-wrap:anywhere]"));
  assert.ok(!classes.includes("bg-clip-text"));
});

test("PageHero's h1 uses PAGE_H1", () => {
  assert.match(
    read("components/landing/DuskPage.tsx"),
    /<h1\s+className=\{`[^`]*\$\{PAGE_H1\}[^`]*`\}/,
  );
});

for (const file of sites) {
  test(`${file} renders its title in PageHero`, () => {
    assert.match(read(file), /<PageHero[^>]*\btitle=\{t\("title"\)\}/);
  });
}
