/**
 * The client env check, `src/env/schema.client.mjs` (#917). It replaced a zod
 * schema so that zod and the server schema stay out of the browser bundle, and
 * it must reject exactly the values that zod schema rejected. This test keeps
 * the old zod schema as the oracle and compares the two, value by value.
 */
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { z } from "zod";

import { validateClientEnv } from "../src/env/schema.client.mjs";

// The schema that lived in src/env/schema.mjs before #917, verbatim.
const zodClientSchema = z.object({
  NEXT_PUBLIC_ENVIRONMENT: z.enum(["development", "test", "production"]).optional(),
  NEXT_PUBLIC_SITE_URL: z.string().url().optional(),
  NEXT_PUBLIC_POSTHOG_KEY: z.string().optional(),
  NEXT_PUBLIC_POSTHOG_HOST: z.string().url().optional(),
});

const ENVIRONMENT_VALUES: unknown[] = [
  undefined,
  "development",
  "test",
  "production",
  "",
  "Production",
  " production",
  "prod",
  "preview",
  5,
  null,
];

const URL_VALUES: unknown[] = [
  undefined,
  "https://www.hyperwhisper.com",
  "https://eu.i.posthog.com",
  "http://localhost:3000",
  "  https://example.com  ",
  "https://exa\tmple.com/a\nb",
  "mailto:a@b.c",
  "ftp://example.com",
  "http:example.com",
  "localhost:3000",
  "www.hyperwhisper.com",
  "",
  "   ",
  "not a url",
  "/relative/path",
  "https://",
  "http://[::1",
  1,
  null,
];

const KEY_VALUES: unknown[] = [undefined, "", "phc_abc123", "  spaced  ", 0, null, true];

function compare(input: Record<string, unknown>): void {
  const expected = zodClientSchema.safeParse(input);
  const actual = validateClientEnv(input);
  const label = JSON.stringify(input, (_k, v) => (v === undefined ? "<undefined>" : v));
  assert.equal(actual.success, expected.success, `success differs for ${label}`);
  if (expected.success && actual.success) {
    assert.deepEqual(actual.data, expected.data, `data differs for ${label}`);
  }
  if (!expected.success && !actual.success) {
    const zodFailed = Array.from(
      new Set(expected.error.issues.map((issue) => String(issue.path[0]))),
    ).sort();
    assert.deepEqual(Object.keys(actual.errors).sort(), zodFailed, `failing keys differ for ${label}`);
  }
}

test("NEXT_PUBLIC_ENVIRONMENT: accepts and rejects what zod did", () => {
  for (const value of ENVIRONMENT_VALUES) compare({ NEXT_PUBLIC_ENVIRONMENT: value });
});

test("both URL vars: accept and reject what zod did, with the same output", () => {
  for (const value of URL_VALUES) {
    compare({ NEXT_PUBLIC_SITE_URL: value });
    compare({ NEXT_PUBLIC_POSTHOG_HOST: value });
  }
});

test("NEXT_PUBLIC_POSTHOG_KEY: any string, nothing else", () => {
  for (const value of KEY_VALUES) compare({ NEXT_PUBLIC_POSTHOG_KEY: value });
});

test("all 4 together, unset, set and with unknown keys", () => {
  compare({});
  compare({
    NEXT_PUBLIC_ENVIRONMENT: undefined,
    NEXT_PUBLIC_SITE_URL: undefined,
    NEXT_PUBLIC_POSTHOG_KEY: undefined,
    NEXT_PUBLIC_POSTHOG_HOST: undefined,
  });
  compare({
    NEXT_PUBLIC_ENVIRONMENT: "production",
    NEXT_PUBLIC_SITE_URL: "https://www.hyperwhisper.com",
    NEXT_PUBLIC_POSTHOG_KEY: "phc_abc",
    NEXT_PUBLIC_POSTHOG_HOST: "https://eu.i.posthog.com",
    STRIPE_SECRET_KEY: "sk_test_not_a_key",
  });
  compare({
    NEXT_PUBLIC_ENVIRONMENT: "staging",
    NEXT_PUBLIC_SITE_URL: "nope",
    NEXT_PUBLIC_POSTHOG_KEY: "phc_abc",
    NEXT_PUBLIC_POSTHOG_HOST: "",
  });
});

test("a rejected value names its key in the result", () => {
  const result = validateClientEnv({ NEXT_PUBLIC_SITE_URL: "nope" });
  assert.equal(result.success, false);
  if (!result.success) assert.deepEqual(Object.keys(result.errors), ["NEXT_PUBLIC_SITE_URL"]);
});

test("the client env modules import neither zod nor the server schema", () => {
  for (const file of ["../src/env/schema.client.mjs", "../src/env/client.mjs"]) {
    const source = readFileSync(new URL(file, import.meta.url), "utf8");
    const imports = source.split("\n").filter((line) => /^\s*import\b|\bimport\(/.test(line));
    for (const line of imports) {
      assert.doesNotMatch(line, /zod|schema\.server|server\.mjs/, `${file}: ${line}`);
    }
  }
});
