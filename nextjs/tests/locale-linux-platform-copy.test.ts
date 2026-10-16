/**
 * #1076: commit 9c19dc04 changed the en.json copy to name Linux, but the other
 * 39 catalogues kept saying "macOS and Windows" in the page title, the meta
 * description, the footer tagline and the FAQ. This test reads every shipped
 * catalogue and fails when one of those platform strings omits `Linux`, or when
 * the system-requirements answer omits the Linux distros, so the next platform
 * change cannot drift the same way.
 */
import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

import { locales } from "../src/i18n/locales";

const MESSAGES_DIR = fileURLToPath(new URL("../messages", import.meta.url));

// Every string that lists the supported platforms. Brand and OS names are never
// translated, so `Linux` must appear verbatim in each locale.
const PLATFORM_KEYS = [
  "metadata.title",
  "metadata.description",
  "faq.questions.offline.answer",
  "faq.questions.privacy.answer",
  "faq.questions.requirements.answer",
  "footer.tagline",
] as const;

const REQUIREMENTS_KEY = "faq.questions.requirements.answer";
const REQUIREMENTS_TERMS = ["Linux", "Ubuntu", "Debian"] as const;

function lookup(messages: unknown, key: string): unknown {
  return key
    .split(".")
    .reduce<unknown>(
      (node, part) =>
        node !== null && typeof node === "object"
          ? (node as Record<string, unknown>)[part]
          : undefined,
      messages,
    );
}

function readCatalogue(locale: string): unknown {
  return JSON.parse(readFileSync(join(MESSAGES_DIR, `${locale}.json`), "utf8"));
}

/** Returns one line per key whose string is missing or omits a required term. */
function missingPlatformTerms(messages: unknown): string[] {
  const problems: string[] = [];
  for (const key of PLATFORM_KEYS) {
    const value = lookup(messages, key);
    if (typeof value !== "string") {
      problems.push(`${key} is not a string`);
      continue;
    }
    const terms = key === REQUIREMENTS_KEY ? REQUIREMENTS_TERMS : (["Linux"] as const);
    for (const term of terms) {
      if (!value.includes(term)) problems.push(`${key} omits ${term}`);
    }
  }
  return problems;
}

test("every catalogue file on disk is a shipped locale, and every locale has one", () => {
  const files = readdirSync(MESSAGES_DIR)
    .filter((name) => name.endsWith(".json"))
    .map((name) => name.slice(0, -".json".length))
    .sort();
  assert.deepEqual(files, [...locales].sort());
});

test("the check flags a catalogue that still says macOS and Windows only", () => {
  // Positive control: a stale catalogue shaped like the pre-fix de.json must be
  // caught, so the per-locale test below cannot pass vacuously.
  const stale = {
    metadata: {
      title: "HyperWhisper - KI-Sprachtranskription für macOS und Windows",
      description: "... auf macOS und Windows.",
    },
    faq: {
      questions: {
        offline: { answer: "... auf Ihrem Mac oder Windows-PC ..." },
        privacy: { answer: "... Ihren Mac oder Windows-PC." },
        requirements: { answer: "Für Mac: macOS 14. Für Windows: Windows 10/11." },
      },
    },
    footer: { tagline: "... für macOS und Windows." },
  };
  const problems = missingPlatformTerms(stale);
  assert.equal(problems.length, PLATFORM_KEYS.length + 2);
  assert.ok(problems.includes(`${REQUIREMENTS_KEY} omits Ubuntu`));
  assert.ok(problems.includes(`${REQUIREMENTS_KEY} omits Debian`));
});

for (const locale of locales) {
  test(`${locale}: every platform string names Linux`, () => {
    assert.deepEqual(missingPlatformTerms(readCatalogue(locale)), []);
  });
}
