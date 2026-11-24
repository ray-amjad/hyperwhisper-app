import assert from "node:assert/strict";
import test from "node:test";

import { SITE_URL } from "../lib/site-metadata";
import {
  APEX_SITE_URL,
  isOffSiteHref,
  rewritePublicHref,
} from "../lib/public-href";

test("internal legal paths pick up the page locale", () => {
  assert.equal(
    rewritePublicHref("/legal/refund-policy", "de"),
    "/de/legal/refund-policy",
  );
  assert.equal(
    rewritePublicHref("/legal/terms-of-service", "en"),
    "/en/legal/terms-of-service",
  );
});

test("the apex host rewrites to www", () => {
  assert.equal(rewritePublicHref(APEX_SITE_URL, "en"), SITE_URL);
  assert.equal(
    rewritePublicHref(`${APEX_SITE_URL}/docs/data-privacy`, "ja"),
    `${SITE_URL}/docs/data-privacy`,
  );
  assert.equal(
    rewritePublicHref(`${APEX_SITE_URL}/docs/cloud-credits`, "en"),
    `${SITE_URL}/docs/cloud-credits`,
  );
});

test("off-site and already-www URLs are left alone", () => {
  assert.equal(
    rewritePublicHref("https://github.com/ray-amjad/hyperwhisper-app", "en"),
    "https://github.com/ray-amjad/hyperwhisper-app",
  );
  assert.equal(
    rewritePublicHref(`${SITE_URL}/docs/data-privacy`, "en"),
    `${SITE_URL}/docs/data-privacy`,
  );
  assert.equal(isOffSiteHref("https://proxyman.io"), true);
  assert.equal(isOffSiteHref(`${SITE_URL}/docs`), false);
  assert.equal(isOffSiteHref("/en/legal/refund-policy"), false);
});

test("a look-alike host that only shares a prefix with the site is off-site", () => {
  assert.equal(isOffSiteHref(`${SITE_URL}.example.com/docs`), true);
  assert.equal(isOffSiteHref(`${APEX_SITE_URL}.example.com`), true);
  assert.equal(isOffSiteHref(`${APEX_SITE_URL}/docs`), false);
  assert.equal(isOffSiteHref(SITE_URL), false);
});
