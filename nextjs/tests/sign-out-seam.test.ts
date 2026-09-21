import assert from "node:assert/strict";
import test from "node:test";

import {
  SIGN_OUT_ERROR_MESSAGE,
  signOutAndRedirect,
  type SignOutResult,
} from "../src/lib/sign-out";

/**
 * The seam `UserHeader` now calls. Records every collaborator call so a test
 * can assert on what did NOT happen — the whole of #870 is a `navigate` that
 * fired after a sign-out the server refused.
 */
function harness(signOut: () => Promise<SignOutResult>) {
  const navigated: string[] = [];
  const errors: string[] = [];
  const logged: unknown[][] = [];

  async function run(redirectTo = "/fr/user/sign-in") {
    const quiet = console.error;
    console.error = (...args: unknown[]) => {
      logged.push(args);
    };
    try {
      await signOutAndRedirect({
        signOut,
        navigate: (destination) => navigated.push(destination),
        onError: (message) => errors.push(message),
        redirectTo,
      });
    } finally {
      console.error = quiet;
    }
  }

  return { errors, logged, navigated, run };
}

test("a refused sign-out does not navigate and reports the failure", async () => {
  const { errors, logged, navigated, run } = harness(() =>
    Promise.resolve({
      data: null,
      error: {
        status: 500,
        statusText: "Internal Server Error",
        message: "Internal Server Error",
      },
    }),
  );

  await run();

  // Better Auth RESOLVES this — it does not reject. Before #870 the caller
  // discarded it and redirected, leaving a live session behind.
  assert.deepEqual(navigated, []);
  assert.deepEqual(errors, ["Internal Server Error"]);
  assert.equal(logged.length, 1);
});

test("a refused sign-out with no message falls back to the shared copy", async () => {
  const { errors, navigated, run } = harness(() =>
    Promise.resolve({ data: null, error: { status: 403 } }),
  );

  await run();

  assert.deepEqual(navigated, []);
  assert.deepEqual(errors, [SIGN_OUT_ERROR_MESSAGE]);
});

test("a successful sign-out navigates exactly once to the locale path", async () => {
  const { errors, logged, navigated, run } = harness(() =>
    Promise.resolve({ data: { success: true }, error: null }),
  );

  await run();

  assert.deepEqual(navigated, ["/fr/user/sign-in"]);
  assert.deepEqual(errors, []);
  assert.deepEqual(logged, []);
});

test("a thrown sign-out does not navigate and reports the failure", async () => {
  const { errors, logged, navigated, run } = harness(() =>
    Promise.reject(new Error("network down")),
  );

  await run();

  assert.deepEqual(navigated, []);
  assert.deepEqual(errors, [SIGN_OUT_ERROR_MESSAGE]);
  assert.equal(logged.length, 1);
});
