/**
 * #813: switching the Metric or Clip length on `/[locale]/latency` recoloured
 * all 187 heat cells in one frame. Each cell now cross-fades its colour
 * (`.t-heat-cell`, a 150 ms background-color + color transition) and fades its
 * number in (`.t-heat-swap`) once the reader has switched. This renders the
 * REAL component with one vendor and reads the first-load server markup, then
 * reads the CSS the classes point at.
 *
 * WHAT IT DOES NOT PROVE: the fade after a click (no DOM here). The PR's
 * browser probe samples a cell's computed colour every 50 ms for that.
 */
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";

import LatencyMatrix from "@/components/latency/LatencyMatrix";
import {
  DURATION_BUCKETS,
  type DurationBucket,
  type LatencyMatrixData,
} from "@/lib/latency/types";

const cell = (region: string, samples: number) => ({
  region,
  samples,
  p50: 263,
  p95: 282,
  p99: 2415,
  errorRate: 0.01,
});

const DATA: LatencyMatrixData = {
  vendors: [
    {
      vendor: "groq",
      label: "Groq",
      // One live cell and one with too few attempts (the "—" cell).
      cells: [cell("iad", 400), cell("fra", 1)],
      models: [],
    },
  ],
  regions: ["iad", "fra"],
  totalSamples: 401,
  windowDays: 90,
};

function render(): string {
  const matrices = Object.fromEntries(
    DURATION_BUCKETS.map((bucket) => [bucket, DATA]),
  ) as Record<DurationBucket, LatencyMatrixData>;

  return renderToStaticMarkup(
    createElement(LatencyMatrix, {
      defaultBucket: DURATION_BUCKETS[0],
      matrices,
    }),
  );
}

test("every heat cell, live or too-few-attempts, carries the colour transition", () => {
  const tds = Array.from(render().matchAll(/<td\b([^>]*)>/g)).map((m) => m[1]);

  assert.equal(tds.length, 2);
  for (const attrs of tds) {
    assert.match(attrs, /class="t-heat-cell /, attrs);
  }
});

test("on first load the number is not faded in", () => {
  const html = render();

  assert.match(html, /<td\b[^>]*><span>263<\/span><\/td>/);
  assert.doesNotMatch(html, /t-heat-swap/);
});

test("the CSS enumerates background-color and color, and reduced motion drops both", () => {
  const css = readFileSync(
    new URL("../styles/globals.css", import.meta.url),
    "utf8",
  );
  const rule = /\.t-heat-cell \{\s*transition:([^;]+);/.exec(css);

  assert.ok(rule, "no .t-heat-cell transition");
  assert.match(rule[1], /background-color var\(--motion-duration-micro\)/);
  assert.match(rule[1], /\bcolor var\(--motion-duration-micro\)/);
  assert.doesNotMatch(rule[1], /\ball\b/);
  assert.match(
    css,
    /@media \(prefers-reduced-motion: reduce\) \{\s*\.t-heat-cell \{\s*transition: none;\s*\}\s*\.t-heat-swap \{\s*animation: none;/,
  );
});
