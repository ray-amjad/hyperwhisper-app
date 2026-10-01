/**
 * #701: the website ignored the OS "Reduce motion" setting.
 *
 * Two global guards fix it, and this file pins both:
 *
 * 1. `app/[locale]/providers.tsx` wraps the tree, inside `<LazyMotion>`, in
 *    framer-motion's `<MotionConfig reducedMotion="user">`. framer-motion's own
 *    default is "never", so without it every `initial`/`whileInView` entrance
 *    slides in at full strength under reduce.
 * 2. `styles/globals.css` ends with a `prefers-reduced-motion: reduce` block that
 *    turns off smooth scrolling, stops the looping decorative keyframes and
 *    collapses every other CSS animation and transition. The !important
 *    universal rule sits inside `@layer base` (#1173): for important
 *    declarations an earlier layer beats a later one and any layer beats
 *    unlayered CSS, so unlayered it lost to HeroUI's `!duration-*` utilities
 *    in `@layer utilities`.
 *
 * These are source checks: the behaviour itself is a browser media query, which
 * neither node nor react-dom/server evaluates; a Chromium run with
 * reducedMotion "reduce" is the behavioural proof.
 */
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const ROOT = fileURLToPath(new URL("..", import.meta.url));
const read = (rel: string) => readFileSync(join(ROOT, rel), "utf8");

test("providers wrap the tree in MotionConfig reducedMotion=\"user\" inside LazyMotion", () => {
  const src = read("app/[locale]/providers.tsx");
  // Strip JSX comments so a commented-out wrapper cannot satisfy the check.
  const code = src.replace(/\{\/\*[\s\S]*?\*\/\}/g, "");

  const lazyOpen = code.indexOf("<LazyMotion");
  const configOpen = code.search(/<MotionConfig\s+reducedMotion="user"\s*>/);
  const children = code.indexOf("{children}");
  const configClose = code.indexOf("</MotionConfig>");
  const lazyClose = code.indexOf("</LazyMotion>");

  assert.ok(lazyOpen >= 0 && lazyClose > lazyOpen, "LazyMotion wrapper present");
  assert.ok(configOpen >= 0, '<MotionConfig reducedMotion="user"> present');
  assert.ok(
    lazyOpen < configOpen &&
      configOpen < children &&
      children < configClose &&
      configClose < lazyClose,
    "MotionConfig sits inside LazyMotion and wraps {children}",
  );
  assert.match(
    code,
    /import\s*\{[^}]*\bMotionConfig\b[^}]*\}\s*from\s*"framer-motion"/,
    "MotionConfig is imported from framer-motion",
  );
});

const REDUCE = /@media\s*\(\s*prefers-reduced-motion\s*:\s*reduce\s*\)\s*\{/;

// Body of the brace block whose opening "{" is the first at or after `at`,
// plus the index just past its closing "}".
function blockAt(css: string, at: number) {
  const open = css.indexOf("{", at);
  let depth = 0;
  let end = open;
  for (; end < css.length; end++) {
    if (css[end] === "{") depth++;
    else if (css[end] === "}" && --depth === 0) break;
  }
  return { body: css.slice(open + 1, end), end: end + 1 };
}

const UNIVERSAL = /\*,\s*\*::before,\s*\*::after\s*\{([^}]*)\}/;

test("globals.css carries the prefers-reduced-motion guard", () => {
  const css = read("styles/globals.css").replace(/\/\*[\s\S]*?\*\//g, "");
  const at = css.search(REDUCE);
  assert.ok(at >= 0, "a prefers-reduced-motion: reduce block exists");

  const block = blockAt(css, at).body.replace(/\s+/g, " ");

  assert.match(block, /html \{ scroll-behavior: auto; \}/, "html scroll-behavior: auto");
  assert.match(
    block,
    /\.animate-blob, \.animate-scroll, \.animate-scroll-x \{ animation: none; \}/,
    "decorative keyframe classes get animation: none",
  );
  // The two normal rules stay unlayered: an unlayered normal rule beats a
  // layered one, so moving them into a layer would weaken them.
  const before = css.slice(0, at);
  const depth = before.split("{").length - before.split("}").length;
  assert.equal(depth, 0, "the html / .animate-* reduce block is top level, not inside an @layer");
  assert.doesNotMatch(block, UNIVERSAL, "the !important universal rule is not in the unlayered block");
});

test("the !important universal rule sits in @layer base, inside a reduce query (#1173)", () => {
  const css = read("styles/globals.css").replace(/\/\*[\s\S]*?\*\//g, "");

  // Every copy of the universal rule must be layered in base; an unlayered
  // copy loses to HeroUI's !important !duration-* utilities.
  const copies = [...css.matchAll(new RegExp(UNIVERSAL.source, "g"))];
  assert.equal(copies.length, 1, "exactly one universal reduced-motion rule");

  const layerAt = css.search(/@layer\s+base\s*\{/);
  assert.ok(layerAt >= 0, "an @layer base block exists");
  const layer = blockAt(css, layerAt);
  const mediaAt = layer.body.search(REDUCE);
  assert.ok(mediaAt >= 0, "@layer base holds a prefers-reduced-motion: reduce query");
  const media = blockAt(layer.body, mediaAt).body;
  const universal = media.match(UNIVERSAL);
  assert.ok(universal, "universal selector rule sits inside @layer base > @media reduce");
  for (const decl of [
    /animation-duration: 0\.01ms !important;/,
    /animation-iteration-count: 1 !important;/,
    /transition-duration: 0\.01ms !important;/,
  ]) {
    assert.match(universal[1], decl);
  }
});

test("tailwind orders base before utilities, so the base-layer guard wins for !important", () => {
  // Tailwind v4 declares the layer order in its own index.css, which
  // `@import "tailwindcss"` pulls in first. If a release reorders it, the
  // guard above stops beating !duration-* again.
  const tw = readFileSync(join(ROOT, "node_modules/tailwindcss/index.css"), "utf8");
  const order = tw.match(/@layer\s+([\w\s,-]+);/);
  assert.ok(order, "tailwindcss/index.css declares a layer order");
  const layers = order[1].split(",").map((l) => l.trim());
  assert.ok(layers.includes("base") && layers.includes("utilities"), `layers: ${layers}`);
  assert.ok(layers.indexOf("base") < layers.indexOf("utilities"), `base precedes utilities: ${layers}`);
});

test("the reduce guard follows the smooth-scroll rule", () => {
  const css = read("styles/globals.css").replace(/\/\*[\s\S]*?\*\//g, "");
  const at = css.search(REDUCE);
  // The guard must come AFTER the unguarded `html { scroll-behavior: smooth }`
  // rule, or the later rule wins at equal specificity.
  const smooth = css.search(/scroll-behavior:\s*smooth/);
  assert.ok(smooth >= 0 && smooth < at, "guard follows the smooth-scroll rule");
});
