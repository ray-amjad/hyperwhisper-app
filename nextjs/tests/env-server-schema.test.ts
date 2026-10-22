/**
 * The server env boundary, `src/env/schema.server.mjs` + `src/env/server.mjs`
 * (#682).
 *
 * `next.config.mjs` imports `server.mjs`, which parses the schema and throws
 * `Invalid environment variables`, so a bad deploy fails the build. That only
 * protects the variables the schema names, and only the call sites that read
 * them through `env`. So this file proves:
 *
 * - the schema's rules for the three variables #682 added (Ray's decisions:
 *   BETTER_AUTH_URL required, PLANETSCALE_DATABASE_URL optional because
 *   Preview has no database, SUPPORT_EMAIL an optional email);
 * - the real `server.mjs`, imported in a fresh process the way the build
 *   imports it, refuses a missing BETTER_AUTH_URL and accepts a missing
 *   PLANETSCALE_DATABASE_URL;
 * - the real db client, with no database URL, still imports (pages import it
 *   at build time) and fails each query with a clear message;
 * - no module outside `src/env/` reads these variables from `process.env`.
 *
 * Every value below is a made-up placeholder.
 */
import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { readdirSync, readFileSync } from "node:fs";
import { join, relative } from "node:path";
import { fileURLToPath } from "node:url";
import { promisify } from "node:util";
import test, { describe } from "node:test";

import { sql } from "drizzle-orm";

import { serverSchema } from "../src/env/schema.server.mjs";

import { moduleMock } from "./module-mock";

/** A complete, valid server env. Each case changes one key. */
const VALID: Record<string, string> = {
  NODE_ENV: "production",
  PLANETSCALE_DATABASE_URL: "postgresql://user:pass@db.example.test:5432/app",
  BETTER_AUTH_URL: "https://site.example.test",
  SUPPORT_EMAIL: "support@example.test",
  STRIPE_SECRET_KEY: "sk_test_not_a_key",
  STRIPE_WEBHOOK_SECRET: "whsec_placeholder",
  UPSTASH_REDIS_SITE_URL: "https://redis.example.test",
  UPSTASH_REDIS_SITE_TOKEN: "placeholder-token",
  RESEND_API_KEY: "re_placeholder",
  CRON_SECRET: "placeholder-cron-secret",
  OUTRANK_WEBHOOK_TOKEN: "placeholder-outrank-token",
};

function without(key: string): Record<string, string> {
  const copy = { ...VALID };
  delete copy[key];
  return copy;
}

/** The names of the keys a failed parse complains about. */
function failedKeys(input: Record<string, string>): string[] {
  const result = serverSchema.safeParse(input);
  assert.equal(result.success, false, "expected the schema to refuse this env");
  return result.error!.issues.map((issue) => String(issue.path[0]));
}

describe("serverSchema", () => {
  test("accepts the complete env (the control for every case below)", () => {
    assert.equal(serverSchema.safeParse(VALID).success, true);
  });

  test("refuses a missing BETTER_AUTH_URL", () => {
    assert.deepEqual(failedKeys(without("BETTER_AUTH_URL")), [
      "BETTER_AUTH_URL",
    ]);
  });

  test("refuses a BETTER_AUTH_URL that is not a URL", () => {
    assert.deepEqual(
      failedKeys({ ...VALID, BETTER_AUTH_URL: "site.example.test" }),
      ["BETTER_AUTH_URL"],
    );
  });

  test("accepts a missing PLANETSCALE_DATABASE_URL (Preview has none)", () => {
    assert.equal(
      serverSchema.safeParse(without("PLANETSCALE_DATABASE_URL")).success,
      true,
    );
  });

  test("refuses a PLANETSCALE_DATABASE_URL that is not a URL", () => {
    assert.deepEqual(
      failedKeys({ ...VALID, PLANETSCALE_DATABASE_URL: "not a url" }),
      ["PLANETSCALE_DATABASE_URL"],
    );
  });

  test("accepts a missing SUPPORT_EMAIL and refuses one that is not an email", () => {
    assert.equal(
      serverSchema.safeParse(without("SUPPORT_EMAIL")).success,
      true,
    );
    assert.deepEqual(failedKeys({ ...VALID, SUPPORT_EMAIL: "support" }), [
      "SUPPORT_EMAIL",
    ]);
  });
});

const run = promisify(execFile);

/**
 * Imports the REAL `src/env/server.mjs` in a fresh process with exactly
 * `vars` set (plus PATH), as `next.config.mjs` does at build time.
 */
async function importServerEnv(vars: Record<string, string>) {
  const modulePath = fileURLToPath(
    new URL("../src/env/server.mjs", import.meta.url),
  );
  const script = `import(${JSON.stringify(modulePath)}).then(
    () => { console.log("IMPORTED"); process.exit(0); },
    (e) => { console.log("REJECTED:" + e.message); process.exit(3); },
  );`;
  const env = {
    PATH: process.env.PATH ?? "",
    ...vars,
  } as unknown as NodeJS.ProcessEnv;
  return run(process.execPath, ["-e", script], { env }).then(
    (r) => ({ code: 0, stdout: r.stdout, stderr: r.stderr }),
    (e: { code: number; stdout: string; stderr: string }) => ({
      code: e.code,
      stdout: e.stdout,
      stderr: e.stderr,
    }),
  );
}

describe("src/env/server.mjs at import (what the build runs)", () => {
  test("imports with the complete env", async () => {
    const result = await importServerEnv(VALID);
    assert.equal(result.code, 0, result.stdout + result.stderr);
    assert.match(result.stdout, /IMPORTED/);
  });

  test("throws Invalid environment variables without BETTER_AUTH_URL, naming it", async () => {
    const result = await importServerEnv(without("BETTER_AUTH_URL"));
    assert.equal(result.code, 3, result.stdout);
    assert.match(result.stdout, /REJECTED:Invalid environment variables/);
    assert.match(result.stderr, /BETTER_AUTH_URL/);
  });

  test("imports without PLANETSCALE_DATABASE_URL", async () => {
    const result = await importServerEnv(without("PLANETSCALE_DATABASE_URL"));
    assert.equal(result.code, 0, result.stdout + result.stderr);
  });
});

describe("src/db without PLANETSCALE_DATABASE_URL", () => {
  test("imports, then fails every query with a clear message", async () => {
    // The validated env as Preview has it: no database URL.
    moduleMock.module(new URL("../src/env/server.mjs", import.meta.url).href, {
      namedExports: { env: {} },
    });
    // A variable specifier, so the import binds to the mock above.
    const dbPath = "../src/db/index.ts";
    const { db, DATABASE_URL_NOT_SET } = (await import(
      dbPath
    )) as typeof import("../src/db");
    assert.equal(DATABASE_URL_NOT_SET, "PLANETSCALE_DATABASE_URL is not set");

    /** Drizzle wraps a failed query; the message is the error or its cause. */
    const isNotSet = (err: unknown): boolean => {
      const e = err as { message?: string; cause?: { message?: string } };
      return (
        e.message === DATABASE_URL_NOT_SET ||
        e.cause?.message === DATABASE_URL_NOT_SET
      );
    };

    await assert.rejects(db.execute(sql`select 1`), isNotSet);
    await assert.rejects(
      db.transaction(async (tx) => {
        await tx.execute(sql`select 1`);
      }),
      isNotSet,
    );
  });
});

describe("no raw process.env read of a validated variable", () => {
  const ROOT = fileURLToPath(new URL("..", import.meta.url));
  const DIRS = ["app", "lib", "server", "src"];
  const READ =
    /process\.env\.(PLANETSCALE_DATABASE_URL|BETTER_AUTH_URL|SUPPORT_EMAIL|STRIPE_SECRET_KEY|STRIPE_WEBHOOK_SECRET|HYPERWHISPER_INTERNAL_SECRET|UPSTASH_REDIS_SITE_URL|UPSTASH_REDIS_SITE_TOKEN|NEXT_PUBLIC_SITE_URL)\b/g;

  /**
   * Known reads left in files #682 does not own. Each is `file : variable`.
   * - checkout/credits: credits code is out of #682's scope; follow-up.
   */
  const ALLOWED = new Set([
    "app/api/checkout/credits/route.ts : NEXT_PUBLIC_SITE_URL",
  ]);

  function sourceFiles(dir: string): string[] {
    return readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
      const path = join(dir, entry.name);
      if (entry.isDirectory()) return sourceFiles(path);
      return /\.(ts|tsx|mjs|js)$/.test(entry.name) ? [path] : [];
    });
  }

  test("only src/env reads them from process.env", () => {
    const files = DIRS.flatMap((dir) => sourceFiles(join(ROOT, dir)))
      .map((path) => relative(ROOT, path).split("\\").join("/"))
      .filter((path) => !path.startsWith("src/env/"));
    // Not vacuous: the walk saw the call sites this issue moved.
    for (const moved of [
      "src/db/index.ts",
      "src/lib/auth.ts",
      "lib/clients/stripe.ts",
    ]) {
      assert.ok(files.includes(moved), `the scan must cover ${moved}`);
    }

    const found = new Set<string>();
    for (const file of files) {
      for (const match of Array.from(
        readFileSync(join(ROOT, file), "utf8").matchAll(READ),
      )) {
        found.add(`${file} : ${match[1]}`);
      }
    }
    const unexpected = Array.from(found).filter((read) => !ALLOWED.has(read));
    assert.deepEqual(
      unexpected,
      [],
      'read these through `import { env } from "@/src/env/server.mjs"`',
    );
  });
});
