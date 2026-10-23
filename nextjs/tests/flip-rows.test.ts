/**
 * #812: the /latency matrix re-sorted its rows in one frame. It now shares the
 * #766 row FLIP (lib/flip-rows) with /choosing-a-model. The slide runs in a
 * browser; this file pins what node can see: which rows slide, from where,
 * and that the matrix's server markup carries no one-frame motion state.
 */
import assert from "node:assert/strict";
import test from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";

import LatencyMatrix from "@/components/latency/LatencyMatrix";
import {
  FLIP_DURATION_MS,
  FLIP_EASING,
  FLIP_MAX_DISTANCE,
  slideRows,
} from "@/lib/flip-rows";
import type { LatencyMatrixData } from "@/lib/latency/types";

type Call = { keyframes: Keyframe[]; options: KeyframeAnimationOptions };

function fakeRow(top: number, calls: Call[], height = 36) {
  return {
    offsetTop: top,
    offsetHeight: height,
    animate(keyframes: Keyframe[], options: KeyframeAnimationOptions) {
      calls.push({ keyframes, options });
      return { cancel() {} } as Animation;
    },
  } as unknown as HTMLElement;
}

function fakeTbody() {
  return {
    offsetTop: 0,
    getBoundingClientRect: () => ({ top: 0 }),
    animate() {},
  } as unknown as HTMLElement;
}

test("a moved row slides from its old top; a still or new row does not", () => {
  (globalThis as { window?: unknown }).window = { innerHeight: 1200 };
  const a: Call[] = [];
  const b: Call[] = [];
  const c: Call[] = [];
  const rows = new Map([
    ["a", fakeRow(0, a)],
    ["b", fakeRow(36, b)],
    ["c", fakeRow(72, c)],
  ]);
  // a and b swapped; c kept its place; "gone" left the table.
  const before = new Map([
    ["a", 36],
    ["b", 0],
    ["c", 72],
    ["gone", 108],
  ]);

  slideRows(fakeTbody(), rows, before, new Map());

  assert.deepEqual(a[0].keyframes, [
    { transform: "translateY(36px)" },
    { transform: "none" },
  ]);
  assert.deepEqual(b[0].keyframes, [
    { transform: "translateY(-36px)" },
    { transform: "none" },
  ]);
  assert.deepEqual(a[0].options, {
    duration: FLIP_DURATION_MS,
    easing: FLIP_EASING,
  });
  assert.equal(c.length, 0, "a row that did not move is not animated");
});

test("a row new to the table just appears", () => {
  (globalThis as { window?: unknown }).window = { innerHeight: 1200 };
  const fresh: Call[] = [];

  slideRows(
    fakeTbody(),
    new Map([["fresh", fakeRow(0, fresh)]]),
    new Map(),
    new Map(),
  );
  assert.equal(fresh.length, 0);
});

test("a far move is capped and fades in; an off-screen slide is skipped", () => {
  (globalThis as { window?: unknown }).window = { innerHeight: 1200 };
  const far: Call[] = [];
  const hidden: Call[] = [];
  const rows = new Map([
    ["far", fakeRow(0, far)],
    ["hidden", fakeRow(5000, hidden)],
  ]);
  const before = new Map([
    ["far", 828],
    ["hidden", 4000],
  ]);

  slideRows(fakeTbody(), rows, before, new Map());

  assert.deepEqual(far[0].keyframes, [
    { transform: `translateY(${FLIP_MAX_DISTANCE}px)`, opacity: 0 },
    { transform: "none", opacity: 1 },
  ]);
  assert.equal(hidden.length, 0);
});

test("the latency matrix's server markup holds no one-frame motion state", () => {
  const cell = {
    region: "iad",
    samples: 1000,
    p50: 300,
    p95: 600,
    p99: 900,
    errorRate: 0.01,
  };
  const matrix = {
    regions: ["iad"],
    windowDays: 7,
    vendors: ["groq", "deepgram"].map((vendor) => ({
      vendor,
      label: vendor,
      cells: [cell],
      models: [],
    })),
  } as unknown as LatencyMatrixData;
  const html = renderToStaticMarkup(
    createElement(LatencyMatrix, {
      matrices: { short: matrix, medium: matrix, long: matrix } as never,
      defaultBucket: "short" as never,
    }),
  );
  const rows = Array.from(html.matchAll(/<tr\b[^>]*>/g)).map((m) => m[0]);

  assert.ok(rows.length >= 3, `only ${rows.length} rows rendered`);
  for (const row of rows) assert.doesNotMatch(row, /\bstyle=/, row);
});
