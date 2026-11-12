/**
 * The /latency podium bars are scaled to the podium's own best score, not to
 * every cell in the table, so the #1 card's bar is always full and the others
 * shrink in proportion to their number (lower is better).
 */
import assert from "node:assert/strict";
import test from "node:test";

import { podiumBarWidth } from "@/components/latency/LatencyMatrix";

test("#1 is a full bar", () => {
  assert.equal(podiumBarWidth(420, 420), 100);
});

test("twice the best number is half the bar", () => {
  assert.equal(podiumBarWidth(840, 420), 50);
});

test("a bar never drops under 10%", () => {
  assert.equal(podiumBarWidth(100_000, 420), 10);
  // A 0% error rate at #1: every worse rate is the shortest bar.
  assert.equal(podiumBarWidth(2.5, 0), 10);
});

test("an all-zero error rate is a full bar for everyone", () => {
  assert.equal(podiumBarWidth(0, 0), 100);
});
