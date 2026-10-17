/**
 * The Account Key must never reach PostHog or stay in the address bar (#1897).
 * The macOS app opens `/credits?license_key=HW-…`, which redirects to
 * `/user/sign-in?licenseKey=HW-…`, and PostHog records the page URL on every
 * event.
 */
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

import {
  redactAccountKeyFromEvent,
  redactAccountKeyFromUrl,
  withoutLicenseKeyParam,
} from "../src/lib/account-key-redaction";

const KEY = "HW-AAAA-BBBB-CCCC-DDDD";

test("the sign-in URL loses the key and keeps returnTo", () => {
  const out = redactAccountKeyFromUrl(
    `https://hyperwhisper.com/en/user/sign-in?licenseKey=${KEY}&returnTo=/en/user/dashboard`,
  );

  assert.doesNotMatch(out, /HW-/);
  assert.equal(
    out,
    "https://hyperwhisper.com/en/user/sign-in?licenseKey=[redacted]&returnTo=/en/user/dashboard",
  );
  assert.equal(new URL(out).searchParams.get("returnTo"), "/en/user/dashboard");
});

test("every parameter name the apps use is redacted", () => {
  for (const name of ["licenseKey", "license_key", "id"]) {
    const out = redactAccountKeyFromUrl(
      `https://hyperwhisper.com/credits?${name}=${KEY}`,
    );
    assert.equal(out, `https://hyperwhisper.com/credits?${name}=[redacted]`);
  }
});

test("a key that is not the usual shape is still redacted as a parameter", () => {
  assert.equal(
    redactAccountKeyFromUrl(
      "https://hyperwhisper.com/credits?license_key=HW-OLD1-X",
    ),
    "https://hyperwhisper.com/credits?license_key=[redacted]",
  );
});

test("a key in a fragment, a path, or nested and encoded in returnTo is redacted", () => {
  for (const url of [
    `https://hyperwhisper.com/en/user/sign-in#licenseKey=${KEY}`,
    `https://hyperwhisper.com/en/keys/${KEY}/devices`,
    `https://hyperwhisper.com/en/x?returnTo=${encodeURIComponent(`/en/user/sign-in?licenseKey=${KEY}`)}`,
  ]) {
    assert.doesNotMatch(redactAccountKeyFromUrl(url), /HW-/, url);
  }
});

test("a URL with no key, and a device id, are left as they were", () => {
  for (const url of [
    "https://hyperwhisper.com/en/user/sign-in?returnTo=%2Fen%2Fuser%2Fdashboard",
    "https://hyperwhisper.com/credits?device_id=5D6E7F80-1A2B-4C3D-9E8F-0A1B2C3D4E5F",
    "https://hyperwhisper.com/en/blog/how-hw-works",
  ]) {
    assert.equal(redactAccountKeyFromUrl(url), url);
  }
});

test("before_send redacts every string property, $set and $set_once, and drops nothing", () => {
  const url = `https://hyperwhisper.com/en/user/sign-in?licenseKey=${KEY}`;
  const event = {
    event: "$pageview",
    properties: {
      $current_url: url,
      $referrer: `https://hyperwhisper.com/credits?license_key=${KEY}`,
      $screen_height: 800,
      title: "Sign in",
    },
    $set: { $current_url: url },
    $set_once: { $initial_current_url: url, $initial_referrer: "$direct" },
  };

  const sent = redactAccountKeyFromEvent(event);

  assert.ok(sent);
  assert.doesNotMatch(JSON.stringify(sent), /HW-/);
  assert.equal(sent.properties.$screen_height, 800);
  assert.equal(sent.properties.title, "Sign in");
  assert.equal(sent.$set_once.$initial_referrer, "$direct");
  assert.equal(redactAccountKeyFromEvent(null), null);
});

test("the address bar URL loses only licenseKey", () => {
  assert.equal(
    withoutLicenseKeyParam(
      `https://hyperwhisper.com/en/user/sign-in?licenseKey=${KEY}&returnTo=%2Fen%2Fuser%2Fdashboard#top`,
    ),
    "/en/user/sign-in?returnTo=%2Fen%2Fuser%2Fdashboard#top",
  );
  assert.equal(
    withoutLicenseKeyParam(
      `https://hyperwhisper.com/en/user/sign-in?licenseKey=${KEY}`,
    ),
    "/en/user/sign-in",
  );
});

test("the sign-in page strips the key from history before it signs in, and PostHog inits with the hook", () => {
  const signIn = readFileSync(
    new URL("../app/[locale]/user/sign-in/SignInClient.tsx", import.meta.url),
    "utf8",
  );
  const effect = signIn.slice(
    signIn.indexOf("if (!licenseKeyParam) return;"),
    signIn.indexOf("void handleLicenseKeySignInWithKey(licenseKeyParam);"),
  );
  assert.match(effect, /window\.history\.replaceState\(/);
  assert.match(effect, /withoutLicenseKeyParam\(window\.location\.href\)/);

  const client = readFileSync(
    new URL("../src/lib/posthog-client.ts", import.meta.url),
    "utf8",
  );
  assert.match(client, /before_send: redactAccountKeyFromEvent/);
});
