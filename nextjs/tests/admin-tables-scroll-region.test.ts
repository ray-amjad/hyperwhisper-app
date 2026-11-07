/**
 * #1148 (sibling of #868): the admin Devices and Customers tables scroll
 * sideways on a phone, but their `overflow-x-auto` boxes took no keyboard
 * focus, so a keyboard user could not reach the cut-off columns (WCAG 2.1.1,
 * axe `scrollable-region-focusable`). Each box now has the PR #1145 shape: a
 * named `role="region"` with `tabIndex={0}` and a visible focus ring. The ring
 * is inset: the card around the box clips its overflow, so an outset ring
 * would be cut off.
 *
 * HOW. Renders the REAL page components through `renderToStaticMarkup`, with
 * the tRPC client mocked by the file the components load (see
 * `refresh-button-spinner.test.ts`), and reads every `overflow-x-auto` box.
 *
 * WHAT IT DOES NOT PROVE: an axe run on a signed-in page, or that the arrow
 * keys scroll the focused box. The PR's verify run measures that.
 */
import assert from "node:assert/strict";
import test from "node:test";

import { moduleMock, moduleUrl } from "./module-mock";

function listQuery(data: unknown) {
  return () => ({
    data,
    isLoading: false,
    isFetching: false,
    isPlaceholderData: false,
    error: null,
    refetch: () => undefined,
  });
}

function idleMutation() {
  return {
    mutate: () => undefined,
    reset: () => undefined,
    isPending: false,
    error: null,
    data: undefined,
  };
}

moduleMock.module(moduleUrl("../lib/trpc/client.ts"), {
  namedExports: {
    api: {
      useUtils: () => ({
        admin: { customers: { list: { invalidate: () => undefined } } },
      }),
      admin: {
        devices: { list: { useQuery: listQuery({ devices: [] }) } },
        customers: {
          list: {
            useQuery: listQuery({ customers: [], total: 0, totalPages: 0 }),
          },
          grant: { useMutation: idleMutation },
          refund: { useMutation: idleMutation },
          addCredits: { useMutation: idleMutation },
          updateEmail: { useMutation: idleMutation },
        },
      },
    },
  },
});

const AUTH = "../app/[locale]/user/(authenticated)";
const PAGES = [
  {
    name: "Devices",
    path: `${AUTH}/devices/DevicesClient.tsx`,
    label: "Device activations table",
  },
  {
    name: "Customers",
    path: `${AUTH}/customers/CustomersClient.tsx`,
    label: "Customers table",
  },
];

async function scrollBoxes(path: string): Promise<string[]> {
  const { createElement } = await import("react");
  const { renderToStaticMarkup } = await import("react-dom/server");
  const { default: Page } = (await import(path)) as { default: () => null };
  const html = renderToStaticMarkup(createElement(Page));

  return Array.from(html.matchAll(/<div\b([^>]*\boverflow-x-auto\b[^>]*)>/g)).map(
    (m) => m[1],
  );
}

for (const { name, path, label } of PAGES) {
  test(`${name}: the table's scroll box is a named region with a tab stop and a focus ring`, async () => {
    const boxes = await scrollBoxes(path);

    assert.equal(boxes.length, 1, "exactly 1 sideways-scrolling box");
    const [attrs] = boxes;

    assert.match(attrs, /\brole="region"/);
    assert.match(attrs, new RegExp(`\\baria-label="${label}"`));
    assert.match(attrs, /\btabindex="0"/);
    assert.match(attrs, /\bfocus-visible:outline-2\b/);
    assert.match(attrs, /focus-visible:outline-\[var\(--dusk-lav\)\]/);
    // Inset: the card's overflow-hidden would clip an outset ring.
    assert.match(attrs, /\bfocus-visible:-outline-offset-2\b/);
  });
}
