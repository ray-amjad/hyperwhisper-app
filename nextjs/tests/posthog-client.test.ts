/**
 * The PostHog loader, `src/lib/posthog-client.ts` (#918). PostHog must stay
 * out of every route's initial bundle, and a caller must never get a client
 * that `init` has not run on: posthog-js drops a capture on an un-initialised
 * instance silently, and a page's effects run before the provider's.
 */
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test, { mock } from "node:test";

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

const inits: Array<{ key: string; options: unknown }> = [];
const fakePostHog = {
  init: (key: string, options: unknown) => {
    inits.push({ key, options });
  },
};

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
      options: {
        api_host: "https://us.i.posthog.com",
        person_profiles: "always",
      },
    },
  ]);
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
