#!/usr/bin/env node

/**
 * Checks that every build the appcasts name can still be downloaded.
 *
 * The build-time validators (validate-appcast.js, validate-windows-appcast.js)
 * stay offline on purpose: macos-release.yml runs them BEFORE it uploads the
 * new DMG, so a network check there would fail every release. That left a gap:
 * the 2.25.0 item named a DMG that was never on builds.hyperwhisper.com, and
 * /en/older-versions drew a "Download DMG" button for it that led to a 404
 * (#761). This script closes the gap from a daily scheduled job
 * (.github/workflows/appcast-enclosures.yml).
 *
 * It sends a HEAD for every enclosure URL in both feeds, retries a failed one
 * once after a short wait, and exits 1 only when the retry fails too, so a
 * transient network error does not turn the job red.
 *
 * Usage:
 *   node scripts/check-appcast-enclosures.js                  # both feeds in public/
 *   node scripts/check-appcast-enclosures.js path/to/feed.xml # any feed files
 */

const fs = require("fs");
const path = require("path");

const DEFAULT_FEEDS = [
  path.join(__dirname, "../public/appcast.xml"),
  path.join(__dirname, "../public/appcast-windows.xml"),
];

const CONCURRENCY = 8;
const RETRY_DELAY_MS = 5000;
const REQUEST_TIMEOUT_MS = 30000;

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

function enclosureUrls(xml) {
  // Windows items spread the enclosure's attributes over several lines.
  const urls = [];
  for (const match of xml.matchAll(/<enclosure\b[^>]*?\burl="([^"]+)"/g)) {
    urls.push(match[1]);
  }
  return urls;
}

async function headStatus(url) {
  try {
    const response = await fetch(url, {
      method: "HEAD",
      redirect: "follow",
      signal: AbortSignal.timeout(REQUEST_TIMEOUT_MS),
    });
    return { ok: response.status === 200, detail: `HTTP ${response.status}` };
  } catch (error) {
    return { ok: false, detail: error.message };
  }
}

async function checkUrl(url) {
  const first = await headStatus(url);
  if (first.ok) return first;
  await sleep(RETRY_DELAY_MS);
  const second = await headStatus(url);
  if (second.ok) {
    console.log(`  recovered on retry: ${url} (first try: ${first.detail})`);
  }
  return second;
}

async function checkAll(entries) {
  const failures = [];
  let next = 0;
  async function worker() {
    while (next < entries.length) {
      const entry = entries[next];
      next += 1;
      const result = await checkUrl(entry.url);
      if (!result.ok) failures.push({ ...entry, detail: result.detail });
    }
  }
  await Promise.all(Array.from({ length: Math.min(CONCURRENCY, entries.length) }, worker));
  return failures;
}

async function main() {
  const feeds = process.argv.slice(2).length > 0 ? process.argv.slice(2) : DEFAULT_FEEDS;
  const entries = [];

  for (const feed of feeds) {
    const xml = fs.readFileSync(feed, "utf-8");
    const urls = [...new Set(enclosureUrls(xml))];
    if (urls.length === 0) {
      console.error(`❌ No enclosure URLs found in ${feed}`);
      process.exit(1);
    }
    console.log(`${path.basename(feed)}: ${urls.length} enclosures`);
    for (const url of urls) entries.push({ feed: path.basename(feed), url });
  }

  const failures = await checkAll(entries);

  if (failures.length > 0) {
    console.error(`\n❌ ${failures.length} of ${entries.length} enclosures cannot be downloaded:`);
    for (const failure of failures) {
      console.error(`  ${failure.feed}: ${failure.url} -> ${failure.detail}`);
    }
    console.error(
      "\nUpload the missing build to builds.hyperwhisper.com, or delete its <item> from the feed." +
        " Do not repoint the URL at another build: its version and sparkle:edSignature would no longer match."
    );
    process.exit(1);
  }

  console.log(`\n✅ All ${entries.length} enclosures are downloadable`);
}

main().catch((error) => {
  console.error(`❌ Unexpected error: ${error.message}`);
  process.exit(1);
});
