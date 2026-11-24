/**
 * #2913: the shared inline text-link recipe (`components/text-link.ts`, used by
 * the home FAQ and every blog post body) still painted pre-Dusk purple-400
 * while /choosing-a-model, the legal pages and /open-source paint their links
 * in the Dusk lavender token. This pins both recipes to the Dusk link: lavender
 * `--dusk-lav`, a 40% lavender underline, white on hover, and no purple left.
 */
import assert from "node:assert/strict";
import test from "node:test";

import { PROSE_TEXT_LINK, TEXT_LINK } from "../components/text-link";

const DUSK_LINK = [
  "text-[var(--dusk-lav)]",
  "underline",
  "decoration-[rgba(196,181,253,.4)]",
  "underline-offset-4",
  "transition-colors",
  "hover:text-white",
  "hover:decoration-white",
];

const classes = (recipe: string) => recipe.split(/\s+/).filter(Boolean);

test("TEXT_LINK is the Dusk lavender link recipe", () => {
  assert.deepEqual(new Set(classes(TEXT_LINK)), new Set(DUSK_LINK));
});

test("PROSE_TEXT_LINK is the same recipe on prose-a:, keeping font-normal", () => {
  assert.deepEqual(
    new Set(classes(PROSE_TEXT_LINK)),
    new Set([...DUSK_LINK.map((c) => `prose-a:${c}`), "prose-a:font-normal"]),
  );
});

test("no pre-Dusk purple is left in either recipe", () => {
  for (const recipe of [TEXT_LINK, PROSE_TEXT_LINK]) {
    assert.doesNotMatch(recipe, /purple/);
  }
});
