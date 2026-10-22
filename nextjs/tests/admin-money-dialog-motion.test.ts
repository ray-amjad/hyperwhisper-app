/**
 * #864: the admin Customers page's Add credits and Refund overlays covered
 * and uncovered the page in one frame. They now enter and leave through
 * framer-motion's <AnimatePresence>, with the presets in lib/modal-motion.ts.
 *
 * The motion itself runs in the browser (the Chromium probe in the PR body).
 * This file pins what node can see: the presets start hidden and end at rest,
 * the exit is short enough for the "gone within 300 ms" bar, the root stops
 * taking clicks while it closes, and both overlays are wired to them.
 */
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

import {
  MODAL_BACKDROP,
  MODAL_DIALOG,
  MODAL_ROOT,
} from "@/lib/modal-motion";

const CLIENT = readFileSync(
  new URL(
    "../app/[locale]/user/(authenticated)/customers/CustomersClient.tsx",
    import.meta.url,
  ),
  "utf8",
);

test("the backdrop fades 0 -> 1 in and back to 0 out", () => {
  assert.equal(MODAL_BACKDROP.initial.opacity, 0);
  assert.equal(MODAL_BACKDROP.animate.opacity, 1);
  assert.equal(MODAL_BACKDROP.exit.opacity, 0);
});

test("the dialog fades and scales up from 0.96 and reverses on close", () => {
  assert.deepEqual(MODAL_DIALOG.initial, { opacity: 0, scale: 0.96 });
  assert.equal(MODAL_DIALOG.animate.opacity, 1);
  assert.equal(MODAL_DIALOG.animate.scale, 1);
  assert.equal(MODAL_DIALOG.exit.opacity, 0);
  assert.equal(MODAL_DIALOG.exit.scale, 0.96);
});

test("open runs 250 ms, close 150 ms (the motion tokens)", () => {
  for (const preset of [MODAL_BACKDROP, MODAL_DIALOG]) {
    assert.equal(preset.animate.transition.duration, 0.25);
    assert.equal(preset.exit.transition.duration, 0.15);
  }
});

test("a closing overlay takes no clicks", () => {
  assert.equal(MODAL_ROOT.exit.pointerEvents, "none");
});

test("both money overlays render inside AnimatePresence with the presets", () => {
  for (const target of ["refundTarget", "creditTarget"]) {
    const block = CLIENT.match(
      new RegExp(
        `<AnimatePresence>\\s*\\{${target} && \\(([\\s\\S]*?)\\)\\}\\s*</AnimatePresence>`,
      ),
    );
    assert.ok(block, `${target} overlay is not wrapped in <AnimatePresence>`);
    assert.match(block[1], /<m\.div\s+key="[a-z]+-modal"\s+\{\.\.\.MODAL_ROOT\}/);
    assert.match(block[1], /\{\.\.\.MODAL_BACKDROP\}/);
    assert.match(block[1], /\{\.\.\.MODAL_DIALOG\}/);
  }
});
