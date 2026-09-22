/**
 * The RTL alignment of HeroUI `CardBody` on the landing page (#878).
 *
 * `@heroui/theme` bakes a PHYSICAL `text-left` into the card `body` slot. Under
 * `dir="rtl"` (/ar and /he) that pins the heading and the copy flush left. The
 * fix is one logical utility, `text-start`, on each `<CardBody>`: tailwind-merge
 * puts `text-left` and `text-start` in the same alignment group, so the later
 * class DROPS the vendor one instead of losing to it.
 *
 * That makes the fix depend on two things no test asserted:
 *
 *   1. the COMPONENT half — each `<CardBody>` still carries `text-start`;
 *   2. the VENDOR half — `@heroui/theme` (ranged `^2.4.26`) plus the
 *      tailwind-merge it ships still resolve that string to a class list with
 *      no `text-left` left in it.
 *
 * A dependency bump that moved `text-left` to another slot, or split the
 * alignment group, would silently revert every RTL locale on all five patched
 * sites with CI green. `.github/workflows/` runs no browser job, so nothing else
 * can catch it.
 *
 * Review round 1 proposed asserting the vendor half alone —
 * `card({}).body({ class: "…text-start" })` with a hand-written string — and
 * named its own weakness: deleting `text-start` from a component would still
 * leave that green. This file instead READS the class strings out of the two
 * component sources and feeds each one through the REAL `<CardBody>`, so both
 * halves are guarded by the same assertion, and the merge `CardBody` itself
 * performs (`clsx(classNames?.body, className)` into the slot) is covered too
 * rather than re-implemented here.
 *
 * No DOM is needed: `Card`/`CardBody` render to a string under
 * `react-dom/server`, the same way `tests/root-layout-direction.test.ts` renders
 * the root layout. jsdom and friends stay absent.
 */
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { fileURLToPath } from "node:url";

import { Card, CardBody } from "@heroui/card";
import { card } from "@heroui/theme";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";

/** Landing-page files whose `CardBody` slots must mirror under `dir="rtl"`. */
const COMPONENTS = [
  "components/landing/PricingSection.tsx",
  "components/landing/FeaturesGrid.tsx",
];

/** One `<CardBody className="…">` found in a component source. */
interface CardBodySite {
  file: string;
  line: number;
  className: string;
}

function readComponent(relative: string): string {
  return readFileSync(
    fileURLToPath(new URL(`../${relative}`, import.meta.url)),
    "utf8",
  );
}

/**
 * Every `<CardBody className="…">` in one file, with its line number so a
 * failure names the exact site rather than a count.
 *
 * The extraction is deliberately narrow — a double-quoted literal only — and it
 * is checked against a plain count of `<CardBody` openings below. A site written
 * with a template literal, an expression, or no `className` at all would not be
 * scraped, and a scraper that silently skips a site is a test that passes
 * vacuously. If that check fails, widen this reader; do not delete it.
 */
function cardBodySites(file: string): CardBodySite[] {
  const source = readComponent(file);
  const sites: CardBodySite[] = [];

  // `Array.from`, not a for-of over the iterator: this tsconfig targets es5
  // without downlevelIteration, so iterating a RegExpStringIterator directly is
  // a TS2802 typecheck error.
  const matches = Array.from(
    source.matchAll(/<CardBody\s+className="([^"]*)"/g),
  );

  for (const match of matches) {
    sites.push({
      file,
      line: source.slice(0, match.index).split("\n").length,
      className: match[1],
    });
  }

  const openings = source.match(/<CardBody\b/g)?.length ?? 0;

  assert.equal(
    sites.length,
    openings,
    `${file}: scraped ${sites.length} of ${openings} <CardBody> sites — one is written in a form this reader cannot see`,
  );
  assert.ok(sites.length > 0, `${file}: no <CardBody> found at all`);

  return sites;
}

/** The sentinel child used to locate the rendered body slot's own <div>. */
const SENTINEL = "card-body-sentinel";

/**
 * The class list the real `<CardBody className={…}>` actually serves.
 *
 * Rendered inside a real `<Card>`, as the components use it, so the whole
 * HeroUI path runs: the slot function, the theme's own class list, and
 * tailwind-merge resolving the conflict between them.
 */
function renderedBodyClass(className: string): string {
  const markup = renderToStaticMarkup(
    createElement(Card, null, createElement(CardBody, { className }, SENTINEL)),
  );

  const match = markup.match(new RegExp(`<div class="([^"]*)">${SENTINEL}`));

  assert.ok(match, `CardBody rendered no body element: ${markup}`);

  return match[1];
}

test("every landing CardBody carries the logical text-start", () => {
  for (const file of COMPONENTS) {
    for (const site of cardBodySites(file)) {
      assert.ok(
        site.className.split(/\s+/).includes("text-start"),
        `${site.file}:${site.line} — <CardBody className="${site.className}"> has no text-start, so its copy stays flush left on /ar and /he`,
      );
    }
  }
});

test("HeroUI resolves each landing CardBody with the physical text-left dropped", () => {
  for (const file of COMPONENTS) {
    for (const site of cardBodySites(file)) {
      const rendered = renderedBodyClass(site.className);
      const classes = rendered.split(/\s+/);

      assert.ok(
        !classes.includes("text-left"),
        `${site.file}:${site.line} — the vendor's text-left survived the merge for className="${site.className}"; the rendered body slot is "${rendered}". A @heroui/theme or tailwind-merge bump has broken the RTL fix.`,
      );
      assert.ok(
        classes.includes("text-start"),
        `${site.file}:${site.line} — text-start did not reach the rendered body slot for className="${site.className}"; got "${rendered}"`,
      );
    }
  }
});

test("the vendor still bakes a physical text-left into the card body slot", () => {
  // A tripwire, not a wish. `text-start` on these five sites exists only to beat
  // this class. If @heroui/theme ever ships a logical default, this fails — and
  // the right answer is to re-read the slot and decide whether the overrides can
  // go, not to assume the fix above is still doing something.
  const vendorDefault = card({}).body({}).split(/\s+/);

  assert.ok(
    vendorDefault.includes("text-left"),
    `@heroui/theme no longer puts text-left in the card body slot (got "${vendorDefault.join(" ")}") — re-check whether the text-start overrides on the landing cards are still needed`,
  );
});
