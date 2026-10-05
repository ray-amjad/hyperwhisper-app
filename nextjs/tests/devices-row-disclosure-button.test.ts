/**
 * #1307 (sibling of #1304): on the admin Devices page
 * (`/[locale]/user/devices`) each licence row opened its devices on a mouse
 * click only. The row was a bare `<tr onClick>`: not in the Tab order, no
 * Enter / Space, no `aria-expanded`. The first cell now holds a real
 * `<button type="button" aria-expanded>` that toggles the row; the row click
 * stays for the mouse.
 *
 * HOW. Renders the REAL `DevicesClient` through `renderToStaticMarkup`, with
 * the tRPC client mocked by the file the component loads (the
 * `devices-range-aria-pressed.test.ts` pattern). The list query answers with
 * 2 licence rows so `DeviceRow` renders; the per-licence query answers with
 * an idle query.
 *
 * WHAT IT DOES NOT PROVE: that a click toggles exactly once (the button stops
 * propagation to the row's onClick), that Enter / Space open the row, or what
 * a screen reader announces. A static render cannot fire events.
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

const LICENCE_ROWS = [
  {
    licenseKeyId: "lk-row-one",
    email: "first@example.com",
    licenseKey: "HW-AAAA-BBBB-CCCC",
    deviceCount: 2,
  },
  {
    licenseKeyId: "lk-row-two",
    email: "second@example.com",
    licenseKey: "HW-DDDD-EEEE-FFFF",
    deviceCount: 1,
  },
];

const forLicenseCalls: Array<{ enabled?: boolean }> = [];

moduleMock.module(moduleUrl("../lib/trpc/client.ts"), {
  namedExports: {
    api: {
      admin: {
        devices: {
          list: {
            useQuery: () => ({
              data: { devices: LICENCE_ROWS },
              isLoading: false,
              error: null,
              refetch: () => undefined,
            }),
          },
          forLicense: {
            useQuery: (_input: unknown, opts: { enabled?: boolean }) => {
              forLicenseCalls.push(opts);

              return { data: undefined, isLoading: false };
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

  return renderToStaticMarkup(createElement(DevicesClient));
}

/** The body rows, one string per `<tr>…</tr>`, without the header row. */
function bodyRows(html: string): string[] {
  const start = html.indexOf("<tbody");
  const end = html.indexOf("</tbody>", start);

  assert.ok(start >= 0 && end > start, "no <tbody> in the render");

  return Array.from(
    html.slice(start, end).matchAll(/<tr\b[^>]*>[\s\S]*?<\/tr>/g),
  ).map((m) => m[0]);
}

test("each licence row holds exactly 1 button, and it is collapsed", async () => {
  const html = await render();
  const rows = bodyRows(html);

  // Collapsed on first load: one <tr> per licence and no device rows.
  assert.equal(rows.length, LICENCE_ROWS.length, "one <tr> per licence");
  assert.equal(forLicenseCalls.length, LICENCE_ROWS.length, "DeviceRow ran");
  for (const opts of forLicenseCalls) {
    assert.equal(opts.enabled, false, "no row is open on first load");
  }

  rows.forEach((tr, i) => {
    const email = LICENCE_ROWS[i].email;

    assert.ok(tr.includes(email), `row ${i} is ${email}`);

    const buttons = Array.from(tr.matchAll(/<button\b([^>]*)>/g)).map(
      (m) => m[1],
    );

    assert.equal(buttons.length, 1, `${email}: exactly 1 <button>`);
    assert.match(buttons[0], /aria-expanded="false"/, email);
    assert.match(buttons[0], /\btype="button"/, email);
    // aria-controls is set only while the devices row it names exists.
    assert.doesNotMatch(buttons[0], /aria-controls=/, email);
  });
});

test("the button wraps the email, so its accessible name is the account", async () => {
  const html = await render();

  bodyRows(html).forEach((tr, i) => {
    const button = /<button\b[^>]*>([\s\S]*?)<\/button>/.exec(tr);

    assert.ok(button, `row ${i} has a button`);
    assert.ok(
      button[1].includes(LICENCE_ROWS[i].email),
      `row ${i}: the email is inside the button`,
    );
  });
});
