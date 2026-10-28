/**
 * #1384: HeroUI `<Card>` has no border width of its own. A Card className that
 * sets only a border COLOUR (`border-gray-800`, `border-purple-700`, ...)
 * computes to a 0px border, so the edge never paints: the three pricing
 * cards, the /credits purchase card and every /older-versions card had no
 * edge, and the featured plan lost its purple outline.
 *
 * WHAT IT ASSERTS — every `<Card>` className in the app that names a border
 * colour (variants included) also carries a border width utility (`border`,
 * `border-2`, `border-t`, ...), and that width survives the real `<Card>`
 * under `react-dom/server` (tailwind-merge could drop it if the vendor slot
 * ever set a width of its own). A className the scan cannot read (`cn(...)`,
 * a variable) fails instead of being skipped.
 *
 * The walk covers every `.tsx` file under `app/` and `components/`, so a new
 * colour-only Card fails here. ONE known site is excluded: the
 * /older-versions error card (`border-red-800`), which #925 owns.
 *
 * No DOM is needed, the same way `tests/card-body-rtl-alignment.test.ts`
 * renders `<CardBody>`.
 */
import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import { join, relative } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

import { Card } from "@heroui/card";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";

const root = fileURLToPath(new URL("..", import.meta.url));

const EXCLUDED = [
  { file: "app/[locale]/older-versions/page.tsx", colour: "border-red-800" }, // #925
];

// A border colour, after any variant prefix: `border-gray-800`,
// `border-orange-700/50`, `border-white`, `border-transparent`, ...
const BORDER_COLOUR =
  /^border-(?:[a-z]+-\d{2,3}(?:\/\d+)?|white|black|transparent|current|inherit)$/;
// Any border width: `border`, `border-2`, `border-[1.5px]`, `border-t`,
// `border-x-2`, `border-s`, ...
const BORDER_WIDTH = /^border(?:-[xytblrse])?(?:-\d+|-\[[^\]]+\])?$/;
// What a Tailwind class token can look like, so `${`, `?` and
// identifiers from a template literal are not taken for classes.
const CLASS_TOKEN = /^[!\w:/.\[\]#%()-]+$/;

const base = (c: string) => c.slice(c.lastIndexOf(":") + 1);

function tsxFiles(dir: string): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const path = join(dir, entry.name);
    if (entry.isDirectory()) return tsxFiles(path);
    return entry.name.endsWith(".tsx") ? [path] : [];
  });
}

/**
 * The class tokens of one `className` value: a plain string, or a template
 * literal whose static text and quoted strings inside `${...}` both count.
 * Anything else (`cn(...)`, a variable) returns null, and the test fails on
 * it instead of skipping the Card.
 */
function classTokens(opening: string): string[] | null | undefined {
  const at = opening.search(/\bclassName=/);
  if (at === -1) return undefined;
  const value = opening.slice(at + "className=".length);
  const plain = value.match(/^"([^"]*)"/);
  const template = value.match(/^\{`([^`]*)`\}/);
  if (!plain && !template) return null;
  const text = plain
    ? plain[1]
    : template![1].replace(/"([^"]*)"/g, " $1 ").replace(/[${}]/g, " ");
  return text.split(/\s+/).filter((c) => CLASS_TOKEN.test(c));
}

const sites = ["app", "components"]
  .flatMap((dir) => tsxFiles(join(root, dir)))
  .flatMap((path) => {
    const file = relative(root, path);
    const source = readFileSync(path, "utf8");
    return Array.from(source.matchAll(/<Card\b/g)).flatMap((m) => {
      // The opening tag runs to the next JSX tag at the latest: `>` alone
      // cannot end it, because `() => ...` and `a > b` hold one too.
      const rest = source.slice(m.index! + 1);
      const next = rest.search(/<[A-Za-z/]/);
      const opening = next === -1 ? rest : rest.slice(0, next);
      const line = source.slice(0, m.index).split("\n").length;
      const classes = classTokens(opening);
      if (classes === undefined) return [];
      if (classes === null) {
        return [{ where: `${file}:${line}`, classes: [], colours: [], unparsed: true }];
      }
      const colours = classes.filter((c) => BORDER_COLOUR.test(base(c)));
      if (colours.length === 0) return [];
      if (EXCLUDED.some((e) => e.file === file && colours.includes(e.colour))) {
        return [];
      }
      return [{ where: `${file}:${line}`, classes, colours, unparsed: false }];
    });
  });

test("the scan finds the six Cards #1384 fixed", () => {
  const at = (f: string) => sites.filter((s) => s.where.startsWith(`${f}:`)).length;
  // At least these; a later bordered Card in the same file is fine.
  assert.ok(at("components/landing/PricingSection.tsx") >= 3);
  assert.ok(at("components/credits/CreditsPurchase.tsx") >= 1);
  assert.ok(at("app/[locale]/older-versions/page.tsx") >= 2);
});

for (const { where, classes, colours, unparsed } of sites) {
  test(`${where} <Card> with ${colours.join(" ") || "a className"} sets a border width`, () => {
    assert.ok(
      !unparsed,
      `${where}: className is not a string or template literal; check its border by hand and widen this scan`,
    );
    assert.ok(
      classes.some((c) => BORDER_WIDTH.test(base(c))),
      `${where} sets ${colours.join(" ")} but no border width utility`,
    );

    // The served class list keeps both the width and the colour.
    const html = renderToStaticMarkup(
      createElement(Card, { className: classes.join(" ") }),
    );
    const served = html.match(/class="([^"]*)"/)?.[1].split(/\s+/) ?? [];
    assert.ok(
      served.some((c) => BORDER_WIDTH.test(base(c))),
      `served classes: ${served.join(" ")}`,
    );
    for (const colour of colours) assert.ok(served.includes(colour));
  });
}
