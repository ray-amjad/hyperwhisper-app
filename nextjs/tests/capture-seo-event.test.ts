/**
 * SEO conversion events: `download_clicked` on every download CTA, never
 * `signup_completed` (first-time magic-link verify is not client-visible
 * without changing the redirect — see `src/lib/auth.ts`).
 */
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

import { moduleMock, moduleUrl } from "./module-mock";

const ROOT = fileURLToPath(new URL("..", import.meta.url));
const captured: Array<{ event: string; properties: unknown }> = [];
let client: unknown = null;

moduleMock.module(moduleUrl("../src/lib/posthog-client.ts"), {
  namedExports: { loadPostHog: async () => client },
});

const fakeWindow = {
  location: { pathname: "/de/download" },
};
(globalThis as { window?: unknown }).window = fakeWindow;

type Capture = typeof import("../src/lib/capture-seo-event");

async function load(): Promise<Capture> {
  return (await import("../src/lib/capture-seo-event")) as Capture;
}

test("download_clicked carries cta, platform, path and locale", async () => {
  captured.length = 0;
  client = {
    capture: (event: string, properties: unknown) => {
      captured.push({ event, properties });
    },
  };
  const { captureDownloadClicked } = await load();

  await captureDownloadClicked({ cta: "home_hero", platform: "mac" });

  assert.deepEqual(captured, [
    {
      event: "download_clicked",
      properties: {
        cta: "home_hero",
        platform: "mac",
        path: "/de/download",
        locale: "de",
      },
    },
  ]);
});

test("a header CTA with no platform omits the property", async () => {
  captured.length = 0;
  const { captureDownloadClicked } = await load();

  await captureDownloadClicked({ cta: "header" });

  assert.deepEqual(captured, [
    {
      event: "download_clicked",
      properties: { cta: "header", path: "/de/download", locale: "de" },
    },
  ]);
});

test("no PostHog (no key, blocked chunk): resolves and captures nothing", async () => {
  client = null;
  captured.length = 0;
  const { captureDownloadClicked } = await load();

  await assert.doesNotReject(captureDownloadClicked({ cta: "footer" }));
  assert.equal(captured.length, 0);
});

test("a stubbed capture that throws is swallowed", async () => {
  client = {
    capture: () => {
      throw new Error("blocked");
    },
  };
  const { captureDownloadClicked } = await load();

  await assert.doesNotReject(captureDownloadClicked({ cta: "blog_post" }));
});

test("zh-Hant is read off the first path segment", async () => {
  captured.length = 0;
  fakeWindow.location.pathname = "/zh-Hant";
  client = {
    capture: (event: string, properties: unknown) => {
      captured.push({ event, properties });
    },
  };
  const { captureDownloadClicked } = await load();

  await captureDownloadClicked({ cta: "home_closing", platform: "linux" });

  assert.equal(
    (captured[0]?.properties as { locale: string }).locale,
    "zh-Hant",
  );
  fakeWindow.location.pathname = "/de/download";
});

test("the helper never captures a signup event", () => {
  const source = readFileSync(
    join(ROOT, "src/lib/capture-seo-event.ts"),
    "utf8",
  );
  assert.doesNotMatch(source, /capture\("signup/);
  assert.match(source, /download_clicked/);
  assert.match(source, /newUserCallbackURL/);
});

test("DownloadCta and download-page clicks fire download_clicked", () => {
  const cta = readFileSync(
    join(ROOT, "components/landing/DownloadCta.tsx"),
    "utf8",
  );
  const page = readFileSync(
    join(ROOT, "app/[locale]/download/page.tsx"),
    "utf8",
  );
  const trigger = page.slice(
    page.indexOf("const triggerDownload"),
    page.indexOf("const handleManualDownload"),
  );

  assert.match(cta, /captureDownloadClicked/);
  assert.match(page, /cta: "download_page"/);
  assert.match(page, /handleManualDownload/);
  assert.doesNotMatch(trigger, /captureDownloadClicked/);
});
