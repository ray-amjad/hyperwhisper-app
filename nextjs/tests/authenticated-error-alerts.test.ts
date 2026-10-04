/**
 * #1128 (sibling of #760): behind the sign-in, the Devices and Customers load
 * errors rendered in a plain `<div>`/`<p>`, so a screen reader was not told
 * the load failed. Each error block that MOUNTS on a failure now carries
 * `role="alert"` (the #1126 SignInClient precedent), and so do the sibling
 * mutation errors on the Customers page, the Dashboard Account Keys load
 * error and the BillingCard portal error.
 *
 * HOW. The two page-level blocks are rendered for real: `DevicesClient` and
 * `CustomersClient` go through `renderToStaticMarkup` with the tRPC client
 * mocked by the file the components load (see `devices-range-aria-pressed`),
 * once with a failed query and once with a settled empty one. The mutation
 * errors sit behind dialog/form state that a static render cannot open, so a
 * last check reads the source: every `{…error && (` block's first element
 * carries `role="alert"`.
 *
 * WHAT IT DOES NOT PROVE: what a given screen reader announces.
 */
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test, { mock } from "node:test";

/** See `devices-range-aria-pressed.test.ts`: `@types/node@20` lacks this. */
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

const LOAD_ERROR = "Failed to load: upstream timed out";

/** Flipped per test; read by both mocked list queries. */
let failLoad = false;

function listQuery(emptyData: unknown) {
  return () =>
    failLoad
      ? {
          data: undefined,
          isLoading: false,
          isFetching: false,
          error: { message: LOAD_ERROR },
          refetch: () => undefined,
        }
      : {
          data: emptyData,
          isLoading: false,
          isFetching: false,
          error: null,
          refetch: () => undefined,
        };
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

moduleMock.module(new URL("../lib/trpc/client.ts", import.meta.url).href, {
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
const PAGES = {
  devices: `${AUTH}/devices/DevicesClient.tsx`,
  customers: `${AUTH}/customers/CustomersClient.tsx`,
};

async function render(path: string): Promise<string> {
  const { createElement } = await import("react");
  const { renderToStaticMarkup } = await import("react-dom/server");
  const { default: Page } = (await import(path)) as { default: () => null };

  return renderToStaticMarkup(createElement(Page));
}

/** The inner HTML of every `<div role="alert">` in the markup. */
function alertBlocks(html: string): string[] {
  return Array.from(
    html.matchAll(/<div\b[^>]*\brole="alert"[^>]*>([\s\S]*?)<\/div>/g),
  ).map((m) => m[1]);
}

for (const [name, path] of Object.entries(PAGES)) {
  test(`${name}: a failed load renders one role="alert" holding the error`, async () => {
    failLoad = true;
    const blocks = alertBlocks(await render(path));

    assert.equal(blocks.length, 1, "exactly one alert");
    assert.ok(blocks[0].includes(LOAD_ERROR), blocks[0]);
  });

  test(`${name}: a settled load renders no role="alert"`, async () => {
    failLoad = false;
    const html = await render(path);

    assert.doesNotMatch(html, /role="alert"/);
  });
}

test("every conditional error block behind sign-in mounts with role=alert", () => {
  const files = [
    `${AUTH}/devices/DevicesClient.tsx`,
    `${AUTH}/customers/CustomersClient.tsx`,
    `${AUTH}/dashboard/UserDashboardClient.tsx`,
    "../components/customer/dashboard/BillingCard.tsx",
  ];
  let checked = 0;

  for (const file of files) {
    const source = readFileSync(new URL(file, import.meta.url), "utf8");
    // `{error && (` / `{fooMutation.error && (` / `) : error ? (`, then the
    // opening tag of the element that block mounts.
    const blocks = Array.from(
      source.matchAll(/(?:\{[\w.]*\berror &&|: error \?) \(\s*(<[a-z]+\b[^>]*>)/g),
    );

    for (const m of blocks) {
      assert.match(m[1], /\brole="alert"/, `${file}: ${m[1]}`);
      checked += 1;
    }
  }

  // 1 Devices + 5 Customers + 1 Dashboard + 1 BillingCard.
  assert.equal(checked, 8);
});
