/**
 * The PostHog loader, `src/lib/posthog-client.ts` (#918). PostHog must stay
 * out of every route's initial bundle, and a caller must never get a client
 * that `init` has not run on: posthog-js drops a capture on an un-initialised
 * instance silently, and a page's effects run before the provider's.
 */
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test, { mock } from "node:test";

import { redactAccountKeyFromEvent } from "../src/lib/account-key-redaction";

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

const env: Record<string, string | undefined> = {};

moduleMock.module("../src/env/client.mjs", { namedExports: { env } });

const inits: Array<{ key: string; options: unknown; href: string }> = [];
const fakePostHog = {
  init: (key: string, options: unknown) => {
    inits.push({ key, options, href: fakeWindow.location.href });
  },
};

// The page PostHog starts on: opened with the Account Key in its URL (#1901).
const PAGE_ORIGIN = "https://hyperwhisper.com";
const fakeWindow = {
  location: new URL(
    `${PAGE_ORIGIN}/en?licenseKey=HW-AB12-CD34-EF56-GH78&license_key=HW-AB12-CD34-EF56-GH78&utm_source=app#faq`,
  ),
  history: {
    state: { next: "router-state" },
    replaceStates: [] as Array<{ state: unknown; url: string }>,
    replaceState(state: unknown, _unused: string, url: string) {
      this.replaceStates.push({ state, url });
      fakeWindow.location = new URL(url, PAGE_ORIGIN);
    },
  },
};
(globalThis as { window?: unknown }).window = fakeWindow;

moduleMock.module("posthog-js", { defaultExport: fakePostHog });

const LOADER_PATH = "../src/lib/posthog-client.ts";

async function loader(): Promise<() => Promise<unknown>> {
  return (
    (await import(LOADER_PATH)) as { loadPostHog: () => Promise<unknown> }
  ).loadPostHog;
}

test("no key: answers null and never inits", async () => {
  const loadPostHog = await loader();

  assert.equal(await loadPostHog(), null);
  assert.equal(inits.length, 0);
});

test("with a key: inits once, with today's options, before any caller gets it", async () => {
  env.NEXT_PUBLIC_POSTHOG_KEY = "phc_test";
  const loadPostHog = await loader();

  // What a caller sees the moment its promise settles: init has already run.
  const initsSeenByCaller = await loadPostHog().then(() => inits.length);
  const [first, second] = await Promise.all([loadPostHog(), loadPostHog()]);

  assert.equal(initsSeenByCaller, 1);

  assert.equal(first, fakePostHog);
  assert.equal(second, fakePostHog);
  assert.equal(await loadPostHog(), fakePostHog);
  assert.deepEqual(inits, [
    {
      key: "phc_test",
      href: `${PAGE_ORIGIN}/en?utm_source=app#faq`,
      options: {
        api_host: "https://us.i.posthog.com",
        person_profiles: "always",
        before_send: redactAccountKeyFromEvent,
      },
    },
  ]);
});

test("the Account Key leaves the address bar before init, once, keeping the router state (#1901)", () => {
  assert.deepEqual(fakeWindow.history.replaceStates, [
    { state: { next: "router-state" }, url: "/en?utm_source=app#faq" },
  ]);
  assert.doesNotMatch(inits[0]?.href ?? "", /HW-/);
});

test("the before_send hook it inits with takes the Account Key out of a $pageview (#1897)", () => {
  const options = inits[0]?.options as {
    before_send: (event: unknown) => unknown;
  };
  const url =
    "https://hyperwhisper.com/en/user/sign-in?licenseKey=HW-AB12-CD34-EF56-GH78&returnTo=%2Fen%2Fuser%2Fdashboard";

  const sent = options.before_send({
    event: "$pageview",
    properties: { $current_url: url, $pathname: "/en/user/sign-in" },
    $set_once: { $initial_current_url: url },
  });

  assert.doesNotMatch(JSON.stringify(sent), /HW-/);
  assert.deepEqual(sent, {
    event: "$pageview",
    properties: {
      $current_url:
        "https://hyperwhisper.com/en/user/sign-in?licenseKey=[redacted]&returnTo=%2Fen%2Fuser%2Fdashboard",
      $pathname: "/en/user/sign-in",
    },
    $set_once: {
      $initial_current_url:
        "https://hyperwhisper.com/en/user/sign-in?licenseKey=[redacted]&returnTo=%2Fen%2Fuser%2Fdashboard",
    },
  });
});

test("no globally-mounted or caller file imports PostHog statically", () => {
  for (const file of [
    "contexts/PostHogProvider.tsx",
    "app/[locale]/purchase-success/page.tsx",
    "components/customer/dashboard/CloudCreditsCard.tsx",
    "src/lib/posthog-client.ts",
  ]) {
    const source = readFileSync(new URL(`../${file}`, import.meta.url), "utf8");

    assert.doesNotMatch(
      source,
      /^import (?!type )[^;]*from "posthog-js/m,
      `${file} imports posthog-js at module scope`,
    );
  }
});
