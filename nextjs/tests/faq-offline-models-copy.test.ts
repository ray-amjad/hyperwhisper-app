/**
 * The home FAQ "What offline models are available?" told visitors in every
 * locale that offline post-processing uses Gemma 3, long after the apps moved
 * their local models to Gemma 4 (#2758). Reads the generation the macOS and
 * Windows local model lists ship and fails on any locale whose answer names
 * another one.
 */
import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

const root = fileURLToPath(new URL("..", import.meta.url));
const messagesDir = join(root, "messages");
const macModels = join(
  root,
  "../app/macos/hyperwhisper/Models/PostProcessingModels.swift",
);
const winModels = join(
  root,
  "../app/windows/HyperWhisper/Models/LanguageModelInfo.cs",
);

// Gemma generations among the shipped local models' display names, e.g. "Gemma 4 E2B (Q4)".
function shippedGenerations(source: string): string[] {
  return Array.from(
    new Set(
      Array.from(source.matchAll(/"Gemma (\d+) [^"]*\(Q\d\)"/g), (m) => m[1]),
    ),
  );
}

// Every "Gemma <n>" the copy states, e.g. "Gemma 4", "Gemma-4-Modells".
function statedGenerations(text: string): string[] {
  return Array.from(text.matchAll(/Gemma[\s-]?(\d+)/gi), (m) => m[1]);
}

test("the check flags the old 'Gemma 3' and 'Gemma-3' copy", () => {
  assert.deepEqual(
    statedGenerations("sizes of the Gemma 3 model; des Gemma-3-Modells"),
    ["3", "3"],
  );
  assert.deepEqual(statedGenerations("Gemma 4 (made by Google)"), ["4"]);
});

test("both desktop heads ship the same Gemma generation locally", () => {
  const mac = shippedGenerations(readFileSync(macModels, "utf8"));
  const win = shippedGenerations(readFileSync(winModels, "utf8"));
  assert.equal(
    mac.length,
    1,
    `macOS local models span Gemma ${mac.join(", ")}`,
  );
  assert.deepEqual(win, mac);
});

test("every locale's offline models answer names the Gemma generation the apps ship", () => {
  const [shipped] = shippedGenerations(readFileSync(macModels, "utf8"));
  const files = readdirSync(messagesDir).filter((f) => f.endsWith(".json"));
  assert.ok(files.length >= 40, `expected every locale, found ${files.length}`);
  for (const file of files) {
    const text: string = JSON.parse(
      readFileSync(join(messagesDir, file), "utf8"),
    ).faq.questions.offlineModels.answer;
    const stated = statedGenerations(text);
    assert.ok(
      stated.length > 0,
      `${file} offlineModels answer names no Gemma model: ${text}`,
    );
    for (const g of stated) {
      assert.equal(
        g,
        shipped,
        `${file} offlineModels answer says Gemma ${g}; the apps ship Gemma ${shipped}`,
      );
    }
  }
});
