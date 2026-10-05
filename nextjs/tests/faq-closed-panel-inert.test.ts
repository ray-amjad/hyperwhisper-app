/**
 * #1119: a collapsed FAQ answer kept its links in the Tab order.
 *
 * A closed panel is a `grid-rows-[0fr]` track whose `overflow-hidden` child is
 * 0px tall, so its links are invisible, but CSS collapse does not take them out
 * of the focus order. A keyboard user tabbing through `/en` landed on links
 * they could not see. The fix marks every closed `faq-panel-*` div `inert`.
 *
 * This file renders the REAL `FAQSection` with the real English answers and
 * checks, from the markup:
 *   - closed (the initial state): every panel carries `inert`, and the closed
 *     panels really do hold links, so the check is not vacuous;
 *   - one item open: that panel is NOT inert and its links are in it, while
 *     every other panel stays inert.
 *
 * HOW the open state is reached with no DOM and no click: `renderToStaticMarkup`
 * renders once, with no events. So `react` is mocked with the real module plus
 * a `useState` that, for the `useState<number | null>(null)` call that holds
 * `openIndex`, returns the index under test. Every other `useState` call goes
 * to React untouched. That proves the render-side contract (`inert` follows
 * `openIndex`); the browser behaviour of `inert` itself is the platform's.
 */
import assert from "node:assert/strict";
import { createRequire } from "node:module";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import test, { mock } from "node:test";
import { fileURLToPath } from "node:url";

/**
 * `mock.module` is a real Node 22 API (behind `--experimental-test-module-mocks`,
 * which `npm test` passes) but this repo pins `@types/node@20`, which has no
 * declaration for it. Narrowed to the one option used here.
 */
interface ModuleMocker {
  module(
    specifier: string,
    options: { namedExports?: Record<string, unknown>; defaultExport?: unknown },
  ): void;
}

const moduleMock = mock as unknown as ModuleMocker;

const ROOT = fileURLToPath(new URL("..", import.meta.url));
const require = createRequire(import.meta.url);

type Faq = {
  questions: Record<string, { question: string; answer: string }>;
} & Record<string, unknown>;

const faq = (
  JSON.parse(readFileSync(join(ROOT, "messages/en.json"), "utf8")) as {
    faq: Faq;
  }
).faq;

/** The `openIndex` the next render starts with; `null` leaves React alone. */
let forcedOpenIndex: number | null = null;

const realReact = require("react") as typeof import("react");
// Captured first: the mock below can write its exports back onto this object.
const realUseState = realReact.useState;

moduleMock.module("react", {
  defaultExport: realReact,
  namedExports: {
    ...realReact,
    useState: (initial: unknown) =>
      initial === null && forcedOpenIndex !== null
        ? [forcedOpenIndex, () => {}]
        : realUseState(initial),
  },
});

/** `t` reads the real English `faq` messages, so the answers carry real links. */
moduleMock.module("next-intl", {
  namedExports: {
    useTranslations: () => (key: string) =>
      key.split(".").reduce<unknown>(
        (node, part) => (node as Record<string, unknown>)[part],
        faq,
      ),
  },
});

// A variable specifier, and deferred: a static import would bind before the
// mocks above, and a literal `.tsx` specifier is a TS5097 error here.
const FAQ_PATH = "../components/landing/FAQSection.tsx";

async function render(openIndex: number | null): Promise<string> {
  const { createElement } = await import("react");
  const { renderToStaticMarkup } = await import("react-dom/server");
  const { default: FAQSection } = (await import(FAQ_PATH)) as {
    default: () => null;
  };

  forcedOpenIndex = openIndex;
  try {
    return renderToStaticMarkup(createElement(FAQSection));
  } finally {
    forcedOpenIndex = null;
  }
}

type Panel = { key: string; open: string; body: string };

/** Each `faq-panel-*` div: its key, its opening tag, and the markup inside it. */
function panels(html: string): Panel[] {
  return Array.from(
    html.matchAll(
      /(<div\b[^>]*\bid="faq-panel-([^"]+)"[^>]*>)([\s\S]*?)<\/p><\/div><\/div>/g,
    ),
  ).map((m) => ({ key: m[2], open: m[1], body: m[3] }));
}

const isInert = (panel: Panel): boolean => /\sinert=""/.test(panel.open);
const keys = Object.keys(faq.questions);

test("every closed FAQ panel is inert, and the closed panels hold links", async () => {
  const all = panels(await render(null));

  assert.equal(all.length, keys.length, "one panel per FAQ item");
  assert.deepEqual(
    all.filter((p) => !isInert(p)).map((p) => p.key),
    [],
    "a closed panel is not inert, so its links stay in the Tab order",
  );
  // Not vacuous: the inert panels really contain the links #1119 tabbed onto.
  assert.ok(
    all.filter((p) => /<a\b/.test(p.body)).length > 0,
    "no closed panel holds a link",
  );
  // The load-bearing child that collapses the 0fr track is still there.
  for (const p of all) {
    assert.match(p.body, /^<div class="overflow-hidden">/);
  }
});

test("opening an FAQ item makes its panel non-inert and keeps the rest inert", async () => {
  // Every item whose answer has a link, opened by its position in the list.
  const closed = panels(await render(null)).map((p) => p.key);
  const withLinks = closed.filter((key) => /\]\(/.test(faq.questions[key].answer));

  assert.ok(withLinks.length > 0, "no FAQ answer has a link to test");

  for (const key of withLinks) {
    const all = panels(await render(closed.indexOf(key)));
    const open = all.filter((p) => !isInert(p));

    assert.deepEqual(
      open.map((p) => p.key),
      [key],
      `opening ${key} must un-inert exactly its own panel`,
    );
    assert.match(open[0].body, /<a\b[^>]*href=/, `${key}'s link is in its panel`);
  }
});
