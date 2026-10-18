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
  hasAccountKeyParam,
  withoutAccountKeyParams,
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

// The shapes posthog-js 1.438 sent on production (#1920): `history.replaceState`
// had cleaned the address bar, but both of these read the Performance API's
// navigation entry, which keeps the URL the page was first loaded with.
const LOADED_URL = `https://www.hyperwhisper.com/en?licenseKey=${KEY}&utm_source=livecheck#faq`;

test("before_send redacts the key nested in a $web_vitals event", () => {
  const event = {
    event: "$web_vitals",
    properties: {
      $current_url: LOADED_URL,
      $web_vitals_FCP_value: 412.5,
      $web_vitals_FCP_event: {
        name: "FCP",
        value: 412.5,
        navigationType: "navigate",
        navigationURL: LOADED_URL,
        $current_url: LOADED_URL,
        attribution: { navigationEntry: { name: LOADED_URL } },
        entries: [{ name: "first-contentful-paint", startTime: 412.5 }],
      },
    },
  };

  const sent = redactAccountKeyFromEvent(event);

  assert.doesNotMatch(JSON.stringify(sent), /HW-/i);
  const fcp = sent.properties.$web_vitals_FCP_event;
  assert.equal(
    fcp.navigationURL,
    "https://www.hyperwhisper.com/en?licenseKey=[redacted]&utm_source=livecheck#faq",
  );
  assert.equal(fcp.$current_url, fcp.navigationURL);
  assert.equal(fcp.value, 412.5);
  assert.equal(fcp.entries[0].name, "first-contentful-paint");
  assert.equal(sent.properties.$web_vitals_FCP_value, 412.5);
});

test("before_send redacts the key from a session recording's network navigation entry", () => {
  const navigation = {
    name: LOADED_URL,
    entryType: "navigation",
    initiatorType: "navigation",
    responseStatus: 200,
    timestamp: 1792150000000,
  };
  const fetchUrl = "https://www.hyperwhisper.com/api/trpc/x?input=%7B%7D";
  const fetch = {
    name: fetchUrl,
    entryType: "resource",
    initiatorType: "fetch",
  };
  const meta = {
    type: 4,
    data: {
      href: "https://www.hyperwhisper.com/en?utm_source=livecheck#faq",
      width: 1280,
      height: 800,
    },
    timestamp: 1,
  };
  const event = {
    event: "$snapshot",
    properties: {
      $snapshot_bytes: 512,
      $session_id: "session-1",
      $window_id: "window-1",
      $snapshot_data: [
        meta,
        {
          type: 6,
          data: {
            plugin: "rrweb/network@1",
            payload: { requests: [navigation, fetch] },
          },
          timestamp: 2,
        },
      ],
    },
  };

  const sent = redactAccountKeyFromEvent(event);

  assert.doesNotMatch(JSON.stringify(sent), /HW-/i);
  // Redacted in place: the sent event holds these same objects.
  assert.equal(
    navigation.name,
    "https://www.hyperwhisper.com/en?licenseKey=[redacted]&utm_source=livecheck#faq",
  );
  assert.equal(navigation.responseStatus, 200);
  assert.equal(fetch.name, fetchUrl);
  assert.equal(
    meta.data.href,
    "https://www.hyperwhisper.com/en?utm_source=livecheck#faq",
  );
  assert.equal(sent.properties.$snapshot_bytes, 512);
});

test("before_send redacts strings in nested arrays, survives a cycle, and keeps other values", () => {
  const nested: Record<string, unknown> = {
    urls: [LOADED_URL, ["deeper", `id=${KEY}`]],
    count: 3,
    flag: false,
    missing: null,
    utm: "hw-launch",
  };
  nested.self = nested;
  const event = { properties: { nested }, $set: { nested } };

  const sent = redactAccountKeyFromEvent(event);

  const out = sent.properties.nested as typeof nested;
  assert.deepEqual(out.urls, [
    "https://www.hyperwhisper.com/en?licenseKey=[redacted]&utm_source=livecheck#faq",
    ["deeper", "id=[redacted]"],
  ]);
  assert.equal(out.count, 3);
  assert.equal(out.flag, false);
  assert.equal(out.missing, null);
  assert.equal(out.utm, "hw-launch");
  assert.equal(out.self, out);
});

test("a nested string loses only the key, so nested campaign values are kept (#1910)", () => {
  const campaign =
    "https://www.hyperwhisper.com/en?utm_campaign=hw-launch&ref=HW-blog#faq";
  const event = {
    event: "$web_vitals",
    properties: {
      $current_url: campaign,
      $web_vitals_LCP_event: {
        navigationURL: campaign,
        oldKey: "https://www.hyperwhisper.com/credits?license_key=HW-OLD1-X",
        keyedId: `https://www.hyperwhisper.com/credits?id=${KEY}&utm_campaign=hw-launch`,
      },
    },
  };

  const sent = redactAccountKeyFromEvent(event);

  const lcp = sent.properties.$web_vitals_LCP_event;
  assert.equal(lcp.navigationURL, campaign);
  assert.equal(
    lcp.oldKey,
    "https://www.hyperwhisper.com/credits?license_key=[redacted]",
  );
  assert.equal(
    lcp.keyedId,
    "https://www.hyperwhisper.com/credits?id=[redacted]&utm_campaign=hw-launch",
  );
});

test("the address bar URL loses only the Account Key parameters", () => {
  assert.equal(
    withoutAccountKeyParams(
      `https://hyperwhisper.com/en/user/sign-in?licenseKey=${KEY}&returnTo=%2Fen%2Fuser%2Fdashboard#top`,
    ),
    "/en/user/sign-in?returnTo=%2Fen%2Fuser%2Fdashboard#top",
  );
  assert.equal(
    withoutAccountKeyParams(
      `https://hyperwhisper.com/en/user/sign-in?licenseKey=${KEY}`,
    ),
    "/en/user/sign-in",
  );
  assert.equal(
    withoutAccountKeyParams(
      `https://hyperwhisper.com/en/credits?license_key=hw-lower&id=${KEY}&id2=device-1&utm=a#faq`,
    ),
    "/en/credits?id2=device-1&utm=a#faq",
  );
});

test("only a URL with an Account Key parameter counts as carrying the key", () => {
  assert.equal(
    hasAccountKeyParam(`https://hyperwhisper.com/en?licenseKey=${KEY}`),
    true,
  );
  assert.equal(
    hasAccountKeyParam(`https://hyperwhisper.com/en/credits?license_key=x`),
    true,
  );
  assert.equal(
    hasAccountKeyParam(`https://hyperwhisper.com/en/credits?id=${KEY}`),
    true,
  );
  assert.equal(
    hasAccountKeyParam("https://hyperwhisper.com/en/checkout?id=price_123"),
    false,
  );
  assert.equal(
    hasAccountKeyParam(
      `https://hyperwhisper.com/en/user/sign-in?returnTo=%2Fen%2Fcredits%3Flicense_key%3D${KEY}`,
    ),
    true,
  );
  assert.equal(
    hasAccountKeyParam(
      "https://hyperwhisper.com/en?utm_campaign=hw-launch&ref=HW-blog",
    ),
    false,
  );
  assert.equal(
    hasAccountKeyParam("https://hyperwhisper.com/en/blog?q=a%20b&flag#top"),
    false,
  );
  assert.equal(hasAccountKeyParam("https://hyperwhisper.com/en"), false);
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
  assert.match(effect, /withoutAccountKeyParams\(window\.location\.href\)/);

  const client = readFileSync(
    new URL("../src/lib/posthog-client.ts", import.meta.url),
    "utf8",
  );
  assert.match(client, /before_send: redactAccountKeyFromEvent/);
});
