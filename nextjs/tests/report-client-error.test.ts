/**
 * `reportClientError` (#739): a caught client failure reaches PostHog error
 * tracking with its operation name, and the report itself can never throw or
 * reject, whether PostHog is loaded, absent or stubbed by an ad-blocker.
 * The wiring half checks the six catches that used to report nowhere.
 */
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test, { mock } from "node:test";

interface ModuleMocker {
  module(
    specifier: string,
    options: { namedExports?: Record<string, unknown> },
  ): void;
}

const moduleMock = mock as unknown as ModuleMocker;

const captured: Array<{ error: unknown; properties: unknown }> = [];
let client: unknown = null;

moduleMock.module("../src/lib/posthog-client.ts", {
  namedExports: { loadPostHog: async () => client },
});

const errors: unknown[][] = [];
console.error = (...args: unknown[]) => {
  errors.push(args);
};

type Report = typeof import("../lib/shared/report-client-error");

async function reportClientError(
  ...args: Parameters<Report["reportClientError"]>
): Promise<void> {
  const helper = (await import("../lib/shared/report-client-error")) as Report;

  return helper.reportClientError(...args);
}

test("sends the error to PostHog with the operation and the context", async () => {
  captured.length = 0;
  client = {
    captureException: (error: unknown, properties: unknown) => {
      captured.push({ error, properties });
    },
  };
  const failure = new TypeError("Failed to fetch");

  await reportClientError("older_versions.fetch", failure, { platform: "mac" });

  assert.deepEqual(captured, [
    {
      error: failure,
      properties: { platform: "mac", operation: "older_versions.fetch" },
    },
  ]);
  assert.deepEqual(errors.at(-1), ["older_versions.fetch", failure]);
});

test("a context cannot overwrite the operation name", async () => {
  captured.length = 0;
  await reportClientError("sign_in.magic_link", new Error("x"), {
    operation: "spoofed",
  });

  assert.equal(
    (captured[0]?.properties as { operation: string }).operation,
    "sign_in.magic_link",
  );
});

test("no PostHog (no key, blocked chunk): resolves, console only", async () => {
  client = null;
  errors.length = 0;

  await assert.doesNotReject(reportClientError("credits.checkout", new Error("x")));
  assert.equal(errors.length, 1);
});

test("a stubbed captureException that throws is swallowed", async () => {
  client = {
    captureException: () => {
      throw new Error("blocked");
    },
  };

  await assert.doesNotReject(reportClientError("credits.checkout", new Error("x")));
});

// The six catches #739 names on main. Each must report, under its own stable
// operation name, before it shows its message.
const SITES: Array<[string, string]> = [
  ["app/[locale]/user/sign-in/SignInClient.tsx", "sign_in.license_key"],
  ["app/[locale]/user/sign-in/SignInClient.tsx", "sign_in.magic_link"],
  ["components/credits/CreditsPurchase.tsx", "credits.checkout"],
  ["app/[locale]/download/page.tsx", "download.linux_latest"],
  ["app/[locale]/older-versions/page.tsx", "older_versions.fetch"],
  ["app/[locale]/older-versions/page.tsx", "older_versions.format_date"],
];

for (const [file, operation] of SITES) {
  test(`${file} reports ${operation}`, () => {
    const source = readFileSync(new URL(`../${file}`, import.meta.url), "utf8");

    assert.match(
      source,
      new RegExp(`reportClientError\\("${operation.replace(".", "\\.")}"`),
    );
  });
}

test("the sign-in and checkout reports carry no email or Account Key", () => {
  for (const file of [
    "app/[locale]/user/sign-in/SignInClient.tsx",
    "components/credits/CreditsPurchase.tsx",
  ]) {
    const source = readFileSync(new URL(`../${file}`, import.meta.url), "utf8");
    // The arguments after the operation name, e.g. `error` or `error, {…}`.
    const rest = Array.from(
      source.matchAll(/reportClientError\("[^"]+",([^)]*)\)/g),
      (match) => match[1],
    );

    assert.ok(rest.length > 0);
    for (const args of rest) {
      assert.doesNotMatch(args, /email|key|licen[cs]e|callback/i);
    }
  }
});
