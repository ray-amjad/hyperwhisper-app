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
import { createHash } from "node:crypto";
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

  test("the re-read is a locking read, so it waits for a rotation or revocation to commit", async () => {
    // Under READ COMMITTED a plain re-read sees the row as it was before an
    // uncommitted rotation, while that rotation's session delete cannot see
    // this new session: both would miss. FOR SHARE waits for the commit.
    grantedAccount();

    const result = await signInWithLicenseKey(GRANTED_KEY);

    assert.equal(result.status, 200);
    assert.deepEqual(calls.findAccountByKeyLocked, [false, true]);
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
  const CLIENT_IP = "203.0.113.7";
  const FORWARDED_FOR = `${CLIENT_IP}, 70.41.3.18`;
  const USER_AGENT = "HyperWhisper/2.1 (macOS)";
  /** `sha256Hex("203.0.113.7").slice(0, 12)`, as a literal known answer. */
  const CLIENT_IP_HASH = createHash("sha256")
    .update(CLIENT_IP, "utf8")
    .digest("hex")
    .slice(0, 12);

  const HEADERS = {
    "user-agent": USER_AGENT,
    "x-forwarded-for": FORWARDED_FOR,
  };

  /** Splits a captured `"<level> <json>"` line into its level and its fields. */
  function parseLine(line: string): {
    level: string;
    fields: Record<string, unknown>;
  } {
    const space = line.indexOf(" ");
    return {
      level: line.slice(0, space),
      fields: JSON.parse(line.slice(space + 1)) as Record<string, unknown>,
    };
  }

  const cases: {
    name: string;
    key: string;
    arrange: () => void;
    level: "log" | "warn" | "error";
    expected: Record<string, unknown>;
  }[] = [
    {
      name: "ok",
      key: GRANTED_KEY,
      arrange: grantedAccount,
      level: "log",
      expected: { outcome: "ok", licenseId: "key_row_1", userId: "user_1" },
    },
    {
      name: "key_not_found",
      key: "HW-ZZZZ-ZZZZ-ZZZZ-ZZZZ",
      arrange: () => {},
      level: "warn",
      expected: { outcome: "rejected", reason: "key_not_found" },
    },
    {
      name: "key_inactive",
      key: GRANTED_KEY,
      arrange: () => {
        storeRow(accountKeyRow({ key: GRANTED_KEY, status: "revoked" }));
      },
      level: "warn",
      expected: {
        outcome: "rejected",
        reason: "key_inactive",
        licenseId: "key_row_1",
      },
    },
    {
      name: "key_no_user",
      key: GRANTED_KEY,
      arrange: () => {
        storeRow(accountKeyRow({ key: GRANTED_KEY, userId: "" }));
      },
      level: "warn",
      expected: {
        outcome: "rejected",
        reason: "key_no_user",
        licenseId: "key_row_1",
      },
    },
    {
      name: "user_missing",
      key: GRANTED_KEY,
      arrange: () => {
        storeRow(accountKeyRow({ key: GRANTED_KEY, userId: "user_gone" }));
      },
      level: "warn",
      expected: {
        outcome: "rejected",
        reason: "user_missing",
        licenseId: "key_row_1",
        userId: "user_gone",
      },
    },
    {
      name: "session_create_failed",
      key: GRANTED_KEY,
      arrange: () => {
        grantedAccount();
        behaviour.createSessionResult = "null";
      },
      level: "error",
      expected: {
        outcome: "failed",
        reason: "session_create_failed",
        licenseId: "key_row_1",
        userId: "user_1",
      },
    },
    {
      name: "revoked_mid_sign_in",
      key: GRANTED_KEY,
      arrange: () => {
        grantedAccount();
        behaviour.beforeLookup = (callNumber) => {
          if (callNumber === 2) {
            storeRow(accountKeyRow({ key: GRANTED_KEY, status: "revoked" }));
          }
        };
      },
      level: "warn",
      expected: {
        outcome: "rejected",
        reason: "revoked_mid_sign_in",
        licenseId: "key_row_1",
        userId: "user_1",
      },
    },
  ];

  for (const c of cases) {
    test(`${c.name}: writes exactly one \`auth.license_key_sign_in\` line at \`${c.level}\``, async () => {
      c.arrange();

      await signInWithLicenseKey(c.key, { headers: HEADERS });

      assert.equal(logLines.length, 1, logLines.join("\n"));
      const { level, fields } = parseLine(logLines[0]!);
      assert.equal(level, c.level);
      assert.deepEqual(fields, {
        event: "auth.license_key_sign_in",
        ...c.expected,
        ipHash: CLIENT_IP_HASH,
      });
    });

    test(`${c.name}: the line carries neither the submitted key nor the raw ip or user agent`, async () => {
      c.arrange();

      await signInWithLicenseKey(c.key, { headers: HEADERS });

      const all = logLines.join("\n");
      assert.ok(!all.includes(c.key), "the submitted key reached the log");
      // Any 4-character group of the key, so a prefix or a slice is caught too.
      for (const group of c.key.split("-").filter((g) => g.length >= 4)) {
        assert.ok(!all.includes(group), `key group ${group} reached the log`);
      }
      assert.ok(!all.includes(CLIENT_IP), "the raw client ip reached the log");
      assert.ok(!all.includes(USER_AGENT), "the user agent reached the log");
    });
  }

  test("trims the client ip out of a padded forwarded-for list before hashing", async () => {
    grantedAccount();

    await signInWithLicenseKey(GRANTED_KEY, {
      headers: { "x-forwarded-for": `   ${CLIENT_IP}   ,  70.41.3.18` },
    });

    assert.equal(parseLine(logLines[0]!).fields.ipHash, CLIENT_IP_HASH);
  });

  test("logs a null ipHash when the proxy header is absent", async () => {
    grantedAccount();

    await signInWithLicenseKey(GRANTED_KEY, { headers: {} });

    assert.equal(parseLine(logLines[0]!).fields.ipHash, null);
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

describe("POST /sign-in/license-key — a key containing a NUL byte (#2079)", () => {
  test("gets the usual invalid-key refusal, not an empty 500", async () => {
    // What Postgres does with 0x00 in a text parameter (SQLSTATE 22021).
    behaviour.beforeLookup = () => {
      throw Object.assign(
        new Error('invalid byte sequence for encoding "UTF8": 0x00'),
        { code: "22021" },
      );
    };

    const result = await signInWithLicenseKey("HW-NXAU-\0");

    assert.equal(result.status, 400);
    assert.equal(result.body.error, INVALID_MESSAGE);
    assert.deepEqual(calls.findAccountByKey, []);
    assert.deepEqual(calls.createSession, []);
    assert.equal(logLines.length, 1, logLines.join("\n"));
    // "<level> <json>", one line per attempt.
    const line = logLines[0]!;
    const space = line.indexOf(" ");
    assert.equal(line.slice(0, space), "warn");
    const fields = JSON.parse(line.slice(space + 1)) as Record<string, unknown>;
    assert.equal(fields.outcome, "rejected");
    assert.equal(fields.reason, "key_malformed");
  });
});
