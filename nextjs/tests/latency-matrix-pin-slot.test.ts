/**
 * #931: on `/[locale]/latency` the pin icon was mounted in front of the
 * provider name only while the row was pinned, so pinning pushed the name 20px
 * right in one frame. The icon now holds its slot in both states and
 * cross-fades in it. This file renders the REAL component and reads the
 * unpinned first-load markup.
 *
 * WHAT IT DOES NOT PROVE: the pinned state (it needs a click) or that the
 * label stays put on screen; the PR's browser verify measures that.
 */
import assert from "node:assert/strict";
import test from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";

import LatencyMatrix from "@/components/latency/LatencyMatrix";
import {
  DURATION_BUCKETS,
  type DurationBucket,
  type LatencyMatrixData,
} from "@/lib/latency/types";

const cell = {
  region: "iad",
  samples: 1000,
  p50: 300,
  p95: 600,
  p99: 900,
  errorRate: 0.01,
};

const DATA = {
  regions: ["iad"],
  totalSamples: 2000,
  windowDays: 7,
  vendors: ["Groq", "Deepgram"].map((label) => ({
    vendor: label.toLowerCase(),
    label,
    cells: [cell],
    models: [],
  })),
} as unknown as LatencyMatrixData;

function pinButtons(): string[] {
  const matrices = Object.fromEntries(
    DURATION_BUCKETS.map((bucket) => [bucket, DATA]),
  ) as Record<DurationBucket, LatencyMatrixData>;
  const html = renderToStaticMarkup(
    createElement(LatencyMatrix, {
      matrices,
      defaultBucket: DURATION_BUCKETS[0],
    }),
  );

  return Array.from(
    html.matchAll(/<button\b[^>]*title="Pin this provider"[^>]*>([\s\S]*?)<\/button>/g),
  ).map((m) => m[1]);
}

test("an unpinned provider button already holds the pin's slot", () => {
  const buttons = pinButtons();

  assert.equal(buttons.length, 2);
  for (const inner of buttons) {
    // The slot comes first, then the label as a bare text node.
    assert.match(inner, /^<span aria-hidden="true" class="[^"]*\bh-3 w-3 shrink-0\b[^"]*"><svg\b/);
    assert.match(inner, /<\/span>(Groq|Deepgram)$/);
  }
});

test("the unpinned pin is hidden by a transition, not by unmounting", () => {
  for (const inner of pinButtons()) {
    const svgClass = /<svg\b[^>]*class="([^"]*)"/.exec(inner)?.[1] ?? "";

    assert.match(svgClass, /\bopacity-0\b/);
    assert.match(svgClass, /\btransition-\[opacity,filter,scale\]/);
    assert.match(svgClass, /\bduration-300\b/);
  }
});
