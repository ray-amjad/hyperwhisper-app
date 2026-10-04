/**
 * #1131: on `/[locale]/choosing-a-model` the "Your platform" and "Languages
 * you dictate" buttons are single-choice toggles. The chosen one was shown by
 * colour only, so a screen reader could not tell which was chosen. Each button
 * now carries `aria-pressed`, like the "Must have" buttons already did.
 *
 * HOW the click works with no DOM. This repo has no jsdom and no
 * testing-library, and `renderToStaticMarkup` never runs an event handler. So,
 * as in `license-keys-card.test.ts`, the picker is called as a plain function
 * under a tiny hook dispatcher installed on React's internals. The REAL
 * `onClick` is pulled off the "Windows" button and invoked, and the picker is
 * called again to read the state that click left behind.
 *
 * WHAT IT DOES NOT PROVE: a committed browser re-render, or what a screen
 * reader announces. The verify round reads that from Chromium.
 */
import assert from "node:assert/strict";
import test, { beforeEach } from "node:test";
import * as React from "react";
import { renderToStaticMarkup } from "react-dom/server";

import ModelPicker from "@/components/choosing-a-model/ModelPicker";

interface Internals {
  H: unknown;
}

const internals = (
  React as unknown as {
    __CLIENT_INTERNALS_DO_NOT_USE_OR_WARN_USERS_THEY_CANNOT_UPGRADE: Internals;
  }
).__CLIENT_INTERNALS_DO_NOT_USE_OR_WARN_USERS_THEY_CANNOT_UPGRADE;

let slots: unknown[] = [];
let cursor = 0;

const dispatcher = {
  useState<S>(initial: S | (() => S)) {
    const index = cursor++;

    if (slots.length <= index) {
      slots.push(typeof initial === "function" ? (initial as () => S)() : initial);
    }

    const set = (next: S | ((prev: S) => S)) => {
      slots[index] =
        typeof next === "function" ? (next as (prev: S) => S)(slots[index] as S) : next;
    };

    return [slots[index] as S, set];
  },
  useRef<T>(initial: T) {
    const index = cursor++;

    if (slots.length <= index) slots.push({ current: initial });

    return slots[index] as { current: T };
  },
  useMemo<T>(factory: () => T) {
    return factory();
  },
  // Effects never run here: the picker is called, not mounted.
  useEffect() {},
};

interface Element {
  type: unknown;
  props: { children?: unknown; onClick?: () => unknown };
}

const picker = ModelPicker as unknown as (props: {
  measured: Record<string, never>;
  regions: string[];
}) => Element;

/** Calls the real picker under the shim and returns its element tree. */
function renderTree(): Element {
  cursor = 0;
  internals.H = dispatcher;

  try {
    return picker({ measured: {}, regions: [] });
  } finally {
    internals.H = null;
  }
}

/** Depth-first search for the host `<button>` whose children are this text. */
function findButton(node: unknown, text: string): Element | undefined {
  if (Array.isArray(node)) {
    for (const child of node) {
      const hit = findButton(child, text);

      if (hit) return hit;
    }

    return undefined;
  }

  if (!node || typeof node !== "object" || !("props" in node)) return undefined;

  const element = node as Element;

  if (element.type === "button" && element.props.children === text) return element;

  return findButton(element.props.children, text);
}

/** The `aria-pressed` value on the `<button>` whose text is exactly `label`. */
function pressedOf(html: string, label: string): string | null {
  const match = Array.from(html.matchAll(/<button\b([^>]*)>([^<]*)<\/button>/g)).find(
    (m) => m[2] === label,
  );

  assert.ok(match, `no <button> reads "${label}"`);

  return /aria-pressed="([^"]+)"/.exec(match[1])?.[1] ?? null;
}

beforeEach(() => {
  slots = [];
});

test("on first load macOS and English only are pressed, the rest are not", () => {
  const html = renderToStaticMarkup(renderTree() as unknown as React.ReactElement);

  assert.equal(pressedOf(html, "macOS"), "true");
  assert.equal(pressedOf(html, "Windows"), "false");
  assert.equal(pressedOf(html, "English only"), "true");
  assert.equal(pressedOf(html, "European"), "false");
  assert.equal(pressedOf(html, "Wide multilingual"), "false");
});

test("a click on Windows moves the pressed state off macOS", () => {
  const windows = findButton(renderTree(), "Windows");

  assert.ok(windows?.props.onClick, "the picker rendered no Windows button");
  windows.props.onClick();

  const html = renderToStaticMarkup(renderTree() as unknown as React.ReactElement);

  assert.equal(pressedOf(html, "Windows"), "true");
  assert.equal(pressedOf(html, "macOS"), "false");
});
