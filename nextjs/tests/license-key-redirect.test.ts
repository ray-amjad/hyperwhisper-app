import assert from "node:assert/strict";
import test from "node:test";

import {
  DEFAULT_LICENSE_KEY_REDIRECT,
  sanitizeLicenseKeyRedirect,
  sanitizeReturnTo,
} from "../src/lib/license-key-redirect";

test("accepts relative callback paths that start with a single slash", () => {
  assert.equal(
    sanitizeLicenseKeyRedirect("/en/user/dashboard"),
    "/en/user/dashboard",
  );
  assert.equal(
    sanitizeLicenseKeyRedirect("/en/user/dashboard?tab=credits"),
    "/en/user/dashboard?tab=credits",
  );
  assert.equal(sanitizeLicenseKeyRedirect("/"), "/");
});

test("falls back when callbackURL is missing or empty", () => {
  assert.equal(
    sanitizeLicenseKeyRedirect(undefined),
    DEFAULT_LICENSE_KEY_REDIRECT,
  );
  assert.equal(sanitizeLicenseKeyRedirect(""), DEFAULT_LICENSE_KEY_REDIRECT);
});

test("rejects off-origin redirect targets", () => {
  assert.equal(
    sanitizeLicenseKeyRedirect("https://evil.example"),
    DEFAULT_LICENSE_KEY_REDIRECT,
  );
  assert.equal(
    sanitizeLicenseKeyRedirect("http://evil.example"),
    DEFAULT_LICENSE_KEY_REDIRECT,
  );
  assert.equal(
    sanitizeLicenseKeyRedirect("//evil.example/path"),
    DEFAULT_LICENSE_KEY_REDIRECT,
  );
  assert.equal(
    sanitizeLicenseKeyRedirect("/\\evil.example/path"),
    DEFAULT_LICENSE_KEY_REDIRECT,
  );
  assert.equal(
    sanitizeLicenseKeyRedirect("dashboard"),
    DEFAULT_LICENSE_KEY_REDIRECT,
  );
});

test("sanitizeReturnTo honors a caller-supplied fallback", () => {
  const fallback = "/fr/user/dashboard";
  assert.equal(sanitizeReturnTo("/fr/user/customers", fallback), "/fr/user/customers");
  assert.equal(sanitizeReturnTo(null, fallback), fallback);
  assert.equal(sanitizeReturnTo(undefined, fallback), fallback);
  assert.equal(sanitizeReturnTo("", fallback), fallback);
});

test("sanitizeReturnTo blocks protocol-relative and backslash open redirects", () => {
  const fallback = "/en/user/dashboard";
  assert.equal(sanitizeReturnTo("//evil.com", fallback), fallback);
  assert.equal(sanitizeReturnTo("/\\evil.com", fallback), fallback);
  assert.equal(sanitizeReturnTo("https://evil.com", fallback), fallback);
});

test("sanitizeReturnTo blocks tab/CR/LF smuggled past the prefix checks", () => {
  const fallback = "/en/user/dashboard";
  assert.equal(sanitizeReturnTo("/\t/evil.com", fallback), fallback);
  assert.equal(sanitizeReturnTo("/\n/evil.com", fallback), fallback);
  assert.equal(sanitizeReturnTo("/\r/evil.com", fallback), fallback);
  assert.equal(sanitizeReturnTo("/\t\t//evil.com", fallback), fallback);
  assert.equal(sanitizeReturnTo("/\t\\evil.com", fallback), fallback);
  assert.equal(sanitizeReturnTo("/\r\n/evil.com", fallback), fallback);
  assert.equal(sanitizeLicenseKeyRedirect("/\t/evil.com"), DEFAULT_LICENSE_KEY_REDIRECT);
});

test("every sanitized target resolves to the same origin", () => {
  const origin = "https://hyperwhisper.com";
  const hostile = [
    "/\t/evil.com",
    "/\n/evil.com",
    "/\r/evil.com",
    "/\t\t//evil.com",
    "/\t\\evil.com",
    "//evil.com",
    "/\\evil.com",
    "https://evil.com",
  ];

  for (const target of hostile) {
    const resolved = new URL(sanitizeLicenseKeyRedirect(target), origin);
    assert.equal(resolved.origin, origin, `${JSON.stringify(target)} escaped the origin`);
  }
});

test("sanitizeReturnTo still accepts ordinary paths with query and hash", () => {
  const fallback = "/en/user/dashboard";
  assert.equal(
    sanitizeReturnTo("/en/user/dashboard?tab=credits#usage", fallback),
    "/en/user/dashboard?tab=credits#usage",
  );
});
