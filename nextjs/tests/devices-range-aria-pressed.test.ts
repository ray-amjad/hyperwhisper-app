/**
 * #1304 (sibling of #1303): on the admin Devices page
 * (`/[locale]/user/devices`) the 7 / 30 / 90-day range selector showed the
 * chosen range by colour only. Each range `<button>` now carries
 * `aria-pressed` and `type="button"`, and the row is a `role="group"` named
 * "Time range" (the #1303 LatencyMatrix precedent; this row has no visible
 * label, so it uses `aria-label`).
 *
 * HOW. Renders the REAL `DevicesClient` through `renderToStaticMarkup`. The
 * tRPC client is mocked by the file the component loads (see
 * `download-modal-email-wiring.test.ts`): `api.admin.devices.list.useQuery`
 * records its input and answers with an empty, settled query.
 *
 * WHAT IT DOES NOT PROVE: that a click moves the pressed state, or what a
 * screen reader announces.
 */
import assert from "node:assert/strict";
import test, { mock } from "node:test";

/**
 * `mock.module` is a real Node 22 API (behind `--experimental-test-module-mocks`,
 * which `npm test` passes) but this repo pins `@types/node@20`, which has no
 * declaration for it. Narrowed to the one option used here.
 */
interface ModuleMocker {
  module(
    specifier: string,
    options: {
      namedExports?: Record<string, unknown>;
      defaultExport?: unknown;
    },
  ): void;
}

const moduleMock = mock as unknown as ModuleMocker;

function moduleUrl(relative: string): string {
  return new URL(relative, import.meta.url).href;
}

const queryInputs: Array<{ days: number }> = [];

moduleMock.module(moduleUrl("../lib/trpc/client.ts"), {
  namedExports: {
    api: {
      admin: {
        devices: {
          list: {
            useQuery: (input: { days: number }) => {
              queryInputs.push(input);

              return {
                data: { devices: [] },
                isLoading: false,
                error: null,
                refetch: () => undefined,
              };
            },
          },
        },
      },
    },
  },
});

// Variable specifier, and deferred: a static import would bind before the
// mock above, and a literal `.tsx` specifier is a TS5097 error here.
const COMPONENT_PATH =
  "../app/[locale]/user/(authenticated)/devices/DevicesClient.tsx";

async function render(): Promise<string> {
  const { createElement } = await import("react");
  const { renderToStaticMarkup } = await import("react-dom/server");
  const { default: DevicesClient } = (await import(COMPONENT_PATH)) as {
    default: () => null;
  };
  const html = renderToStaticMarkup(createElement(DevicesClient));

  assert.ok(queryInputs.length > 0, "the tRPC mock never bound");

  return html;
}

const RANGE_LABELS = ["7 days", "30 days", "90 days"];

/** Every `<button>` whose text is exactly one of the range labels. */
function rangeButtons(html: string): Array<{ label: string; attrs: string }> {
  return Array.from(html.matchAll(/<button\b([^>]*)>([^<]*)<\/button>/g))
    .filter((m) => RANGE_LABELS.includes(m[2]))
    .map((m) => ({ label: m[2], attrs: m[1] }));
}

test("exactly 1 range button is pressed on first load, and it is 30 days", async () => {
  const html = await render();
  const buttons = rangeButtons(html);

  assert.deepEqual(
    buttons.map((b) => b.label),
    RANGE_LABELS,
  );

  const pressed = buttons.filter((b) => /aria-pressed="true"/.test(b.attrs));

  assert.equal(pressed.length, 1, "exactly 1 range button is pressed");
  assert.equal(pressed[0].label, "30 days");

  for (const b of buttons) {
    if (b.label !== "30 days") {
      assert.match(b.attrs, /aria-pressed="false"/, b.label);
    }
    assert.match(b.attrs, /\btype="button"/, b.label);
  }

  // The pressed button and the query agree on the default range.
  assert.equal(queryInputs.at(-1)?.days, 30);
});

test("the range buttons sit in a group named Time range", async () => {
  const html = await render();
  const open = /<div\b([^>]*)\brole="group"([^>]*)>/.exec(html);

  assert.ok(open, "no role=group element");
  assert.match(open[0], /aria-label="Time range"/);

  const start = open.index + open[0].length;
  const row = html.slice(start, html.indexOf("</div>", start));

  assert.deepEqual(
    rangeButtons(row).map((b) => b.label),
    RANGE_LABELS,
  );
});
