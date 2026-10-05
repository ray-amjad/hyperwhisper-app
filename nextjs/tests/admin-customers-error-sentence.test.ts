/**
 * #1155: the admin Customers page shows one sentence for a zod input failure,
 * never the serialized zod issue array.
 *
 * HOW. The real admin routers are driven over real HTTP through
 * `trpc-http-harness.ts` (route handler, superjson, adminProcedure and the zod
 * parsers all run), so the error each mutation gets is the one the page gets.
 * That error goes through `errorSentence`, the function the page renders. A
 * zod failure stops before any db-layer call, so the harness's alarms stay
 * quiet. A last check reads the page source: every mutation error line goes
 * through `errorSentence`, so no sibling site prints `error.message` raw.
 */
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { beforeEach, test } from "node:test";

import { calls, httpMutation, plainUser, resetHarness } from "./trpc-http-harness";

import { errorSentence } from "@/lib/trpc/input-error";

const ACCOUNT_ID = "33333333-3333-4333-8333-333333333333";
const SENTENCE = "Enter a valid email address.";

beforeEach(() => resetHarness());

async function sentenceFor(path: string, input: unknown): Promise<string> {
  const result = await httpMutation(path, input);

  assert.ok(result.error, `expected ${path} to fail: ${result.raw}`);
  assert.equal(result.error.data.code, "BAD_REQUEST");
  assert.deepEqual(calls.otherDb, [], "a zod failure must not reach the db");

  return errorSentence(result.error, SENTENCE);
}

function assertNoIssueArray(text: string): void {
  assert.doesNotMatch(text, /invalid_format/);
  assert.doesNotMatch(text, /pattern/);
  assert.doesNotMatch(text, /too_big/);
}

test("grant with me@gmail shows one sentence, not the zod issue array", async () => {
  const text = await sentenceFor("admin.customers.grant", { email: "me@gmail" });

  assert.equal(text, SENTENCE);
  assertNoIssueArray(text);
});

test("update email with me@gmail shows one sentence, not the zod issue array", async () => {
  const text = await sentenceFor("admin.customers.updateEmail", {
    userId: "user_2",
    newEmail: "me@gmail",
  });

  assert.equal(text, SENTENCE);
  assertNoIssueArray(text);
});

test("add credits over the server cap shows the given sentence", async () => {
  const result = await httpMutation("admin.customers.addCredits", {
    licenseKeyId: ACCOUNT_ID,
    amount: 2_000_000,
  });

  assert.ok(result.error);
  const text = errorSentence(result.error, "Enter a smaller amount.");

  assert.equal(text, "Enter a smaller amount.");
  assertNoIssueArray(text);
});

test("a sentence-shaped BAD_REQUEST from the router stays visible", () => {
  const message = "No Stripe session associated with this license";

  assert.equal(
    errorSentence({ message, data: { code: "BAD_REQUEST" } }, SENTENCE),
    message,
  );
});

test("a non-input error keeps its own message", async () => {
  const result = await httpMutation(
    "admin.customers.grant",
    { email: "me@gmail" },
    plainUser(),
  );

  assert.ok(result.error);
  assert.equal(result.error.data.code, "FORBIDDEN");
  assert.equal(errorSentence(result.error, SENTENCE), result.error.message);
});

test("the Customers page renders every mutation error through errorSentence", () => {
  const source = readFileSync(
    new URL(
      "../app/[locale]/user/(authenticated)/customers/CustomersClient.tsx",
      import.meta.url,
    ),
    "utf8",
  );

  assert.doesNotMatch(source, /Mutation\.error\.message/);
  for (const name of ["grant", "updateEmail", "refund", "addCredits"]) {
    assert.match(source, new RegExp(`errorSentence\\(${name}Mutation\\.error,`));
  }
});
