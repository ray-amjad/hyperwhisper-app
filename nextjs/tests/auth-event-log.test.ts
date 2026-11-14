/**
 * The shared auth log helpers (#2955): the outcome → console level mapping and
 * the hashed client IP that `logSignIn`, `logMagicLinkVerify` and `logRotate`
 * all write through.
 */
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { describe, test } from "node:test";

import { authLogIpHash, emitAuthEvent } from "../src/lib/auth-event-log";
import { captureConsole } from "./module-mock";

function hash12(value: string): string {
  return createHash("sha256").update(value, "utf8").digest("hex").slice(0, 12);
}

describe("emitAuthEvent", () => {
  for (const [outcome, level] of [
    ["ok", "log"],
    ["rejected", "warn"],
    ["failed", "error"],
    ["error", "error"],
    ["something_new", "error"],
  ] as const) {
    test(`outcome "${outcome}" writes one line at console.${level}`, () => {
      const capture = captureConsole(["log", "warn", "error"]);

      try {
        emitAuthEvent({ event: "test.event", outcome, ipHash: null }, outcome);
      } finally {
        capture.restore();
      }

      assert.deepEqual(capture.lines, [
        `${level} {"event":"test.event","outcome":"${outcome}","ipHash":null}`,
      ]);
    });
  }

  test("keeps the caller's key order", () => {
    const capture = captureConsole(["log", "warn", "error"]);

    try {
      emitAuthEvent(
        { event: "e", outcome: "ok", z: 1, a: 2, ipHash: "x" },
        "ok",
      );
    } finally {
      capture.restore();
    }

    assert.deepEqual(capture.lines, [
      'log {"event":"e","outcome":"ok","z":1,"a":2,"ipHash":"x"}',
    ]);
  });
});

describe("authLogIpHash", () => {
  test("hashes cf-connecting-ip over x-forwarded-for when both are sent", () => {
    const headers = new Headers({
      "cf-connecting-ip": "203.0.113.7",
      "x-forwarded-for": "198.51.100.9, 10.0.0.1",
    });

    assert.equal(authLogIpHash(headers), hash12("203.0.113.7"));
  });

  test("trims cf-connecting-ip", () => {
    // A real `Headers` already strips outer whitespace from a value, so a
    // stand-in that does not is the only way to reach the helper's own trim.
    const headers = {
      get: (name: string) =>
        name === "cf-connecting-ip" ? "  203.0.113.7\t " : null,
    } as unknown as Headers;

    assert.equal(authLogIpHash(headers), hash12("203.0.113.7"));
  });

  test("falls back to the first x-forwarded-for entry, trimmed", () => {
    const headers = new Headers({ "x-forwarded-for": "  a  , b" });

    assert.equal(authLogIpHash(headers), hash12("a"));
  });

  test("falls back to x-forwarded-for when cf-connecting-ip is blank", () => {
    const headers = new Headers({
      "cf-connecting-ip": "   ",
      "x-forwarded-for": "203.0.113.7",
    });

    assert.equal(authLogIpHash(headers), hash12("203.0.113.7"));
  });

  test("is a 12-character lowercase hex prefix", () => {
    const value = authLogIpHash(new Headers({ "cf-connecting-ip": "::1" }));

    assert.match(value ?? "", /^[0-9a-f]{12}$/);
  });

  test("is null with no headers, no IP header, or an empty first entry", () => {
    assert.equal(authLogIpHash(undefined), null);
    assert.equal(authLogIpHash(new Headers()), null);
    assert.equal(
      authLogIpHash(new Headers({ "x-forwarded-for": " , 10.0.0.1" })),
      null,
    );
  });
});
