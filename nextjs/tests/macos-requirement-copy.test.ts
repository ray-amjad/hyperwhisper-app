/**
 * Every 2.x Mac build is built for MACOSX_DEPLOYMENT_TARGET 14.6, and the
 * appcast says minimumSystemVersion 14.6, so macOS 14.0–14.5 cannot open it.
 * The site once said "macOS 14+" in every locale's hero badge and requirements
 * FAQ and in two comparison posts (#2571). Reads the Xcode project for the real
 * floor and fails on any site copy that states a lower one.
 */
import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

const root = fileURLToPath(new URL("..", import.meta.url));
const messagesDir = join(root, "messages");
const postsDir = join(root, "src/content/code-posts");
const pbxproj = join(root, "../app/macos/hyperwhisper.xcodeproj/project.pbxproj");

function deploymentTarget(): [number, number] {
  const targets = Array.from(readFileSync(pbxproj, "utf8").matchAll(/MACOSX_DEPLOYMENT_TARGET = (\d+)\.(\d+);/g));
  assert.ok(targets.length > 0, "no MACOSX_DEPLOYMENT_TARGET in project.pbxproj");
  return targets
    .map((m): [number, number] => [Number(m[1]), Number(m[2])])
    .reduce((a, b) => (b[0] > a[0] || (b[0] === a[0] && b[1] > a[1]) ? b : a));
}

// Every "macOS <major>[.<minor>]" the copy states, e.g. "macOS 14+", "macOS 14.6以降".
function statedVersions(text: string): { raw: string; v: [number, number] }[] {
  return Array.from(text.matchAll(/macOS\s?(\d+)(?:\.(\d+))?/g)).map((m) => ({
    raw: m[0],
    v: [Number(m[1]), Number(m[2] ?? 0)],
  }));
}

function below(v: [number, number], floor: [number, number]): boolean {
  return v[0] < floor[0] || (v[0] === floor[0] && v[1] < floor[1]);
}

test("the check flags the old 'macOS 14+' copy", () => {
  const stale = statedVersions("Mac: macOS 14+ on Apple Silicon. macOS 14以降");
  assert.equal(stale.filter((s) => below(s.v, [14, 6])).length, 2);
  assert.equal(statedVersions("macOS 14.6+").filter((s) => below(s.v, [14, 6])).length, 0);
});

test("every locale's Mac badge and requirements answer state the real macOS floor", () => {
  const floor = deploymentTarget();
  const files = readdirSync(messagesDir).filter((f) => f.endsWith(".json"));
  assert.ok(files.length >= 40, `expected every locale, found ${files.length}`);
  for (const file of files) {
    const messages = JSON.parse(readFileSync(join(messagesDir, file), "utf8"));
    for (const [key, text] of [
      ["hero.macos", messages.hero.macos],
      ["faq.questions.requirements.answer", messages.faq.questions.requirements.answer],
    ] as const) {
      const stated = statedVersions(text);
      assert.ok(stated.length > 0, `${file} ${key} names no macOS version: ${text}`);
      for (const s of stated) {
        assert.ok(!below(s.v, floor), `${file} ${key} says "${s.raw}", below the ${floor.join(".")} deployment target`);
      }
    }
  }
});

test("no comparison post states a macOS floor below the deployment target", () => {
  const floor = deploymentTarget();
  for (const file of readdirSync(postsDir).filter((f) => f.startsWith("hyperwhisper-"))) {
    const post = readFileSync(join(postsDir, file), "utf8");
    for (const m of Array.from(post.matchAll(/HyperWhisper[^.<]*?(macOS\s?\d+(?:\.\d+)?)/g))) {
      for (const s of statedVersions(m[1])) {
        assert.ok(!below(s.v, floor), `${file} says HyperWhisper runs on "${s.raw}", below ${floor.join(".")}`);
      }
    }
  }
});
