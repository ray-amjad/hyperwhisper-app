/**
 * #988: on `/[locale]/latency` the "Break down by model" toggle swapped a right
 * chevron for a down chevron in one frame. It now draws one right chevron and
 * turns it 90deg with a 150ms transition, like the FAQ and Devices
 * disclosures. This file renders the REAL component and reads the closed
 * first-load markup.
 *
 * WHAT IT DOES NOT PROVE: the open state (it needs a click) or the turn on
 * screen; the PR's browser verify reads `rotate` in both states.
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

const DATA = {
  regions: ["iad"],
  totalSamples: 1000,
  windowDays: 7,
  vendors: [
    {
      vendor: "groq",
      label: "Groq",
      cells: [
        { region: "iad", samples: 1000, p50: 300, p95: 600, p99: 900, errorRate: 0 },
      ],
      models: [],
    },
  ],
} as unknown as LatencyMatrixData;

function toggleInner(): string {
  const matrices = Object.fromEntries(
    DURATION_BUCKETS.map((bucket) => [bucket, DATA]),
  ) as Record<DurationBucket, LatencyMatrixData>;
  const html = renderToStaticMarkup(
    createElement(LatencyMatrix, {
      matrices,
      defaultBucket: DURATION_BUCKETS[0],
    }),
  );
  const match = /<button\b[^>]*aria-pressed="false"[^>]*>(<!--[\s\S]*?-->)?(<svg\b[\s\S]*?<\/svg>)Break down by model<\/button>/.exec(
    html,
  );

  assert.ok(match, "no closed Break down by model button with a leading svg");

  return match[2];
}

test("the closed toggle draws one right chevron, unturned, with a 150ms turn", () => {
  const svg = toggleInner();
  const svgClass = /class="([^"]*)"/.exec(svg)?.[1] ?? "";

  assert.equal(svg.match(/<svg\b/g)?.length, 1);
  assert.match(svgClass, /\blucide-chevron-right\b/);
  assert.match(svgClass, /\btransition-transform\b/);
  assert.match(svgClass, /\bduration-150\b/);
  assert.doesNotMatch(svgClass, /\brotate-90\b/);
});

test("the matrix no longer draws a down chevron anywhere", () => {
  const source = readFileSync(
    new URL("../components/latency/LatencyMatrix.tsx", import.meta.url),
    "utf8",
  );

  assert.doesNotMatch(source, /ChevronDown/);
});
