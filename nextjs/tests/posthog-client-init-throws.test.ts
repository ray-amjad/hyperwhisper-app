/**
 * The PostHog loader when `posthog.init` throws (#918). Its own file, so the
 * loader's memoised promise starts empty: `posthog-client.test.ts` has already
 * cached a working client by the time a test there could make init throw.
 * The import and the init share one try/catch, so a throw settles as `null`;
 * a rejection would stay cached for the rest of the page.
 */
import assert from "node:assert/strict";
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

moduleMock.module("../src/env/client.mjs", {
  namedExports: { env: { NEXT_PUBLIC_POSTHOG_KEY: "phc_test" } },
});

let initCalls = 0;

moduleMock.module("posthog-js", {
  defaultExport: {
    init: () => {
      initCalls += 1;
      throw new Error("posthog init failed");
    },
  },
});

const LOADER_PATH = "../src/lib/posthog-client.ts";

test("init throws: answers null, never rejects, and stays null", async () => {
  const { loadPostHog } = (await import(LOADER_PATH)) as {
    loadPostHog: () => Promise<unknown>;
  };

  assert.equal(await loadPostHog(), null);
  assert.equal(await loadPostHog(), null);
  // Memoised: the failed init is not retried on every call.
  assert.equal(initCalls, 1);
});
