/**
 * #867: every control on `/[locale]/choosing-a-model` that sits under a
 * visible caption carries that caption as its accessible name.
 *
 * `components/choosing-a-model/ModelPicker.tsx` drew "Closest region" as a
 * plain `<span>` beside a native `<select>`, with nothing joining the two, so
 * a screen reader announced an unnamed combo box (axe `select-name`,
 * critical). The three button groups above it ("Your platform", "Languages
 * you dictate", "Must have") had the same unjoined caption. This file renders
 * the REAL component and checks that each control points, by
 * `aria-labelledby`, at an element that exists and holds the caption text.
 *
 * WHAT IT DOES NOT PROVE. It reads server markup, not a browser's computed
 * accessible name. The verify round on the PR reads that from Chromium.
 */
import assert from "node:assert/strict";
import test from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";

import ModelPicker from "@/components/choosing-a-model/ModelPicker";

function render(regions: string[]): string {
  return renderToStaticMarkup(
    createElement(ModelPicker, { measured: {}, regions }),
  );
}

/** The text inside the element with this id, tags stripped. */
function textOfId(html: string, id: string): string | null {
  const match = new RegExp(`<([a-z]+)[^>]*\\bid="${id}"[^>]*>([\\s\\S]*?)</\\1>`).exec(html);

  return match ? match[2].replace(/<[^>]+>/g, "").trim() : null;
}

/** The `aria-labelledby` value on the first `<tag>` whose attributes match. */
function labelledByOf(html: string, openTag: RegExp): string | null {
  const tag = openTag.exec(html)?.[0];

  if (!tag) return null;

  return /aria-labelledby="([^"]+)"/.exec(tag)?.[1] ?? null;
}

test("the region select is named by its visible Closest region caption", () => {
  const html = render(["fra", "iad"]);
  const id = labelledByOf(html, /<select\b[^>]*>/);

  assert.ok(id, "the <select> has no aria-labelledby");
  assert.equal(textOfId(html, id), "Closest region");
});

for (const caption of ["Your platform", "Languages you dictate", "Must have"]) {
  test(`the "${caption}" buttons form a group named by their caption`, () => {
    const html = render(["fra"]);
    const groups = Array.from(html.matchAll(/<div\b[^>]*\brole="group"[^>]*>/g)).map(
      (m) => /aria-labelledby="([^"]+)"/.exec(m[0])?.[1] ?? null,
    );
    const names = groups.map((id) => (id ? textOfId(html, id) : null));

    assert.ok(
      names.includes(caption),
      `no role="group" is named "${caption}"; named groups: ${JSON.stringify(names)}`,
    );
  });
}

test("with no measured region there is no select and no dangling label id", () => {
  const html = render([]);

  assert.doesNotMatch(html, /<select\b/);
  for (const m of Array.from(html.matchAll(/aria-labelledby="([^"]+)"/g))) {
    assert.ok(textOfId(html, m[1]), `aria-labelledby="${m[1]}" names no element`);
  }
});
