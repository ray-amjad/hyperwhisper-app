/**
 * HyperWhisper Cloud is licensed-only: a request without an account key gets a
 * 401 (hyperwhisper-cloud/src/middleware/auth.ts), so there is no free daily
 * cloud allowance. The site once promised "5 minutes of cloud transcription a
 * day" in every locale's subscription FAQ and in the Spokenly comparison post
 * (#2548). Reads the shipped copy itself, every locale.
 */
import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

const root = fileURLToPath(new URL("..", import.meta.url));
const messagesDir = join(root, "messages");
const locales = readdirSync(messagesDir).filter((f) => f.endsWith(".json"));

test("no locale's subscription FAQ answer promises a number of free cloud minutes", () => {
  assert.ok(locales.length >= 40, `expected every locale, found ${locales.length}`);
  for (const file of locales) {
    const messages = JSON.parse(readFileSync(join(messagesDir, file), "utf8"));
    const answer: string = messages.faq.questions.lifetime.answer;
    assert.ok(answer.includes("HyperWhisper Cloud"), `${file}: answer still names HyperWhisper Cloud`);
    assert.doesNotMatch(answer, /\d/, `${file}: lifetime answer carries a number: ${answer}`);
  }
});

test("the Spokenly comparison post promises no free cloud minutes", () => {
  const post = readFileSync(join(root, "src/content/code-posts/hyperwhisper-vs-spokenly.ts"), "utf8");
  assert.doesNotMatch(post, /minutes? (of cloud|a day|per day)/i);
});
