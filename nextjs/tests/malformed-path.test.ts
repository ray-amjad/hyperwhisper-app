/**
 * worker.ts answers 400 for a path whose percent-escapes are not UTF-8,
 * instead of the 500 Next throws on it (`failed to decode param`).
 */
import assert from "node:assert/strict";
import test from "node:test";

import {
  isMalformedPath,
  malformedPathResponse,
} from "../lib/cloudflare/malformed-path";

test("a percent-escape of invalid UTF-8 is malformed", () => {
  assert.equal(isMalformedPath("/en/blog/%E0%A4%A"), true);
  assert.equal(isMalformedPath("/api/trpc/%E0"), true);
  assert.equal(isMalformedPath("/api/auth/%C0%AF"), true);
});

test("plain, encoded and non-ASCII paths are not malformed", () => {
  assert.equal(isMalformedPath("/"), false);
  assert.equal(isMalformedPath("/en/blog/some-post"), false);
  assert.equal(isMalformedPath("/en/blog/ok%20post"), false);
  assert.equal(isMalformedPath("/en/blog/%E0%A4%A4"), false);
  assert.equal(isMalformedPath("/api/trpc/customer.profile,customer.keys"), false);
});

test("the response is a plain 400", async () => {
  const response = malformedPathResponse();

  assert.equal(response.status, 400);
  assert.equal(await response.text(), "Bad Request");
});
