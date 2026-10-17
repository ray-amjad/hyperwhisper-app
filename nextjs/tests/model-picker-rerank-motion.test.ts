/**
 * #766: on `/[locale]/choosing-a-model` a weight change re-ranked every row,
 * and swapped the best-match name and its stats, in one frame.
 *
 * The motion itself runs in the browser (a FLIP in a layout effect, plus two
 * CSS transitions), so the behavioural proof is a Chromium run. This file pins
 * the two halves node can see:
 *
 * 1. The server markup carries the transition hooks (the name and every stat
 *    `<dd>`), and none of the one-frame motion state — no `data-motion-from`,
 *    no inline transform or opacity on a row — so the first client render
 *    matches the server HTML.
 * 2. globals.css gives those hooks a non-zero transition at rest, and turns
 *    them off under `prefers-reduced-motion: reduce`.
 */
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";

import ModelPicker, {
  SWAP_REST_MS,
  startsGesture,
} from "@/components/choosing-a-model/ModelPicker";

const ROOT = fileURLToPath(new URL("..", import.meta.url));

function render() {
  return renderToStaticMarkup(
    createElement(ModelPicker, { measured: {}, regions: [] }),
  );
}

test("the best-match name and every stat value carry their transition class", () => {
  const html = render();

  assert.match(html, /<h2 class="t-text-swap text-2xl[^"]*">[^<]+<\/h2>/);
  const dl = html.match(/<dl\b[\s\S]*?<\/dl>/);
  assert.ok(dl, "no stats <dl>");
  const dds = Array.from(dl[0].matchAll(/<dd class="([^"]*)"/g));
  assert.equal(dds.length, 5, "4 stat values + the Cost dollar note");
  for (const [, cls] of dds) assert.match(cls, /\bt-number-pop\b/);
});

test("the server markup holds no one-frame motion state", () => {
  const html = render();
  const rows = Array.from(html.matchAll(/<tr\b[^>]*>/g)).map((m) => m[0]);

  assert.ok(rows.length > 10, `only ${rows.length} rows rendered`);
  assert.doesNotMatch(html, /data-motion-from/);
  for (const row of rows) assert.doesNotMatch(row, /\bstyle=/, row);
});

const REDUCE = /@media\s*\(\s*prefers-reduced-motion\s*:\s*reduce\s*\)\s*\{/g;

function ruleBody(css: string, selector: string): string {
  const at = css.indexOf(`${selector} {`);
  assert.ok(at >= 0, `${selector} rule exists`);
  return css.slice(css.indexOf("{", at) + 1, css.indexOf("}", at));
}

test("globals.css times the swap and the pop from motion tokens, and guards reduce", () => {
  const css = readFileSync(join(ROOT, "styles/globals.css"), "utf8").replace(
    /\/\*[\s\S]*?\*\//g,
    "",
  );

  for (const token of [
    "--motion-duration-micro: 150ms",
    "--motion-duration-fast: 250ms",
    "--motion-ease-smooth-out: cubic-bezier(0.22, 1, 0.36, 1)",
  ]) {
    assert.ok(css.includes(token), `token ${token}`);
  }
  assert.match(ruleBody(css, ".t-text-swap"), /opacity var\(--motion-duration-micro\)/);
  assert.match(ruleBody(css, ".t-number-pop"), /opacity var\(--motion-duration-fast\)/);

  // A reduce block, after both rules, turns both transitions off.
  const guarded = Array.from(css.matchAll(REDUCE)).some((m) => {
    const rest = css.slice(m.index ?? 0);
    return /\.t-text-swap,\s*\.t-number-pop\s*\{\s*transition:\s*none;/.test(rest);
  });
  assert.ok(guarded, "a prefers-reduced-motion block sets transition: none on both");
});

test("only the first weight change of a gesture replays the swap and the pop", () => {
  // A drag fires a change every few tens of ms; replaying each one restarted
  // the pop from opacity 0, so the Match value stayed dim for the whole drag.
  assert.equal(startsGesture(1000, null), true, "the first change ever");
  assert.equal(startsGesture(1000 + SWAP_REST_MS, 1000), true, "after a rest");
  assert.equal(startsGesture(1050, 1000), false, "mid-drag");
  assert.equal(startsGesture(1000 + SWAP_REST_MS - 1, 1000), false);
  // Longer than the pop, so a pop still running is never restarted.
  assert.ok(SWAP_REST_MS > 250);
});
