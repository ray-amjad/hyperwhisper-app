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
  FLIP_LAYER,
  FLIP_MAX_DISTANCE,
  slideRows,
} from "@/lib/flip-rows";
import type { LatencyMatrixData } from "@/lib/latency/types";

/** #2458: the paint layer each kind of slide gets. */
const UP = { position: "relative", zIndex: FLIP_LAYER.up };
const DOWN = { position: "relative", zIndex: FLIP_LAYER.down };
const FAR_UP = { position: "relative", zIndex: FLIP_LAYER.farUp };

type Call = { keyframes: Keyframe[]; options: KeyframeAnimationOptions };

/** A node whose computed background is `bg` ("" reads as transparent). */
type Painted = { bg?: string; parentElement?: unknown };

/** A window with `innerHeight` and a getComputedStyle that reads `bg`. */
function fakeWindow() {
  (globalThis as { window?: unknown }).window = {
    innerHeight: 1200,
    getComputedStyle: (node: Painted) => ({
      backgroundColor: node.bg ?? "rgba(0, 0, 0, 0)",
    }),
  };
}

function fakeRow(top: number, calls: Call[], height = 36, bg?: string) {
  return {
    bg,
    offsetTop: top,
    offsetHeight: height,
    animate(keyframes: Keyframe[], options: KeyframeAnimationOptions) {
      calls.push({ keyframes, options });
      return { cancel() {} } as Animation;
    },
  } as unknown as HTMLElement;
}

function fakeTbody(parentElement: Painted | null = null) {
  return {
    parentElement,
    offsetTop: 0,
    getBoundingClientRect: () => ({ top: 0 }),
    animate() {},
  } as unknown as HTMLElement;
}

test("a moved row slides from its old top; a still or new row does not", () => {
  fakeWindow();
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
    { transform: "translateY(36px)", ...UP },
    { transform: "none", ...UP },
  ]);
  assert.deepEqual(b[0].keyframes, [
    { transform: "translateY(-36px)", ...DOWN },
    { transform: "none", ...DOWN },
  ]);
  assert.deepEqual(a[0].options, {
    duration: FLIP_DURATION_MS,
    easing: FLIP_EASING,
  });
  assert.equal(c.length, 0, "a row that did not move is not animated");
});

test("a row new to the table just appears", () => {
  fakeWindow();
  const fresh: Call[] = [];

  slideRows(
    fakeTbody(),
    new Map([["fresh", fakeRow(0, fresh)]]),
    new Map(),
    new Map(),
  );
  assert.equal(fresh.length, 0);
});

test("a far move is capped and slides opaque, with no fade; an off-screen slide is skipped", () => {
  fakeWindow();
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
    { transform: `translateY(${FLIP_MAX_DISTANCE}px)`, ...FAR_UP },
    { transform: "none", ...FAR_UP },
  ]);
  assert.equal(hidden.length, 0);
});

test("#2458: a sliding row is painted opaque, its own tint over the table's", () => {
  fakeWindow();
  const region = "oklch(0.13 0.028 261.692 / 0.8)";
  const pinned = "oklab(0.29 0.06 -0.1 / 0.2)";
  const page: Painted = { bg: "rgb(0, 0, 0)", parentElement: null };
  // tbody -> table (transparent) -> scroll region (80% gray) -> page (black).
  const tbody = fakeTbody({
    parentElement: { bg: region, parentElement: page },
  });
  const tinted: Call[] = [];
  const plain: Call[] = [];
  const far: Call[] = [];

  slideRows(
    tbody,
    new Map([
      ["tinted", fakeRow(0, tinted, 36, pinned)],
      ["plain", fakeRow(36, plain)],
      ["far", fakeRow(72, far)],
    ]),
    new Map([
      ["tinted", 36],
      ["plain", 0],
      ["far", 900],
    ]),
    new Map(),
  );

  const tintedFill = {
    backgroundColor: "rgb(0, 0, 0)",
    backgroundImage: `linear-gradient(${pinned}, ${pinned}), linear-gradient(${region}, ${region})`,
  };
  const plainFill = {
    backgroundColor: "rgb(0, 0, 0)",
    backgroundImage: `linear-gradient(${region}, ${region})`,
  };

  assert.deepEqual(tinted[0].keyframes, [
    { transform: "translateY(36px)", ...tintedFill, ...UP },
    { transform: "none", ...tintedFill, ...UP },
  ]);
  assert.deepEqual(plain[0].keyframes, [
    { transform: "translateY(-36px)", ...plainFill, ...DOWN },
    { transform: "none", ...plainFill, ...DOWN },
  ]);
  assert.deepEqual(far[0].keyframes, [
    {
      transform: `translateY(${FLIP_MAX_DISTANCE}px)`,
      ...plainFill,
      ...FAR_UP,
    },
    { transform: "none", ...plainFill, ...FAR_UP },
  ]);
});

test("#2458: an opaque table needs no layers; no opaque colour, no fill", () => {
  fakeWindow();
  const onOpaque: Call[] = [];
  const onNothing: Call[] = [];
  const before = new Map([["a", 36]]);

  slideRows(
    fakeTbody({ bg: "rgb(3, 7, 18)", parentElement: null }),
    new Map([["a", fakeRow(0, onOpaque)]]),
    before,
    new Map(),
  );
  slideRows(
    fakeTbody({ bg: "rgba(3, 7, 18, 0.5)", parentElement: null }),
    new Map([["a", fakeRow(0, onNothing)]]),
    before,
    new Map(),
  );

  assert.deepEqual(onOpaque[0].keyframes, [
    { transform: "translateY(36px)", backgroundColor: "rgb(3, 7, 18)", ...UP },
    { transform: "none", backgroundColor: "rgb(3, 7, 18)", ...UP },
  ]);
  assert.deepEqual(onNothing[0].keyframes, [
    { transform: "translateY(36px)", ...UP },
    { transform: "none", ...UP },
  ]);
});

test("#2458: up over down over far rows, far up over far down, all over the sticky name cells", () => {
  assert.ok(FLIP_LAYER.up > FLIP_LAYER.down);
  assert.ok(FLIP_LAYER.down > FLIP_LAYER.farUp);
  assert.ok(FLIP_LAYER.farUp > FLIP_LAYER.farDown);
  // LatencyMatrix's sticky provider cells are `z-10`; its sticky header `z-20`.
  assert.ok(FLIP_LAYER.farDown > 10);
  assert.ok(FLIP_LAYER.up < 20);
});

test("#2458: a far row moving down sits under one moving up", () => {
  fakeWindow();
  const down: Call[] = [];
  slideRows(
    fakeTbody(),
    new Map([["d", fakeRow(828, down)]]),
    new Map([["d", 0]]),
    new Map(),
  );
  assert.equal(down[0].keyframes[0].zIndex, FLIP_LAYER.farDown);
  assert.equal(
    down[0].keyframes[0].transform,
    `translateY(-${FLIP_MAX_DISTANCE}px)`,
  );
  assert.equal(down[0].keyframes[0].opacity, undefined);
});

test("#2458: slides in flight are cancelled before the rows are read", () => {
  fakeWindow();
  let cancelled = 0;
  const inFlight = { cancel: () => (cancelled += 1) } as unknown as Animation;
  const slides = new Map([
    ["a", inFlight],
    ["b", inFlight],
  ]);
  const rows = new Map([["a", fakeRow(0, [])]]);

  slideRows(fakeTbody(), rows, new Map([["a", 36]]), slides);

  assert.equal(cancelled, 2);
  assert.deepEqual(Array.from(slides.keys()), ["a"]);
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
