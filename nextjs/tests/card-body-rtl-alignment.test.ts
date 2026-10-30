/**
 * #878: the homepage pricing and feature cards were HeroUI `<CardBody>`s, and
 * `@heroui/theme` bakes a PHYSICAL `text-left` into that slot, so under
 * `dir="rtl"` (/ar and /he) the heading and copy sat flush left. PR #869 and
 * PR #906 appended the logical `text-start` to each of those five sites.
 *
 * The Dusk homepage draws those cards as plain elements, with no HeroUI slot
 * class to fight. This guard keeps the #878 property true there:
 *   - the two files render no `<CardBody>` (a reintroduced one brings the
 *     vendor `text-left` back, and needs the old patch);
 *   - no class string in them names a physical alignment (`text-left`,
 *     `text-right`, behind any variant, or as an arbitrary value);
 *   - each card container (the pricing plan, the Enterprise strip and the
 *     feature tile) sets `text-start`, so it does not inherit a centred
 *     section heading's alignment.
 *
 * The other `<CardBody>` sites in the repo (support, download,
 * older-versions, credits) were never in scope for #878; this file makes no
 * claim about them.
 */
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { fileURLToPath } from "node:url";

const FILES = [
  "components/landing/PricingSection.tsx",
  "components/landing/FeaturesGrid.tsx",
] as const;

/** A physical alignment class, bare, behind a variant, or arbitrary-valued. */
const PHYSICAL_ALIGNMENT =
  /(^|[\s"'`:])!?(text-left|text-right|\[text-align:\s*(left|right)\])!?(?=$|[\s"'`])/m;

function read(file: string): string {
  return readFileSync(
    fileURLToPath(new URL(`../${file}`, import.meta.url)),
    "utf8",
  ).replace(/\/\*[\s\S]*?\*\//g, ""); // comments are documentation, not classes
}

for (const file of FILES) {
  test(`${file} renders no HeroUI CardBody and no physical text alignment`, () => {
    const source = read(file);

    assert.doesNotMatch(source, /<CardBody\b/);
    assert.doesNotMatch(source, PHYSICAL_ALIGNMENT);
  });
}

test("every homepage pricing card is start-aligned", () => {
  const source = read("components/landing/PricingSection.tsx");
  const plan = /const PLAN =\s*"([^"]*)"/.exec(source)?.[1] ?? "";

  // The two plans share PLAN; the Enterprise strip has its own class string.
  assert.ok(plan.split(/\s+/).includes("text-start"), `PLAN: ${plan}`);
  assert.equal((source.match(/className=\{`\$\{PLAN\}/g) ?? []).length, 2);
  assert.match(source, /className="[^"]*\brounded-\[20px\][^"]*\btext-start\b/);
});

test("every homepage feature tile is start-aligned", () => {
  const source = read("components/landing/FeaturesGrid.tsx");
  const tile = /<Reveal\s+as="li"\s+className=\{`([^`]*)`\}/.exec(source)?.[1];

  assert.ok(tile, "no feature tile <Reveal as=\"li\"> found");
  assert.ok(tile.split(/\s+/).includes("text-start"), `tile: ${tile}`);
});
