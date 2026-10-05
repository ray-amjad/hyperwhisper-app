/**
 * #890: on `/[locale]/choosing-a-model` the recommendation card draws its 4
 * headline stats (Word errors, Cost, Per audio minute, Match) as a `<dl>`.
 * The "Cost" wrapper held a stray `<p>` (the per-minute dollar note) beside
 * its `<dt>` and `<dd>`, so axe `definition-list` (serious) flagged the list.
 *
 * This file renders the REAL component to server markup, finds the stats
 * `<dl>`, and checks every direct child of every wrapper `<div>` is a DT or a
 * DD. It also checks the dollar note is still rendered, so the fix cannot
 * pass by deleting it.
 *
 * WHAT IT DOES NOT PROVE: what axe reports in a browser, or that the card
 * renders pixel-identical. The verify round reads both from Chromium.
 */
import assert from "node:assert/strict";
import test from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";

import ModelPicker from "@/components/choosing-a-model/ModelPicker";

const VOID = new Set(["br", "img", "input", "hr", "meta", "link", "source", "wbr"]);

interface Node {
  tag: string;
  children: Node[];
  text: string;
}

/** A minimal HTML tree builder. Enough for React's well-formed server output. */
function parse(html: string): Node {
  const root: Node = { tag: "#root", children: [], text: "" };
  const stack: Node[] = [root];
  const re = /<(\/?)([a-zA-Z][a-zA-Z0-9]*)\b[^>]*?(\/?)>|([^<]+)/g;

  for (const m of Array.from(html.matchAll(re))) {
    const top = stack[stack.length - 1];

    if (m[4] !== undefined) {
      for (const n of stack) n.text += m[4];
      continue;
    }

    const tag = m[2].toLowerCase();

    if (m[1]) {
      assert.equal(top.tag, tag, `mismatched </${tag}> inside <${top.tag}>`);
      stack.pop();
      continue;
    }

    const node: Node = { tag, children: [], text: "" };

    top.children.push(node);
    if (!m[3] && !VOID.has(tag)) stack.push(node);
  }

  return root;
}

function findAll(node: Node, tag: string, out: Node[] = []): Node[] {
  if (node.tag === tag) out.push(node);
  for (const child of node.children) findAll(child, tag, out);

  return out;
}

function statsList(): Node {
  const html = renderToStaticMarkup(
    createElement(ModelPicker, { measured: {}, regions: [] }),
  );
  const dl = findAll(parse(html), "dl").find((d) => /Word errors/.test(d.text));

  assert.ok(dl, "the picker rendered no stats <dl>");

  return dl;
}

test("the stats <dl> holds 4 wrapper divs, one per headline stat", () => {
  const dl = statsList();

  assert.deepEqual(
    dl.children.map((c) => c.tag),
    ["div", "div", "div", "div"],
  );
});

test("every child of every stats wrapper is a <dt> or a <dd>", () => {
  const dl = statsList();

  dl.children.forEach((wrap, i) => {
    const tags = wrap.children.map((c) => c.tag.toUpperCase());

    assert.ok(tags.length >= 2, `wrapper ${i + 1} is empty`);
    assert.equal(tags[0], "DT", `wrapper ${i + 1} does not open with a <dt>: ${tags}`);
    for (const tag of tags) {
      assert.ok(
        tag === "DT" || tag === "DD",
        `wrapper ${i + 1} holds a <${tag.toLowerCase()}>: ${tags}`,
      );
    }
  });
});

test("the Cost stat still carries its per-minute dollar note", () => {
  const cost = statsList().children.find(
    (wrap) => wrap.children[0]?.text.trim() === "Cost",
  );

  assert.ok(cost, "no stats wrapper is titled Cost");
  assert.match(cost.text, /no per-minute cost|per 1,000 min/);
});
