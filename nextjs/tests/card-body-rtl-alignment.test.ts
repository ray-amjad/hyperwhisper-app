/**
 * The five landing-page `CardBody` sites that PR #869 and PR #906 patched for
 * #878 — and nothing else in the repo.
 *
 * `@heroui/theme` bakes a PHYSICAL `text-left` into the card `body` slot. Under
 * `dir="rtl"` (/ar and /he) that pins the heading and the copy flush left. The
 * fix is one logical utility, `text-start`, appended to each `<CardBody>`:
 * tailwind-merge puts `text-left` and `text-start` in the same alignment group,
 * so the later class DROPS the vendor one instead of losing to it.
 *
 * SCOPE — this guard covers exactly 3 sites in `components/landing/
 * PricingSection.tsx` and 2 in `components/landing/FeaturesGrid.tsx`. Seven
 * other `<CardBody>` sites exist (support, download, older-versions, credits);
 * they were NOT in scope for #878, they are NOT patched, and this file makes no
 * claim about them. Widening it to a directory walk would fail on day one.
 *
 * WHAT IT ASSERTS — for each site, the className literal is scraped from the
 * source and put through the real `<CardBody>` under `react-dom/server`. The
 * served body-slot class list must (a) carry `text-start` and (b) contain no
 * physical alignment class at all. Both halves are properties of the SERVED
 * page, so the guard stays correct if `@heroui/theme` ever ships a logical
 * default: the vendor's own `text-start` would satisfy (a), and that page is
 * aligned correctly whatever the component string says.
 *
 * `nextjs/package-lock.json` is gitignored and CI runs `npm install`, so every
 * run re-resolves `@heroui/theme: ^2.4.26`. Nothing here may assert what the
 * vendor's DEFAULT slot contains — a HeroUI release that goes logical would
 * then turn every unrelated nextjs PR red and tell the maintainer to strip a
 * working fix.
 *
 * No DOM is needed, the same way `tests/root-layout-direction.test.ts` renders
 * the root layout. jsdom and friends stay absent.
 */
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { fileURLToPath } from "node:url";

import { Card, CardBody } from "@heroui/card";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";

/**
 * The patched sites, with the EXACT number expected in each file. A count that
 * moves is a real failure in both directions: a new landing card that nobody
 * gave `text-start`, or a scraper that has gone blind to a site written in a
 * form it cannot read (template literal, expression, no `className`).
 */
const PATCHED_SITES: ReadonlyArray<readonly [file: string, count: number]> = [
  ["components/landing/PricingSection.tsx", 3],
  ["components/landing/FeaturesGrid.tsx", 2],
];

/** One `<CardBody className="…">` found in a component source. */
interface CardBodySite {
  file: string;
  line: number;
  className: string;
}

/**
 * A physical alignment class, in any form that survives to the class list:
 * bare, behind any variant prefix (`md:`, `rtl:`, `[&>p]:`), as an
 * arbitrary-value property, and with either important marker. Verified against
 * the real rendered slot — none of the ~20 vendor classes matches it.
 *
 * `rtl:text-right` is rejected too. It is start-aligned in practice, but no
 * patched site writes it, and a site that starts to should re-read this guard
 * rather than slip past it.
 */
const PHYSICAL_ALIGNMENT =
  /(^|:)!?(text-left|text-right|\[text-align:\s*(left|right)\])!?$/;

/** The sentinel child used to locate the rendered body slot's own element. */
const SENTINEL = "card-body-sentinel";

/** The slot element's opening tag, whatever attributes it carries. */
const SLOT_OPEN_TAG = new RegExp(`<[a-zA-Z][^>]*>(?=${SENTINEL})`);

/** `class` read out of that tag by name, not by position. */
const CLASS_ATTRIBUTE = /\sclass="([^"]*)"/;

function readComponent(relative: string): string {
  return readFileSync(
    fileURLToPath(new URL(`../${relative}`, import.meta.url)),
    "utf8",
  );
}

/**
 * Every `<CardBody className="…">` in one file, with its line number so a
 * failure names the exact site.
 *
 * Block comments are blanked first — `{/* … *\/}` around a `<CardBody>` is
 * documentation, not a site — and blanked in place so line numbers still point
 * at the real source. String literals are not masked; the exact count above is
 * what catches a site the scraper miscounts either way.
 */
function cardBodySites(file: string, expected: number): CardBodySite[] {
  const source = readComponent(file).replace(/\/\*[\s\S]*?\*\//g, (comment) =>
    comment.replace(/[^\n]/g, " "),
  );

  // `Array.from`, not a for-of over the iterator: this tsconfig targets es5
  // without downlevelIteration, so iterating a RegExpStringIterator directly is
  // a TS2802 typecheck error.
  const sites = Array.from(
    source.matchAll(/<CardBody\s+className="([^"]*)"/g),
    (match): CardBodySite => ({
      file,
      line: source.slice(0, match.index).split("\n").length,
      className: match[1],
    }),
  );

  assert.equal(
    sites.length,
    expected,
    `${file}: scraped ${sites.length} <CardBody className="…"> sites, expected ${expected}. Either the file gained or lost a landing card — give the new one text-start and update the count here — or a site is written in a form this scraper cannot read.`,
  );

  return sites;
}

/**
 * The class list the real `<CardBody className={…}>` actually serves.
 *
 * Rendered inside a real `<Card>`, as the components use it, so the whole
 * HeroUI path runs: the slot function, the theme's own class list, and
 * tailwind-merge resolving the conflict between them. The slot is found by the
 * sentinel and its `class` is read by name, so a vendor that adds `data-slot`
 * or reorders attributes does not break the lookup.
 */
function renderedBodyClass(className: string): string {
  const markup = renderToStaticMarkup(
    createElement(Card, null, createElement(CardBody, { className }, SENTINEL)),
  );

  const tag = markup.match(SLOT_OPEN_TAG);
  assert.ok(tag, `CardBody rendered no element around the sentinel: ${markup}`);

  const attribute = tag[0].match(CLASS_ATTRIBUTE);
  assert.ok(attribute, `CardBody body slot carries no class: ${markup}`);

  return attribute[1];
}

test("the five patched landing CardBody sites serve a logically aligned body slot", () => {
  for (const [file, expected] of PATCHED_SITES) {
    for (const site of cardBodySites(file, expected)) {
      const rendered = renderedBodyClass(site.className);
      const classes = rendered.split(/\s+/).filter(Boolean);
      const physical = classes.filter((name) => PHYSICAL_ALIGNMENT.test(name));

      assert.deepEqual(
        physical,
        [],
        `${site.file}:${site.line} — the served body slot still carries physical alignment ${physical.join(", ")} for className="${site.className}", so this card snaps flush left on /ar and /he. Rendered: "${rendered}".`,
      );
      assert.ok(
        classes.includes("text-start"),
        `${site.file}:${site.line} — text-start did not reach the served body slot for className="${site.className}"; got "${rendered}".`,
      );
    }
  }
});
