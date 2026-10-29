/**
 * The cron triggers (wrangler.jsonc) and the routes worker.ts calls for them
 * (config/cron-jobs.mjs) must agree, or a trigger fires with no job (worker.ts
 * throws) or a job never runs. Replaces the vercel.json `crons` check.
 */
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { describe, test } from "node:test";

import { CRON_JOBS } from "../config/cron-jobs.mjs";

/** wrangler.jsonc, with its full-line `//` comments removed. */
function readWranglerConfig(): {
  name: string;
  triggers?: { crons?: string[] };
  hyperdrive?: Array<{ binding: string; id: string }>;
  routes?: unknown;
} {
  const raw = readFileSync(new URL("../wrangler.jsonc", import.meta.url), "utf8");
  const json = raw
    .split("\n")
    .filter((line) => !/^\s*\/\//.test(line))
    .join("\n");
  return JSON.parse(json);
}

describe("cron triggers", () => {
  const config = readWranglerConfig();
  const crons = config.triggers?.crons ?? [];

  test("every wrangler.jsonc cron has a job, and every job a cron", () => {
    assert.deepEqual([...crons].sort(), Object.keys(CRON_JOBS).sort());
  });

  test("both retention prunes run daily", () => {
    for (const path of [
      "/api/internal/latency/prune",
      "/api/internal/sessions/prune",
    ]) {
      const entry = Object.entries(CRON_JOBS).find(([, p]) => p === path);
      assert.ok(entry, `${path} must be scheduled`);
      assert.match(entry[0], /^\d+ \d+ \* \* \*$/);
    }
  });
});

describe("wrangler.jsonc", () => {
  const config = readWranglerConfig();

  test("is the hyperwhisper-web Worker with a HYPERDRIVE binding", () => {
    assert.equal(config.name, "hyperwhisper-web");
    assert.equal(config.hyperdrive?.[0]?.binding, "HYPERDRIVE");
  });

  test("claims no hostname: the custom domains are attached at cutover", () => {
    assert.equal(config.routes, undefined);
  });
});
