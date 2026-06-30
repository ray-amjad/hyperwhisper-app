#!/usr/bin/env node

const fs = require("fs");
const path = require("path");
const { parseStringPromise } = require("xml2js");

const appcastPath = path.join(__dirname, "../public/appcast-windows.xml");
const expectedHost = "builds.hyperwhisper.com";
const expectedArchitectures = new Set(["windows-x64", "windows-arm64"]);

function fail(message) {
  console.error(`ERROR: ${message}`);
  process.exit(1);
}

function compareSemver(a, b) {
  const aParts = a.split(".").map(Number);
  const bParts = b.split(".").map(Number);
  for (let i = 0; i < Math.max(aParts.length, bParts.length); i += 1) {
    const diff = (aParts[i] || 0) - (bParts[i] || 0);
    if (diff !== 0) return diff;
  }
  return 0;
}

function requireSemver(value, fieldName) {
  if (!/^\d+\.\d+\.\d+$/.test(value || "")) {
    fail(`${fieldName} must be a semver release like 1.6.0. Found: ${value || "(missing)"}`);
  }
}

function readText(item, tagName) {
  const value = item[tagName];
  if (!Array.isArray(value) || value.length === 0) return null;
  if (typeof value[0] === "string") return value[0].trim();
  return value[0] == null ? null : String(value[0]).trim();
}

function readParsedEnclosure(item) {
  const enclosure = item.enclosure && item.enclosure[0];
  return enclosure && enclosure.$ ? enclosure.$ : null;
}

function validateDescription(value, version, os) {
  if (!value || typeof value !== "string") {
    fail(`Missing description for ${version} ${os}`);
  }

  if (!/<ul[\s>][\s\S]*<\/ul>/i.test(value)) {
    fail(`Description for ${version} ${os} must contain a <ul> release-note list`);
  }

  const liMatches = value.match(/<li[\s>][\s\S]*?<\/li>/gi) || [];
  if (liMatches.length === 0) {
    fail(`Description for ${version} ${os} must contain at least one complete <li>...</li> note`);
  }

  for (const note of liMatches) {
    const text = note.replace(/<[^>]+>/g, "").trim();
    if (!text) {
      fail(`Description for ${version} ${os} contains an empty release-note <li>`);
    }
  }
}

function validatePubDate(value, version, os) {
  if (!/^[A-Z][a-z]{2}, \d{2} [A-Z][a-z]{2} \d{4} \d{2}:\d{2}:\d{2} \+0000$/.test(value || "")) {
    fail(`Invalid pubDate format for ${version} ${os}: ${value || "(missing)"}`);
  }

  const parsed = new Date(value);
  if (Number.isNaN(parsed.getTime())) {
    fail(`Unparseable pubDate for ${version} ${os}: ${value}`);
  }

  const actualDay = parsed.toUTCString().slice(0, 3);
  const statedDay = value.slice(0, 3);
  if (actualDay !== statedDay) {
    fail(`pubDate day-of-week mismatch for ${version} ${os}: stated ${statedDay}, actual ${actualDay}`);
  }
}

async function validateWindowsAppcast() {
  const xmlContent = fs.readFileSync(appcastPath, "utf8");
  let parsed;

  try {
    parsed = await parseStringPromise(xmlContent);
  } catch (error) {
    fail(`Invalid appcast-windows.xml XML: ${error.message}`);
  }

  const items =
    parsed &&
    parsed.rss &&
    parsed.rss.channel &&
    parsed.rss.channel[0] &&
    parsed.rss.channel[0].item;

  if (!Array.isArray(items) || items.length === 0) {
    fail("Invalid appcast-windows.xml structure: missing channel items");
  }

  const itemsByVersion = new Map();
  let previousVersion = null;

  for (const item of items) {
    const title = readText(item, "title") || "unknown";
    const sparkleVersion = readText(item, "sparkle:version");
    const shortVersion = readText(item, "sparkle:shortVersionString");
    const os = readText(item, "sparkle:os");
    const pubDate = readText(item, "pubDate");
    const description = readText(item, "description");
    const enclosure = readParsedEnclosure(item);

    requireSemver(sparkleVersion, `sparkle:version for ${title}`);
    requireSemver(shortVersion, `sparkle:shortVersionString for ${title}`);

    if (sparkleVersion !== shortVersion) {
      fail(`sparkle:version and sparkle:shortVersionString differ for ${title}: ${sparkleVersion} vs ${shortVersion}`);
    }

    if (!expectedArchitectures.has(os)) {
      fail(`Unexpected sparkle:os for ${title}: ${os || "(missing)"}`);
    }

    validatePubDate(pubDate, shortVersion, os);
    validateDescription(description, shortVersion, os);

    if (!enclosure || !enclosure.url) {
      fail(`Missing enclosure URL for ${title}`);
    }

    let url;
    try {
      url = new URL(enclosure.url);
    } catch {
      fail(`Invalid enclosure URL for ${title}: ${enclosure.url}`);
    }

    if (url.hostname !== expectedHost) {
      fail(`Enclosure URL must use ${expectedHost}. Found: ${enclosure.url}`);
    }

    const arch = os.replace("windows-", "");
    const expectedPath = `/HyperWhisper-${shortVersion}-${arch}-Setup.exe`;
    if (url.pathname !== expectedPath) {
      fail(`Unexpected enclosure path for ${title}. Expected ${expectedPath}, found ${url.pathname}`);
    }

    const size = Number(enclosure.length);
    if (!Number.isInteger(size) || size <= 0) {
      fail(`Invalid enclosure length for ${title}: ${enclosure.length || "(missing)"}`);
    }

    if (!enclosure["sparkle:edSignature"] || enclosure["sparkle:edSignature"].length < 10) {
      fail(`Missing or invalid Ed25519 signature for ${title}`);
    }

    if (!itemsByVersion.has(shortVersion)) {
      itemsByVersion.set(shortVersion, new Set());
      if (previousVersion !== null && compareSemver(shortVersion, previousVersion) >= 0) {
        fail(`Versions must be newest first. ${shortVersion} appears after ${previousVersion}`);
      }
      previousVersion = shortVersion;
    }

    const architectures = itemsByVersion.get(shortVersion);
    if (architectures.has(os)) {
      fail(`Duplicate ${os} item for version ${shortVersion}`);
    }
    architectures.add(os);
  }

  for (const [version, architectures] of itemsByVersion.entries()) {
    for (const expected of expectedArchitectures) {
      if (!architectures.has(expected)) {
        fail(`Version ${version} is missing ${expected}`);
      }
    }
  }

  console.log("Windows appcast validation passed.");
  console.log(`Validated ${items.length} items across ${itemsByVersion.size} versions.`);
}

validateWindowsAppcast().catch((error) => {
  fail(`Unexpected validation error: ${error.message}`);
});
