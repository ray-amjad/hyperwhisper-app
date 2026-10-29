/**
 * #1180: with the OS "Reduce motion" setting on, the home page's scroll
 * reveals still jumped. Each block is server-rendered at its `initial` offset
 * (`translateY(12px)` / `translateY(20px)`), and `MotionConfig
 * reducedMotion="user"` skips the y animation, so the offset snapped to `none`
 * in one frame when the block revealed.
 *
 * Every `whileInView` block in FeaturesGrid, VideoDemo, PricingSection and
 * FAQSection now takes `animate={{ y: 0 }}` when `useReducedMotion()` is true:
 * the offset settles at mount, while the block is still invisible, and the
 * reveal is a pure fade. `initial` is the same either way, so the server render
 * and the hydration render still match.
 *
 * The motion itself runs in the browser (the Chromium probe in the PR body).
 * This file renders the REAL sections with `framer-motion`'s `m` swapped for a
 * recorder, and pins the props each reveal block receives.
 */
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { createRequire } from "node:module";
import { pathToFileURL } from "node:url";
import test from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { NextIntlClientProvider } from "next-intl";

import { moduleMock } from "./module-mock";

// VARIABLE specifiers: a literal `.tsx` specifier is a TS5097 error under this
// tsconfig. Same rule the other tests in this folder document.
const CONTEXT_PATH = "../contexts/DownloadModalContext.tsx";
const SECTIONS = [
  // [file, reveal blocks with a y offset]: 24 in all, the count the probe saw
  // as `translateY` inline styles in the served /en markup.
  ["../components/landing/FeaturesGrid.tsx", 13],
  ["../components/landing/VideoDemo.tsx", 5],
  ["../components/landing/PricingSection.tsx", 4],
  ["../components/landing/FAQSection.tsx", 2],
] as const;

type Props = Record<string, unknown> & { children?: React.ReactNode };

let reduceMotion: boolean | null = null;
let recorded: Props[] = [];

// Each `m.<tag>` records its props and renders a plain <tag>.
const recorder = new Proxy(
  {},
  {
    get: (_target, tag: string) => (props: Props) => {
      recorded.push(props);

      return createElement(tag, {
        className: props.className as string | undefined,
        id: props.id as string | undefined,
        children: props.children,
      });
    },
  },
);

// tsx compiles the sections to CJS, so they require framer-motion's
// dist/cjs entry: mock that file, over the real exports. (A bare
// "framer-motion" mock would hit the ES entry and record nothing.)
const requireReal = createRequire(import.meta.url);
const motionCjs = requireReal.resolve("framer-motion");
const motionMock = {
  namedExports: {
    ...(requireReal("framer-motion") as Record<string, unknown>),
    m: recorder,
    useReducedMotion: () => reduceMotion,
  },
};

moduleMock.module(pathToFileURL(motionCjs).href, motionMock);

let DownloadModalProvider: (p: {
  children: React.ReactNode;
}) => React.ReactElement;

test.before(async () => {
  ({ DownloadModalProvider } = (await import(CONTEXT_PATH)) as {
    DownloadModalProvider: typeof DownloadModalProvider;
  });
});

const messages = JSON.parse(
  readFileSync(new URL("../messages/en.json", import.meta.url), "utf8"),
) as Record<string, unknown>;

/** The y a block starts at: its own `initial`, or the `hidden` variant it inherits. */
function initialY(props: Props): number | undefined {
  const initial = props.initial as Record<string, unknown> | string | undefined;

  if (initial && typeof initial === "object") return initial.y as number;
  const variants = props.variants as
    | Record<string, Record<string, unknown>>
    | undefined;

  return variants?.hidden?.y as number | undefined;
}

async function render(path: string, reduce: boolean | null): Promise<Props[]> {
  const { default: Section } = (await import(path)) as {
    default: () => React.ReactElement;
  };

  reduceMotion = reduce;
  recorded = [];
  renderToStaticMarkup(
    createElement(NextIntlClientProvider, {
      locale: "en",
      messages,
      timeZone: "UTC",
      children: createElement(DownloadModalProvider, {
        children: createElement(Section),
      }),
    }),
  );

  // A reveal block with an offset: in-view triggered, itself or via its parent.
  return recorded.filter((p) => {
    const y = initialY(p);

    return typeof y === "number" && y !== 0;
  });
}

for (const [path, count] of SECTIONS) {
  const name = path.split("/").pop();

  test(`${name}: under Reduce motion every offset reveal block settles y to 0 at mount`, async () => {
    const blocks = await render(path, true);

    assert.equal(blocks.length, count, "reveal blocks with a y offset");
    for (const block of blocks) {
      assert.deepEqual(
        block.animate,
        { y: 0 },
        `block ${String(block.className)} keeps its offset until it reveals`,
      );
    }
  });

  test(`${name}: with motion allowed, and on the server, the slide-in is unchanged`, async () => {
    for (const reduce of [false, null]) {
      const blocks = await render(path, reduce);

      assert.equal(blocks.length, count);
      for (const block of blocks) {
        assert.equal(block.animate, undefined);
      }
    }
  });

  test(`${name}: initial is the same either way, so hydration matches the server`, async () => {
    const served = (await render(path, null)).map((b) => b.initial);
    const reduced = (await render(path, true)).map((b) => b.initial);

    assert.deepEqual(reduced, served);
  });
}
