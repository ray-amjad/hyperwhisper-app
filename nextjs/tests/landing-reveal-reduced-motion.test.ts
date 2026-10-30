/**
 * #1180: with the OS "Reduce motion" setting on, the home page's scroll
 * reveals still jumped. Each block is server-rendered at its `initial` offset,
 * and `MotionConfig reducedMotion="user"` skips the transform animation, so the
 * offset snapped to `none` in one frame when the block revealed.
 *
 * Every scroll reveal on the homepage is now the shared `Reveal`
 * (components/landing/Reveal.tsx): up 12px and out of a 4px blur, 400ms
 * ease-out. When `useReducedMotion()` is true it takes `animate` = the shown
 * state with a zero-length transition, so the block is settled and visible at
 * mount and never enters at all. `initial` is the same either way, so the
 * server render and the hydration render still match. (Before hydration,
 * `[data-reveal]` in globals.css shows it from first paint.)
 *
 * The motion itself runs in the browser. This file renders the REAL sections
 * with `framer-motion`'s `m` swapped for a recorder, and pins the props each
 * reveal block receives.
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
  // [file, reveal blocks it renders]. An exact count: a block that stops
  // being a Reveal, or a scraper gone blind, both move it.
  ["../components/landing/LogoMarquee.tsx", 2],
  ["../components/landing/HowItWorks.tsx", 8],
  ["../components/landing/StatsRow.tsx", 4],
  ["../components/landing/FeaturesGrid.tsx", 10],
  ["../components/landing/ModesShowcase.tsx", 1],
  ["../components/landing/PricingSection.tsx", 6],
  ["../components/landing/FAQSection.tsx", 4],
  ["../components/landing/ClosingSection.tsx", 3],
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

const HIDDEN = { opacity: 0, y: 12, filter: "blur(4px)" };
const SHOWN = {
  opacity: 1,
  y: 0,
  filter: "blur(0px)",
  transitionEnd: { filter: "none" },
};

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

  return recorded.filter((p) => "data-reveal" in p);
}

for (const [path, count] of SECTIONS) {
  const name = path.split("/").pop();

  test(`${name}: under Reduce motion every reveal is shown at mount, with no tween`, async () => {
    const blocks = await render(path, true);

    assert.equal(blocks.length, count, "reveal blocks");
    for (const block of blocks) {
      assert.deepEqual(block.animate, SHOWN);
      assert.deepEqual(block.transition, { duration: 0 });
    }
  });

  test(`${name}: with motion allowed, and on the server, it enters on scroll (400ms ease-out)`, async () => {
    for (const reduce of [false, null]) {
      const blocks = await render(path, reduce);

      assert.equal(blocks.length, count);
      for (const block of blocks) {
        assert.equal(block.animate, undefined);
        assert.deepEqual(block.initial, HIDDEN);
        assert.deepEqual(block.whileInView, SHOWN);
        const transition = block.transition as Record<string, unknown>;

        assert.equal(transition.duration, 0.4);
        assert.equal(transition.ease, "easeOut");
      }
    }
  });

  test(`${name}: initial is the same either way, so hydration matches the server`, async () => {
    const served = (await render(path, null)).map((b) => b.initial);
    const reduced = (await render(path, true)).map((b) => b.initial);

    assert.deepEqual(reduced, served);
  });
}
