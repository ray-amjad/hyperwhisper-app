/**
 * #1147 contract with the REAL `@heroui/input` (2.4.33): a caller's
 * `aria-labelledby` reaches the rendered `<input>` exactly as given.
 *
 * React Aria's `useLabels` prepends the field's own id when a field has both
 * an aria-label and an aria-labelledby, and HeroUI always passes one (the
 * placeholder). That would fold the placeholder back into the name. HeroUI's
 * `getInputProps` then merges the caller's raw props last, which restores the
 * caller's list. If an upgrade changes that order, this test goes red.
 */
import assert from "node:assert/strict";
import test from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { Input } from "@heroui/input";

test("a caller's aria-labelledby reaches the input unchanged", () => {
  const html = renderToStaticMarkup(
    createElement(Input, {
      "aria-labelledby": "heading-id tile-id",
      placeholder: "Amount in USD (5 to 500)",
      type: "number",
    }),
  );
  const input = /<input[^>]*>/.exec(html)?.[0] ?? "";

  assert.match(input, /\saria-labelledby="heading-id tile-id"/);
});
