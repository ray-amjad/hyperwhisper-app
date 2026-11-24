import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const ROOT = fileURLToPath(new URL("..", import.meta.url));
const en = JSON.parse(readFileSync(join(ROOT, "messages/en.json"), "utf8")) as {
  metadata: { title: string; description: string };
};

test("the English homepage title and description name dictation and speech-to-text", () => {
  const { title, description } = en.metadata;
  assert.match(title, /dictation/i);
  assert.match(title, /speech-to-text/i);
  assert.match(title, /Linux/);
  assert.match(description, /dictation/i);
  assert.match(description, /speech-to-text/i);
  assert.match(description, /Linux/);
});
