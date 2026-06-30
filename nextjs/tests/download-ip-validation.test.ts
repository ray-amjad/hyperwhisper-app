import assert from "node:assert/strict";
import test from "node:test";

import { getClientIPFromHeaders } from "../server/api/routers/download-ip";

test("extracts valid IPv4 and IPv6 header candidates", () => {
  assert.equal(
    getClientIPFromHeaders(new Headers({ "x-forwarded-for": "203.0.113.10, 10.0.0.1" })),
    "203.0.113.10"
  );
  assert.equal(
    getClientIPFromHeaders(new Headers({ "x-real-ip": "2001:db8::1" })),
    "2001:db8::1"
  );
});

test("prefers trusted edge headers over raw forwarded headers", () => {
  assert.equal(
    getClientIPFromHeaders(
      new Headers({
        "x-forwarded-for": "203.0.113.10",
        "x-vercel-forwarded-for": "198.51.100.7",
      })
    ),
    "198.51.100.7"
  );
});

test("skips malformed forwarded values before considering fallback headers", () => {
  assert.equal(
    getClientIPFromHeaders(
      new Headers({
        "x-forwarded-for": "1.1.1.1/../../foo?x=",
        "x-vercel-forwarded-for": "198.51.100.7",
      })
    ),
    "198.51.100.7"
  );
});

test("returns unknown when all header candidates are malformed", () => {
  assert.equal(
    getClientIPFromHeaders(
      new Headers({
        "x-forwarded-for": "1.1.1.1/../../foo?x=",
        "x-real-ip": "not an ip",
        "cf-connecting-ip": "fe80::1%eth0",
      })
    ),
    "unknown"
  );
});
