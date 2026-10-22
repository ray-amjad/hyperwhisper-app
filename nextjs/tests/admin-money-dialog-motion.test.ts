/**
 * #864: the admin Customers page's Add credits and Refund overlays covered
 * and uncovered the page in one frame. Both now render <AnimatedDialog> as
 * the keyed child of an <AnimatePresence>.
 *
 * The motion itself runs in the browser (the Chromium probe in the PR body).
 * This file pins what node can see: the backdrop and the panel start hidden
 * and end at rest, open runs 250 ms and close 150 ms, and a dialog that is
 * exiting is inert, so its stale Refund / Add Credits buttons take no click,
 * focus or key press.
 */
import assert from "node:assert/strict";
import test from "node:test";
import { PresenceContext } from "framer-motion";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";

import {
  AnimatedDialog,
  DIALOG_BACKDROP,
  DIALOG_PANEL,
} from "@/components/AnimatedDialog";

function render(isPresent: boolean) {
  const dialog = createElement(
    AnimatedDialog,
    {
      onBackdropClick: () => {},
      children: createElement("button", { type: "button" }, "Refund Only"),
    },
  );

  return renderToStaticMarkup(
    createElement(
      PresenceContext.Provider,
      {
        value: {
          id: "t",
          initial: undefined,
          isPresent,
          custom: undefined,
          onExitComplete: () => {},
          register: () => () => {},
        },
      },
      dialog,
    ),
  );
}

test("the backdrop fades 0 -> 1 in and back to 0 out", () => {
  assert.equal(DIALOG_BACKDROP.initial.opacity, 0);
  assert.equal(DIALOG_BACKDROP.animate.opacity, 1);
  assert.equal(DIALOG_BACKDROP.exit.opacity, 0);
});

test("the panel fades and scales up from 0.96 and reverses on close", () => {
  assert.deepEqual(DIALOG_PANEL.initial, { opacity: 0, scale: 0.96 });
  assert.equal(DIALOG_PANEL.animate.opacity, 1);
  assert.equal(DIALOG_PANEL.animate.scale, 1);
  assert.equal(DIALOG_PANEL.exit.opacity, 0);
  assert.equal(DIALOG_PANEL.exit.scale, 0.96);
});

test("open runs 250 ms, close 150 ms (the motion tokens)", () => {
  for (const preset of [DIALOG_BACKDROP, DIALOG_PANEL]) {
    assert.equal(preset.animate.transition.duration, 0.25);
    assert.equal(preset.exit.transition.duration, 0.15);
  }
});

test("an open dialog renders its first frame hidden and takes input", () => {
  const html = render(true);
  const root = html.match(/^<div [^>]*>/)?.[0] ?? "";

  assert.match(root, /class="fixed inset-0 z-50 flex items-center justify-center"/);
  assert.doesNotMatch(root, /\binert\b/);
  assert.doesNotMatch(root, /pointer-events-none/);
  assert.match(html, /class="absolute inset-0 bg-black\/60[^"]*" style="opacity:0"/);
  assert.match(html, /style="opacity:0;transform:scale\(0\.96\)"/);
  assert.match(html, /<button type="button">Refund Only<\/button>/);
});

test("an exiting dialog is inert and lets clicks through", () => {
  const root = render(false).match(/^<div [^>]*>/)?.[0] ?? "";

  assert.match(root, /\binert=""/);
  assert.match(root, /pointer-events-none/);
  assert.match(root, /data-closing=""/);
});
