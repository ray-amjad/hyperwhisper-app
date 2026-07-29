#!/usr/bin/env node

/**
 * Verifies that a released version actually reached the LIVE appcast.
 *
 * Publishing a release is not the same as delivering it. The release workflows
 * commit the appcast to this repo, but the file only reaches users once the
 * site is deployed — a separate system the release neither triggers nor owns.
 * If that deploy never happens, the release goes green while users see nothing.
 *
 * This script closes the loop: it polls the live appcast until the version
 * appears, then confirms the installers it points at are actually downloadable.
 *
 * Usage:
 *   node scripts/check-appcast-live.js --platform windows --version 1.8.2
 *   node scripts/check-appcast-live.js --platform macos --version 2.41.0 --timeout 300
 */

const APPCASTS = {
  windows: "https://www.hyperwhisper.com/appcast-windows.xml",
  macos: "https://www.hyperwhisper.com/appcast.xml",
};

const DEFAULT_TIMEOUT_SECONDS = 600;
const POLL_INTERVAL_SECONDS = 15;

function fail(message, hint) {
  console.error(`ERROR: ${message}`);
  if (hint) console.error(hint);
  process.exit(1);
}

function parseArgs(argv) {
  const args = {};
  for (let i = 0; i < argv.length; i += 1) {
    const arg = argv[i];
    if (!arg.startsWith("--")) continue;
    const key = arg.slice(2);
    const next = argv[i + 1];
    if (next === undefined || next.startsWith("--")) {
      fail(`Missing value for --${key}`);
    }
    args[key] = next;
    i += 1;
  }
  return args;
}

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

/**
 * Always bust the CDN cache. The appcast has been observed served from cache
 * with an age of 12 days, so an uncached read can report a stale version long
 * after a deploy — exactly the false signal this script exists to catch.
 */
async function fetchAppcast(url) {
  const bustUrl = `${url}?cb=${Date.now()}`;
  const response = await fetch(bustUrl, {
    headers: { "Cache-Control": "no-cache", Pragma: "no-cache" },
  });
  if (!response.ok) {
    throw new Error(`HTTP ${response.status} fetching ${url}`);
  }
  return response.text();
}

function versionsIn(xml) {
  const matches =
    xml.match(/<sparkle:shortVersionString>\s*([^<\s]+)\s*<\/sparkle:shortVersionString>/g) || [];
  return matches.map((m) => m.replace(/<[^>]+>/g, "").trim());
}

function enclosureUrlsForVersion(xml, version) {
  const urls = (xml.match(/url="([^"]+)"/g) || []).map((m) => m.slice(5, -1));
  // Both platforms embed the version in the artifact filename
  // (HyperWhisper-1.8.2-x64-Setup.exe, hyperwhisper-2.41.0.dmg).
  return [...new Set(urls.filter((u) => u.includes(version)))];
}

async function verifyDownloadable(urls) {
  const failures = [];
  for (const url of urls) {
    try {
      const response = await fetch(url, { method: "HEAD" });
      if (!response.ok) {
        failures.push(`${url} -> HTTP ${response.status}`);
      } else {
        console.log(`  reachable: ${url}`);
      }
    } catch (error) {
      failures.push(`${url} -> ${error.message}`);
    }
  }
  return failures;
}

async function main() {
  const args = parseArgs(process.argv.slice(2));
  const platform = (args.platform || "").toLowerCase();
  const version = (args.version || "").trim();
  const timeoutSeconds = Number(args.timeout || DEFAULT_TIMEOUT_SECONDS);

  if (!APPCASTS[platform]) {
    fail(`--platform must be one of: ${Object.keys(APPCASTS).join(", ")}. Got: ${platform || "(missing)"}`);
  }
  if (!/^\d+\.\d+\.\d+$/.test(version)) {
    fail(`--version must be a semver release like 1.8.2. Got: ${version || "(missing)"}`);
  }
  if (!Number.isFinite(timeoutSeconds) || timeoutSeconds <= 0) {
    fail(`--timeout must be a positive number of seconds. Got: ${args.timeout}`);
  }

  const url = APPCASTS[platform];
  const deadline = Date.now() + timeoutSeconds * 1000;

  console.log(`Waiting for ${platform} ${version} to appear at ${url}`);
  console.log(`Timeout: ${timeoutSeconds}s, polling every ${POLL_INTERVAL_SECONDS}s\n`);

  let lastSeen = "(never fetched)";
  let attempt = 0;

  while (Date.now() < deadline) {
    attempt += 1;
    try {
      const xml = await fetchAppcast(url);
      const versions = versionsIn(xml);
      lastSeen = versions.length ? versions.slice(0, 3).join(", ") : "(no versions parsed)";

      if (versions.includes(version)) {
        console.log(`Found ${version} in the live appcast after ${attempt} attempt(s).\n`);

        const enclosures = enclosureUrlsForVersion(xml, version);
        if (enclosures.length === 0) {
          fail(
            `${version} is listed in the live appcast but no enclosure URL references it.`,
            "The appcast entry is malformed — users would see the update and fail to download it."
          );
        }

        console.log("Verifying enclosures are downloadable:");
        const failures = await verifyDownloadable(enclosures);
        if (failures.length > 0) {
          fail(
            `${version} is live in the appcast but ${failures.length} enclosure(s) are not downloadable:\n  ${failures.join("\n  ")}`,
            "Users would be offered an update that fails to download. Check the R2 upload."
          );
        }

        console.log(`\nOK: ${platform} ${version} is live and downloadable.`);
        return;
      }

      console.log(`  attempt ${attempt}: live appcast is at ${lastSeen} — waiting...`);
    } catch (error) {
      console.log(`  attempt ${attempt}: ${error.message} — retrying...`);
    }

    await sleep(POLL_INTERVAL_SECONDS * 1000);
  }

  fail(
    `${platform} ${version} never appeared in the live appcast within ${timeoutSeconds}s. Live appcast is still at: ${lastSeen}`,
    [
      "",
      "The release itself is fine — installers are signed, uploaded, and published on GitHub.",
      "What failed is DELIVERY: users cannot see this update.",
      "",
      "The appcast is a static file in nextjs/public/, so it only goes live when the site",
      "is deployed. Check the site's production deployment state: a last production deploy",
      "older than this release's commit is the smoking gun.",
    ].join("\n")
  );
}

main().catch((error) => {
  fail(`Unexpected error: ${error.message}`);
});
