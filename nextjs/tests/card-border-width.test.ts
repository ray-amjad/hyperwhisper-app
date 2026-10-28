/**
 * #1384: HeroUI `<Card>` has no border width of its own. A Card className that
 * sets only a border COLOUR (`border-gray-800`, `border-purple-700`, ...)
 * computes to a 0px border, so the edge never paints: the three pricing
 * cards, the /credits purchase card and every /older-versions card had no
 * edge, and the featured plan lost its purple outline.
 *
 * WHAT IT ASSERTS — every `<Card>` className literal in the app that names a
 * border colour also carries the `border` width utility, and that utility
 * survives the real `<Card>` under `react-dom/server` (tailwind-merge could
 * drop it if the vendor slot ever set a width of its own).
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

const BORDER_COLOUR =
  /^(?:\w+:)*border-(?!(?:t|b|l|r|x|y|s|e)$)(?:[a-z]+-\d{2,3}(?:\/\d+)?|white|black)$/;

function tsxFiles(dir: string): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const path = join(dir, entry.name);
    if (entry.isDirectory()) return tsxFiles(path);
    return entry.name.endsWith(".tsx") ? [path] : [];
  });
}

const sites = ["app", "components"]
  .flatMap((dir) => tsxFiles(join(root, dir)))
  .flatMap((path) => {
    const file = relative(root, path);
    const source = readFileSync(path, "utf8");
    return [...source.matchAll(/<Card\b([^>]*?)>/g)].flatMap((m) => {
      const attr = m[1].match(/className=(?:"([^"]*)"|\{`([^`]*)`\})/);
      if (!attr) return [];
      const classes = (attr[1] ?? attr[2]).split(/\s+/).filter(Boolean);
      const colours = classes.filter(
        (c) => !c.includes(":") && BORDER_COLOUR.test(c),
      );
      if (colours.length === 0) return [];
      if (EXCLUDED.some((e) => e.file === file && colours.includes(e.colour))) {
        return [];
      }
      const line = source.slice(0, m.index).split("\n").length;
      return [{ where: `${file}:${line}`, classes, colours }];
    });
  });

test("the scan finds the six Cards #1384 fixed", () => {
  const files = sites.map((s) => s.where.replace(/:\d+$/, ""));
  const count = (f: string) => files.filter((x) => x === f).length;
  assert.equal(count("components/landing/PricingSection.tsx"), 3);
  assert.equal(count("components/credits/CreditsPurchase.tsx"), 1);
  assert.equal(count("app/[locale]/older-versions/page.tsx"), 2);
});

for (const { where, classes, colours } of sites) {
  test(`${where} <Card> with ${colours.join(" ")} sets a border width`, () => {
    assert.ok(
      classes.includes("border"),
      `${where} sets ${colours.join(" ")} but no \`border\` width utility`,
    );

    // The served class list keeps both the width and the colour.
    const html = renderToStaticMarkup(
      createElement(Card, { className: classes.join(" ") }),
    );
    const served = html.match(/class="([^"]*)"/)?.[1].split(/\s+/) ?? [];
    assert.ok(served.includes("border"), `served classes: ${served.join(" ")}`);
    for (const colour of colours) assert.ok(served.includes(colour));
  });
}
