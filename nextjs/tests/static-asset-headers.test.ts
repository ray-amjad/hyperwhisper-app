/**
 * public/_headers carries the security headers to static files on Cloudflare
 * Workers, where next.config.mjs headers() never runs for them. It must stay
 * in step with config/security-headers.mjs.
 */
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

import { securityHeaders } from "../config/security-headers.mjs";

const HEADERS_FILE = readFileSync(
  new URL("../public/_headers", import.meta.url),
  "utf8",
);

/** The header lines under one path rule of the _headers file. */
function headersFor(path: string): Map<string, string> {
  const lines = HEADERS_FILE.split("\n");
  const start = lines.findIndex((line) => line.trim() === path);
  assert.notEqual(start, -1, `public/_headers has no ${path} rule`);
  const found = new Map<string, string>();
  for (const line of lines.slice(start + 1)) {
    if (!/^\s+\S/.test(line)) break;
    const [name, ...rest] = line.trim().split(":");
    found.set(name.toLowerCase(), rest.join(":").trim());
  }
  return found;
}

test("every static file gets the site's security headers", () => {
  const all = headersFor("/*");
  for (const { key, value } of securityHeaders) {
    assert.equal(all.get(key.toLowerCase()), value, key);
  }
});

test("the update feeds stay readable cross-origin", () => {
  for (const feed of ["/appcast.xml", "/appcast-windows.xml", "/linux-latest.json"]) {
    assert.equal(headersFor(feed).get("access-control-allow-origin"), "*", feed);
  }
});

test("hashed build output is cached for a year, as on Vercel", () => {
  assert.equal(
    headersFor("/_next/static/*").get("cache-control"),
    "public, max-age=31536000, immutable",
  );
});
