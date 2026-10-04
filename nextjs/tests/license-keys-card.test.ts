/**
 * #872: the dashboard Account Keys card's copy button when the Clipboard API
 * refuses (insecure context, denied permission, unfocused document).
 *
 * HOW, with no DOM and no click. This repo has no jsdom and no testing-library,
 * and `renderToStaticMarkup` never runs an event handler. So this file calls the
 * card as a plain function under a tiny `useState` dispatcher installed on
 * React's own internals (`H`, the slot every hook reads its dispatcher from),
 * pulls the REAL `onClick` off the copy button in the element tree it returns,
 * invokes it, and then calls the card again to read the state that click left
 * behind. The tree is serialised with the real `renderToStaticMarkup`, which
 * swaps its own dispatcher in and restores ours after.
 *
 * WHAT IT DOES NOT PROVE: a committed client re-render in a browser, or that a
 * screen reader announces the status region.
 */
import assert from "node:assert/strict";
import test, { afterEach, beforeEach } from "node:test";
import * as React from "react";
import { renderToStaticMarkup } from "react-dom/server";

// A variable specifier: a literal `.tsx` specifier is TS5097 under this
// tsconfig (the same rule the other component tests here document).
const CARD_PATH = "../components/customer/dashboard/LicenseKeysCard.tsx";

const FAILURE = "Copy failed — reveal the key and copy it by hand";

// Built from parts so no credential-shaped literal sits in the source.
const SAMPLE = ["AAAA", "BBBB", "CCCC", "DDDD"].join("-");

interface Internals {
  H: unknown;
}

const internals = (
  React as unknown as {
    __CLIENT_INTERNALS_DO_NOT_USE_OR_WARN_USERS_THEY_CANNOT_UPGRADE: Internals;
  }
).__CLIENT_INTERNALS_DO_NOT_USE_OR_WARN_USERS_THEY_CANNOT_UPGRADE;

/** One mounted card's hook state, kept across calls in call order. */
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
};

interface Element {
  type: unknown;
  props: { children?: unknown; title?: string; onClick?: () => unknown };
}

type Card = (props: {
  licenses: { id: string; key: string; status: string }[];
}) => Element;

/** Calls the real card under the shim and returns its element tree. */
async function renderCard(): Promise<Element> {
  const { default: LicenseKeysCard } = (await import(CARD_PATH)) as {
    default: Card;
  };

  cursor = 0;
  internals.H = dispatcher;

  try {
    return LicenseKeysCard({
      licenses: [{ id: "l1", key: SAMPLE, status: "granted" }],
    });
  } finally {
    internals.H = null;
  }
}

/** Depth-first search of host elements for the first `title` match. */
function findByTitle(node: unknown, title: string): Element | undefined {
  if (Array.isArray(node)) {
    for (const child of node) {
      const hit = findByTitle(child, title);

      if (hit) return hit;
    }

    return undefined;
  }

  if (!node || typeof node !== "object" || !("props" in node)) return undefined;

  const element = node as Element;

  if (element.props.title === title) return element;

  return findByTitle(element.props.children, title);
}

/** Clicks the copy button and lets the handler's promise settle. */
async function clickCopy(): Promise<unknown> {
  const button = findByTitle(await renderCard(), "Copy to clipboard");

  assert.ok(button?.props.onClick, "the card rendered no copy button");

  const returned = button.props.onClick();

  await new Promise((resolve) => setImmediate(resolve));

  return returned;
}

const rejections: unknown[] = [];
const onRejection = (reason: unknown) => rejections.push(reason);
const originalNavigator = Object.getOwnPropertyDescriptor(globalThis, "navigator");

function stubWriteText(writeText: (text: string) => Promise<void>) {
  Object.defineProperty(globalThis, "navigator", {
    configurable: true,
    value: { clipboard: { writeText } },
  });
}

beforeEach(() => {
  slots = [];
  rejections.length = 0;
  process.on("unhandledRejection", onRejection);
});

afterEach(() => {
  process.off("unhandledRejection", onRejection);

  if (originalNavigator) {
    Object.defineProperty(globalThis, "navigator", originalNavigator);
  }
});

test("a refused copy settles quietly and shows the failure message", async () => {
  const written: string[] = [];

  stubWriteText(async (text) => {
    written.push(text);
    throw new DOMException("Document is not focused.", "NotAllowedError");
  });

  // The button's handler returns nothing: the promise is consumed, not handed
  // to React, so a rejection inside it cannot escape the click.
  assert.equal(await clickCopy(), undefined);
  assert.deepEqual(written, [SAMPLE]);
  assert.deepEqual(rejections, []);

  const markup = renderToStaticMarkup(
    (await renderCard()) as unknown as React.ReactElement,
  );

  assert.match(markup, /role="status"/);
  assert.ok(markup.includes(FAILURE), "the failure message was not rendered");
  assert.ok(!markup.includes("M5 13l4 4L19 7"), "a refused copy showed the tick");
});

test("a successful copy shows the tick and no failure message", async () => {
  stubWriteText(async () => {});

  await clickCopy();

  assert.deepEqual(rejections, []);

  const markup = renderToStaticMarkup(
    (await renderCard()) as unknown as React.ReactElement,
  );

  // The tick glyph the card swaps in on `isCopied`.
  assert.ok(markup.includes("M5 13l4 4L19 7"), "the copied tick was not rendered");
  assert.ok(!markup.includes(FAILURE));
});

test("a successful copy after a refused one clears the message", async () => {
  stubWriteText(async () => {
    throw new Error("denied");
  });
  await clickCopy();

  stubWriteText(async () => {});
  await clickCopy();

  const markup = renderToStaticMarkup(
    (await renderCard()) as unknown as React.ReactElement,
  );

  assert.ok(!markup.includes(FAILURE));
  assert.ok(markup.includes("M5 13l4 4L19 7"));
});
