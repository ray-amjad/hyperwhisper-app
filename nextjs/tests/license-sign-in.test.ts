/**
 * The license-key sign-in endpoint, driven the way better-auth's router drives
 * it.
 *
 * This endpoint turns a pasted license key into a browser session. A wrong
 * answer here either signs somebody in on a key that was refunded or revoked,
 * or locks a paying customer out of their own dashboard. So the tests below
 * assert what a caller actually reads — the HTTP status, the error string, the
 * redirect target — plus the two writes that decide entitlement: whether a
 * session cookie was set, and whether a session minted during a revocation was
 * rolled back.
 *
 * Collaborators are replaced at the module boundary in
 * `license-sign-in-harness.ts`; the endpoint itself is the real one.
 */
import assert from "node:assert/strict";
import { after, before, beforeEach, describe, test } from "node:test";

import {
  accountKeyRow,
  behaviour,
  calls,
  loadRateLimitRules,
  logLines,
  resetHarness,
  restoreEndpointLogging,
  signInWithLicenseKey,
  silenceEndpointLogging,
  storeRow,
  storeUser,
  userRow,
} from "./license-sign-in-harness";

/** A made-up key string. Only the fake database in the harness knows it. */
const GRANTED_KEY = "HW-AAAA-BBBB-CCCC-DDDD";
const DEFAULT_REDIRECT = "/en/user/dashboard";
const INVALID_MESSAGE = "Invalid or inactive license key.";
const NO_ACCOUNT_MESSAGE =
  "No account found for this license key. Please contact support.";

/** Puts a granted key and the user it points at in the fake database. */
function grantedAccount(): void {
  storeRow(accountKeyRow({ key: GRANTED_KEY, userId: "user_1" }));
  storeUser(userRow({ id: "user_1" }));
}

before(() => {
  silenceEndpointLogging();
});

after(() => {
  restoreEndpointLogging();
});

beforeEach(() => {
  resetHarness();
  logLines.length = 0;
});

describe("POST /sign-in/license-key — the entitlement decision", () => {
  test("signs in a granted key and sets the session cookie for its user", async () => {
    grantedAccount();

    const result = await signInWithLicenseKey(GRANTED_KEY);

    assert.equal(result.status, 200);
    assert.deepEqual(result.body, { redirect: DEFAULT_REDIRECT });
    assert.deepEqual(calls.createSession, ["user_1"]);
    assert.deepEqual(calls.setSessionCookie, [
      { sessionToken: "session_token_for_user_1", userId: "user_1" },
    ]);
    assert.deepEqual(calls.deleteSession, []);
  });

  test("looks the user up by the id on the key row, not by the key", async () => {
    storeRow(accountKeyRow({ key: GRANTED_KEY, userId: "user_42" }));
    storeUser(userRow({ id: "user_42" }));

    const result = await signInWithLicenseKey(GRANTED_KEY);

    assert.equal(result.status, 200);
    assert.deepEqual(calls.selectUserById, ["user_42"]);
    assert.deepEqual(calls.createSession, ["user_42"]);
  });

  test("rejects a key the database does not hold, and mints no session", async () => {
    const result = await signInWithLicenseKey("HW-ZZZZ-ZZZZ-ZZZZ-ZZZZ");

    assert.equal(result.status, 400);
    assert.equal(result.body.error, INVALID_MESSAGE);
    assert.deepEqual(calls.createSession, []);
    assert.deepEqual(calls.setSessionCookie, []);
  });

  test("rejects a revoked key, and mints no session", async () => {
    storeRow(accountKeyRow({ key: GRANTED_KEY, status: "revoked" }));
    storeUser(userRow({ id: "user_1" }));

    const result = await signInWithLicenseKey(GRANTED_KEY);

    assert.equal(result.status, 400);
    assert.equal(result.body.error, INVALID_MESSAGE);
    assert.deepEqual(calls.selectUserById, []);
    assert.deepEqual(calls.createSession, []);
    assert.deepEqual(calls.setSessionCookie, []);
  });

  test("rejects a pending key, so only `granted` signs in", async () => {
    storeRow(accountKeyRow({ key: GRANTED_KEY, status: "pending" }));
    storeUser(userRow({ id: "user_1" }));

    const result = await signInWithLicenseKey(GRANTED_KEY);

    assert.equal(result.status, 400);
    assert.equal(result.body.error, INVALID_MESSAGE);
    assert.deepEqual(calls.createSession, []);
  });

  // `account_keys.user_id` is NOT NULL in the schema, so the endpoint's
  // `!license.userId` branch is defence in depth. An empty string is what a
  // bad import or a partial backfill would leave behind, and it is the only
  // falsy value the column's own type admits.
  test("rejects a granted key that is not attached to a user yet", async () => {
    storeRow(accountKeyRow({ key: GRANTED_KEY, userId: "" }));

    const result = await signInWithLicenseKey(GRANTED_KEY);

    assert.equal(result.status, 400);
    assert.equal(result.body.error, NO_ACCOUNT_MESSAGE);
    assert.deepEqual(calls.selectUserById, []);
    assert.deepEqual(calls.createSession, []);
  });

  test("rejects a key whose user row is gone", async () => {
    storeRow(accountKeyRow({ key: GRANTED_KEY, userId: "user_gone" }));

    const result = await signInWithLicenseKey(GRANTED_KEY);

    assert.equal(result.status, 400);
    assert.equal(result.body.error, NO_ACCOUNT_MESSAGE);
    assert.deepEqual(calls.selectUserById, ["user_gone"]);
    assert.deepEqual(calls.createSession, []);
    assert.deepEqual(calls.setSessionCookie, []);
  });

  test("answers 500 and sets no cookie when the session adapter fails", async () => {
    grantedAccount();
    behaviour.createSessionResult = "null";

    const result = await signInWithLicenseKey(GRANTED_KEY);

    assert.equal(result.status, 500);
    assert.equal(result.body.error, "Failed to create session.");
    assert.deepEqual(calls.setSessionCookie, []);
    assert.deepEqual(calls.deleteSession, []);
  });
});

describe("POST /sign-in/license-key — the revocation race", () => {
  test("deletes the session it just minted when the key is revoked mid-flight", async () => {
    grantedAccount();
    // Revocation lands after the first lookup and before the re-read.
    behaviour.beforeLookup = (callNumber) => {
      if (callNumber === 2) {
        storeRow(accountKeyRow({ key: GRANTED_KEY, status: "revoked" }));
      }
    };

    const result = await signInWithLicenseKey(GRANTED_KEY);

    assert.equal(result.status, 400);
    assert.equal(result.body.error, INVALID_MESSAGE);
    assert.deepEqual(calls.createSession, ["user_1"]);
    assert.deepEqual(calls.deleteSession, ["session_token_for_user_1"]);
    assert.deepEqual(
      calls.setSessionCookie,
      [],
      "a revoked key must never reach the cookie writer",
    );
  });

  test("deletes the session it just minted when the key row disappears mid-flight", async () => {
    grantedAccount();
    behaviour.beforeLookup = (callNumber) => {
      if (callNumber === 2) {
        behaviour.rows.delete(GRANTED_KEY);
      }
    };

    const result = await signInWithLicenseKey(GRANTED_KEY);

    assert.equal(result.status, 400);
    assert.equal(result.body.error, INVALID_MESSAGE);
    assert.deepEqual(calls.deleteSession, ["session_token_for_user_1"]);
    assert.deepEqual(calls.setSessionCookie, []);
  });

  test("re-reads the key after the session row exists, not before", async () => {
    grantedAccount();
    const order: string[] = [];
    behaviour.beforeLookup = (callNumber) => {
      order.push(`lookup_${callNumber}`);
      if (callNumber === 2) {
        order.push(`sessions_minted_${calls.createSession.length}`);
      }
    };

    const result = await signInWithLicenseKey(GRANTED_KEY);

    assert.equal(result.status, 200);
    assert.deepEqual(order, ["lookup_1", "lookup_2", "sessions_minted_1"]);
    assert.deepEqual(calls.findAccountByKey, [GRANTED_KEY, GRANTED_KEY]);
  });
});

describe("POST /sign-in/license-key — the redirect target", () => {
  test("returns a same-origin path the caller asked for", async () => {
    grantedAccount();

    const result = await signInWithLicenseKey(GRANTED_KEY, {
      callbackURL: "/ja/user/dashboard?welcome=1",
    });

    assert.equal(result.status, 200);
    assert.equal(result.body.redirect, "/ja/user/dashboard?welcome=1");
  });

  test("refuses a protocol-relative callback and falls back", async () => {
    grantedAccount();

    const result = await signInWithLicenseKey(GRANTED_KEY, {
      callbackURL: "//evil.example.com/steal",
    });

    assert.equal(result.body.redirect, DEFAULT_REDIRECT);
  });

  test("refuses a backslash-prefixed callback and falls back", async () => {
    grantedAccount();

    const result = await signInWithLicenseKey(GRANTED_KEY, {
      callbackURL: "/\\evil.example.com",
    });

    assert.equal(result.body.redirect, DEFAULT_REDIRECT);
  });

  test("refuses an absolute URL callback and falls back", async () => {
    grantedAccount();

    const result = await signInWithLicenseKey(GRANTED_KEY, {
      callbackURL: "https://evil.example.com/steal",
    });

    assert.equal(result.body.redirect, DEFAULT_REDIRECT);
  });
});

describe("POST /sign-in/license-key — the request contract", () => {
  test("refuses a body with no license key", async () => {
    grantedAccount();

    await assert.rejects(
      () => signInWithLicenseKey(GRANTED_KEY, { rawBody: {} }),
      (error: unknown) => {
        const message = String((error as Error).message);
        assert.match(message, /licenseKey/);
        return true;
      },
    );
    assert.deepEqual(calls.findAccountByKey, []);
  });

  test("refuses a license key that is not a string", async () => {
    grantedAccount();

    await assert.rejects(() =>
      signInWithLicenseKey(GRANTED_KEY, { rawBody: { licenseKey: 12345 } }),
    );
    assert.deepEqual(calls.findAccountByKey, []);
  });

  test("refuses a request that carries no headers", async () => {
    grantedAccount();

    await assert.rejects(
      () => signInWithLicenseKey(GRANTED_KEY, { headers: null }),
      (error: unknown) => {
        assert.match(String((error as Error).message), /[Hh]eaders/);
        return true;
      },
    );
    assert.deepEqual(calls.findAccountByKey, []);
  });

  test("passes the key through to the lookup exactly as it was sent", async () => {
    await signInWithLicenseKey("  hw-aaaa-bbbb-cccc-dddd  ");

    assert.deepEqual(calls.findAccountByKey, ["  hw-aaaa-bbbb-cccc-dddd  "]);
  });
});

describe("POST /sign-in/license-key — the audit line", () => {
  test("records the user, the key row, the client ip and the user agent", async () => {
    grantedAccount();

    await signInWithLicenseKey(GRANTED_KEY, {
      headers: {
        "user-agent": "HyperWhisper/2.1 (macOS)",
        "x-forwarded-for": "203.0.113.7, 70.41.3.18",
      },
    });

    assert.equal(logLines.length, 1);
    assert.equal(
      logLines[0],
      '[license-key-sign-in] user=user_1 license=key_row_1 ip=203.0.113.7 ua="HyperWhisper/2.1 (macOS)"',
    );
  });

  test("trims the client ip out of a padded forwarded-for list", async () => {
    grantedAccount();

    await signInWithLicenseKey(GRANTED_KEY, {
      headers: {
        "user-agent": "HyperWhisper/2.1",
        "x-forwarded-for": "   198.51.100.4   ,  70.41.3.18",
      },
    });

    assert.match(logLines[0] ?? "", /ip=198\.51\.100\.4 /);
  });

  test("falls back to `unknown` when the proxy headers are absent", async () => {
    grantedAccount();

    await signInWithLicenseKey(GRANTED_KEY, { headers: {} });

    assert.equal(
      logLines[0],
      '[license-key-sign-in] user=user_1 license=key_row_1 ip=unknown ua="unknown"',
    );
  });

  test("writes no audit line for a key that was refused", async () => {
    await signInWithLicenseKey("HW-ZZZZ-ZZZZ-ZZZZ-ZZZZ");

    assert.deepEqual(logLines, []);
  });
});

describe("the license-key rate-limit rule", () => {
  test("throttles only the sign-in path, at 5 attempts a minute", async () => {
    const rules = await loadRateLimitRules();

    assert.equal(rules.length, 1);
    const rule = rules[0]!;
    assert.equal(rule.window, 60);
    assert.equal(rule.max, 5);
    assert.equal(rule.pathMatcher("/sign-in/license-key"), true);
    assert.equal(rule.pathMatcher("/sign-in/email"), false);
    assert.equal(rule.pathMatcher("/sign-in/license-key/extra"), false);
    assert.equal(rule.pathMatcher("/x/sign-in/license-key"), false);
  });
});
