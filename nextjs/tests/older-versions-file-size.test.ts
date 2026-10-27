/**
 * #2086: on /older-versions every Windows card printed ONE file size, taken
 * from `downloads[0]` (the ARM64 item, listed first in `appcast-windows.xml`),
 * above both the ARM64 and the x64 buttons. The x64 installer is ~40-50 MB
 * bigger, so every Windows card understated the x64 download.
 *
 * The fix puts each file's own size on its own button and drops the shared
 * size line when a card has more than one download. A single-download card
 * (every macOS card) keeps its one size line.
 *
 * This file renders the REAL page with the real `public/appcast-windows.xml`
 * and `public/appcast.xml` data. `renderToStaticMarkup` renders once and runs
 * no effect, so the fetch never happens: `react` is mocked with the real
 * module plus a `useState` that hands the page the parsed versions, the
 * platform under test and `loading: false`. Every other `useState` call goes
 * to React untouched. The parse below mirrors the page's own (`sparkle:version`
 * groups the variants, `sparkle:os` names the arch, `length` is the size); the
 * page's parse runs on the browser's DOMParser, which Node does not have.
 */
import assert from "node:assert/strict";
import { createRequire } from "node:module";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

import { moduleMock } from "./module-mock";

const ROOT = fileURLToPath(new URL("..", import.meta.url));
const require = createRequire(import.meta.url);

type Variant = { arch: string; downloadUrl: string; fileSize: number };
type Version = {
  version: string;
  pubDate: string;
  buildNumber: string;
  minimumSystemVersion: string;
  downloads: Variant[];
};
type Platform = "mac" | "windows";

/** The appcast's items grouped by build, as the page groups them. */
function parseAppcast(file: string): Version[] {
  const xml = readFileSync(join(ROOT, "public", file), "utf8");
  const tag = (item: string, name: string): string =>
    item.match(new RegExp(`<${name}>([^<]*)</${name}>`))?.[1] ?? "";
  const byBuild = new Map<string, Version>();

  for (const [, item] of Array.from(xml.matchAll(/<item>([\s\S]*?)<\/item>/g))) {
    const buildNumber = tag(item, "sparkle:version");
    const downloadUrl = item.match(/<enclosure[^>]*\burl="([^"]+)"/)?.[1] ?? "";
    const fileSize = parseInt(
      item.match(/<enclosure[^>]*\blength="([^"]*)"/)?.[1] || "0",
    );
    const os = tag(item, "sparkle:os");

    if (!buildNumber || !downloadUrl) continue;
    const arch = os.includes("arm64") ? "ARM64" : os.includes("x64") ? "x64" : "";
    const variant = { arch, downloadUrl, fileSize };
    const existing = byBuild.get(buildNumber);

    if (existing) existing.downloads.push(variant);
    else
      byBuild.set(buildNumber, {
        version: tag(item, "sparkle:shortVersionString") || buildNumber,
        pubDate: tag(item, "pubDate"),
        buildNumber,
        minimumSystemVersion: tag(item, "sparkle:minimumSystemVersion"),
        downloads: [variant],
      });
  }

  return Array.from(byBuild.values());
}

/** The page's own formatter: `(bytes / 1 MiB).toFixed(2) + " MB"`. */
const mb = (bytes: number): string => `${(bytes / (1024 * 1024)).toFixed(2)} MB`;

/** What the next render's `useState` calls start with; `null` = React's own. */
let forced: { versions: Version[]; platform: Platform } | null = null;

const realReact = require("react") as typeof import("react");
// Captured first: the mock below can write its exports back onto this object.
const realUseState = realReact.useState;

moduleMock.module("react", {
  defaultExport: realReact,
  namedExports: {
    ...realReact,
    useState: (initial: unknown) => {
      if (forced === null) return realUseState(initial);
      // `versions` starts as [], `loading` as true, both platforms as "mac".
      if (Array.isArray(initial)) return [forced.versions, () => {}];
      if (initial === true) return [false, () => {}];
      if (initial === "mac") return [forced.platform, () => {}];

      return realUseState(initial);
    },
  },
});

const messages = (
  JSON.parse(readFileSync(join(ROOT, "messages/en.json"), "utf8")) as {
    olderVersions: Record<string, string>;
  }
).olderVersions;

moduleMock.module("next-intl", {
  namedExports: {
    useLocale: () => "en",
    useTranslations:
      () =>
      (key: string, values: Record<string, string> = {}) =>
        (messages[key] ?? key).replace(/\{(\w+)\}/g, (_, k: string) => values[k] ?? ""),
  },
});

// A variable specifier, and deferred: a static import would bind before the
// mocks above, and a literal `.tsx` specifier is a TS5097 error here.
const PAGE_PATH = "../app/[locale]/older-versions/page.tsx";

async function render(platform: Platform, versions: Version[]): Promise<string> {
  const { createElement } = await import("react");
  const { renderToStaticMarkup } = await import("react-dom/server");
  const { default: Page } = (await import(PAGE_PATH)) as {
    default: () => null;
  };

  forced = { versions, platform };
  try {
    return renderToStaticMarkup(createElement(Page));
  } finally {
    forced = null;
  }
}

/** The markup of the card that holds the link to `href`. */
function cardFor(html: string, href: string): string {
  const at = html.indexOf(`href="${href}"`);

  assert.ok(at > 0, `no link to ${href} in the page`);
  // Each card holds one `<h3>` (its version), so a card runs from its own
  // heading to the next card's.
  const start = html.lastIndexOf("<h3", at);
  const end = html.indexOf("<h3", at);

  return html.slice(start, end === -1 ? undefined : end);
}

/** The markup of the `<a>` whose href is `href`, up to its close. */
function buttonFor(html: string, href: string): string {
  const at = html.indexOf(`href="${href}"`);
  const start = html.lastIndexOf("<a", at);

  return html.slice(start, html.indexOf("</a>", at));
}

const windows = parseAppcast("appcast-windows.xml");
const mac = parseAppcast("appcast.xml");

test("fixture: every Windows version has an ARM64 and an x64 file of different sizes", () => {
  assert.ok(windows.length > 0);
  for (const v of windows) {
    const arches = v.downloads.map((d) => d.arch).sort();

    assert.deepEqual(arches, ["ARM64", "x64"], `v${v.version}`);
    assert.notEqual(v.downloads[0].fileSize, v.downloads[1].fileSize, `v${v.version}`);
  }
});

test("each Windows download button states its own file's size", async () => {
  const html = await render("windows", windows);

  for (const v of windows) {
    for (const dl of v.downloads) {
      const button = buttonFor(html, dl.downloadUrl);

      assert.ok(button.includes(dl.arch), `v${v.version} ${dl.arch} label`);
      assert.ok(
        button.includes(mb(dl.fileSize)),
        `v${v.version} ${dl.arch} button must state ${mb(dl.fileSize)}: ${button}`,
      );
    }
  }
});

test("a Windows card prints no shared size: each size appears once, on its own button", async () => {
  const html = await render("windows", windows);

  for (const v of windows) {
    const card = cardFor(html, v.downloads[0].downloadUrl);

    for (const dl of v.downloads) {
      const count = card.split(mb(dl.fileSize)).length - 1;

      assert.equal(count, 1, `v${v.version}: ${mb(dl.fileSize)} printed ${count} times`);
    }
  }
});

test("a single-download macOS card keeps its one size line and a plain button", async () => {
  assert.ok(mac.length > 0 && mac.every((v) => v.downloads.length === 1));
  const html = await render("mac", mac);

  for (const v of mac) {
    const [dl] = v.downloads;
    const card = cardFor(html, dl.downloadUrl);
    const button = buttonFor(html, dl.downloadUrl);
    const size = dl.fileSize ? mb(dl.fileSize) : messages.unknownSize;

    assert.ok(card.includes(size), `v${v.version}: size line ${size}`);
    assert.ok(!button.includes(size), `v${v.version}: the button carries no size`);
    assert.ok(button.includes(messages.downloadDmg), `v${v.version}: button label`);
  }
});
