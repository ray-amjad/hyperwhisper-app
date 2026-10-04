/**
 * #1303 (sibling of #1131): the platform switch on `/[locale]/download`
 * (macOS / Windows / Linux) and on `/[locale]/older-versions` (macOS /
 * Windows) showed the chosen platform by colour only. Each HeroUI `<Button>`
 * now carries `aria-pressed`, and HeroUI forwards it to the rendered
 * `<button>`; this file proves that from the REAL page components' markup.
 *
 * HOW a non-default platform is reached with no DOM and no click:
 * `renderToStaticMarkup` renders once, with no events. So `react` is mocked
 * with the real module plus a `useState` that, for a `useState("mac")` call
 * (the page's `selectedPlatform`), returns the platform under test. Every
 * other `useState` call goes to React untouched. On older-versions the same
 * initial also seeds `listPlatform`, which only picks list labels.
 *
 * WHAT IT DOES NOT PROVE: a real click, or what a screen reader announces.
 */
import assert from "node:assert/strict";
import { createRequire } from "node:module";
import test, { mock } from "node:test";

/**
 * `mock.module` is a real Node 22 API (behind `--experimental-test-module-mocks`,
 * which `npm test` passes) but this repo pins `@types/node@20`, which has no
 * declaration for it. Narrowed to the one option used here.
 */
interface ModuleMocker {
  module(
    specifier: string,
    options: {
      namedExports?: Record<string, unknown>;
      defaultExport?: unknown;
    },
  ): void;
}

const moduleMock = mock as unknown as ModuleMocker;
const require = createRequire(import.meta.url);

const realReact = require("react") as typeof import("react");
// Captured first: the mock below can write its exports back onto this object.
const realUseState = realReact.useState;

/** The platform the next render starts on; `null` leaves React alone. */
let forcedPlatform: string | null = null;

moduleMock.module("react", {
  defaultExport: realReact,
  namedExports: {
    ...realReact,
    useState: (initial: unknown) =>
      initial === "mac" && forcedPlatform !== null
        ? [forcedPlatform, () => {}]
        : realUseState(initial),
  },
});

moduleMock.module("next-intl", {
  namedExports: { useTranslations: () => (key: string) => key },
});

moduleMock.module("next/navigation", {
  namedExports: { useSearchParams: () => new URLSearchParams() },
});

// Variable specifiers, and deferred: a static import would bind before the
// mocks above, and a literal `.tsx` specifier is a TS5097 error here.
const DOWNLOAD_PATH = "../app/[locale]/download/page.tsx";
const OLDER_PATH = "../app/[locale]/older-versions/page.tsx";

async function render(path: string, platform: string): Promise<string> {
  const { createElement } = await import("react");
  const { renderToStaticMarkup } = await import("react-dom/server");
  const { default: Page } = (await import(path)) as { default: () => null };

  forcedPlatform = platform;
  try {
    return renderToStaticMarkup(createElement(Page));
  } finally {
    forcedPlatform = null;
  }
}

/** The `aria-pressed` value on the `<button>` whose text is exactly `label`. */
function pressedOf(html: string, label: string): string | null {
  const matches = Array.from(
    html.matchAll(/<button\b([^>]*)>([^<]*)<\/button>/g),
  ).filter((m) => m[2] === label);

  assert.equal(matches.length, 1, `expected one <button> reading "${label}"`);

  return /aria-pressed="([^"]+)"/.exec(matches[0][1])?.[1] ?? null;
}

const PAGES = [
  {
    name: "download",
    path: DOWNLOAD_PATH,
    options: [
      ["mac", "macOS"],
      ["windows", "Windows"],
      ["linux", "Linux"],
    ],
  },
  {
    name: "older-versions",
    path: OLDER_PATH,
    options: [
      ["mac", "macOS"],
      ["windows", "Windows"],
    ],
  },
] as const;

for (const page of PAGES) {
  for (const [chosen] of page.options) {
    test(`${page.name}: with ${chosen} chosen, only its platform button is pressed`, async () => {
      const html = await render(page.path, chosen);

      for (const [id, label] of page.options) {
        assert.equal(
          pressedOf(html, label),
          id === chosen ? "true" : "false",
          label,
        );
      }
    });
  }
}
