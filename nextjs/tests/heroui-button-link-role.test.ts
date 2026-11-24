/**
 * #2796: a HeroUI `Button` rendered `as={SomeLink}` navigates, but
 * `@heroui/use-aria-button` gives every element type other than "button" a
 * `role="button"`, so a screen reader announces the CTA as a button and drops
 * it from the links list. A caller's `role="link"` is merged last and wins.
 *
 * The first test pins that contract with the REAL `@heroui/button`. The second
 * walks every `<Button as={…} href=…>` under `app/`, `components/` and `src/`
 * and fails when one has no `role="link"`. A `Button as={…}` with no `href`
 * does not navigate, so it keeps its button role.
 */
import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import { join, relative } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { createElement, type ReactNode } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { Button } from "@heroui/button";
import ts from "typescript";

const root = fileURLToPath(new URL("..", import.meta.url));

function FakeLink(props: { children?: ReactNode; href: string }) {
  return createElement("a", props);
}

function anchor(extra: Record<string, unknown>): string {
  const html = renderToStaticMarkup(
    createElement(Button, { as: FakeLink, href: "/credits", ...extra }, "Go"),
  );

  return /<a\b[^>]*>/.exec(html)?.[0] ?? "";
}

test('HeroUI Button as a link component: role="link" overrides role="button"', () => {
  assert.match(anchor({}), /\srole="button"/);
  assert.match(anchor({ role: "link" }), /\srole="link"/);
  assert.doesNotMatch(anchor({ role: "link" }), /\srole="button"/);
});

function tsxFiles(dir: string): string[] {
  return readdirSync(dir, { recursive: true, encoding: "utf8" })
    .filter((f) => f.endsWith(".tsx"))
    .map((f) => join(dir, f));
}

function isLinkRole(init: ts.JsxAttributeValue | undefined): boolean {
  const value =
    init && ts.isJsxExpression(init) ? init.expression : (init as ts.Node);

  return !!value && ts.isStringLiteralLike(value) && value.text === "link";
}

function attr(node: ts.JsxAttributes, name: string) {
  return node.properties.find(
    (p): p is ts.JsxAttribute =>
      ts.isJsxAttribute(p) && p.name.getText() === name,
  );
}

test('every navigating <Button as={…}> passes role="link"', () => {
  const missing: string[] = [];
  let checked = 0;

  for (const file of [
    ...tsxFiles(join(root, "app")),
    ...tsxFiles(join(root, "components")),
    ...tsxFiles(join(root, "src")),
  ]) {
    const source = ts.createSourceFile(
      file,
      readFileSync(file, "utf8"),
      ts.ScriptTarget.Latest,
      true,
      ts.ScriptKind.TSX,
    );
    const visit = (node: ts.Node) => {
      if (
        (ts.isJsxOpeningElement(node) || ts.isJsxSelfClosingElement(node)) &&
        node.tagName.getText() === "Button"
      ) {
        const as = attr(node.attributes, "as");

        if (
          as?.initializer &&
          ts.isJsxExpression(as.initializer) &&
          attr(node.attributes, "href")
        ) {
          checked++;

          if (!isLinkRole(attr(node.attributes, "role")?.initializer)) {
            const { line } = source.getLineAndCharacterOfPosition(
              node.getStart(),
            );

            missing.push(`${relative(root, file)}:${line + 1}`);
          }
        }
      }
      ts.forEachChild(node, visit);
    };

    visit(source);
  }

  // The homepage hero and /credits CTAs were the other two. The Dusk homepage
  // draws them as plain <button>s and a locale <Link> (an <a>, with its native
  // link role), so /purchase-success is the one Button site left.
  assert.ok(checked >= 1, `expected the known CTA, found ${checked}`);
  assert.deepEqual(missing, []);
});
