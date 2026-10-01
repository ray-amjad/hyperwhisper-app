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
 *    collapses every other CSS animation and transition.
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

test("globals.css carries the prefers-reduced-motion guard", () => {
  const css = read("styles/globals.css").replace(/\/\*[\s\S]*?\*\//g, "");
  const at = css.search(/@media\s*\(\s*prefers-reduced-motion\s*:\s*reduce\s*\)\s*\{/);
  assert.ok(at >= 0, "a prefers-reduced-motion: reduce block exists");

  // Take the block body by brace matching.
  const open = css.indexOf("{", at);
  let depth = 0;
  let end = open;
  for (; end < css.length; end++) {
    if (css[end] === "{") depth++;
    else if (css[end] === "}" && --depth === 0) break;
  }
  const block = css.slice(open + 1, end).replace(/\s+/g, " ");

  assert.match(block, /html \{ scroll-behavior: auto; \}/, "html scroll-behavior: auto");
  assert.match(
    block,
    /\.animate-blob, \.animate-scroll, \.animate-scroll-x \{ animation: none; \}/,
    "decorative keyframe classes get animation: none",
  );
  const universal = block.match(/\*, \*::before, \*::after \{([^}]*)\}/);
  assert.ok(universal, "universal selector rule present");
  for (const decl of [
    /animation-duration: 0\.01ms !important;/,
    /animation-iteration-count: 1 !important;/,
    /transition-duration: 0\.01ms !important;/,
  ]) {
    assert.match(universal[1], decl);
  }

  // The guard must come AFTER the unguarded `html { scroll-behavior: smooth }`
  // rule, or the later rule wins at equal specificity.
  const smooth = css.search(/scroll-behavior:\s*smooth/);
  assert.ok(smooth >= 0 && smooth < at, "guard follows the smooth-scroll rule");
});
