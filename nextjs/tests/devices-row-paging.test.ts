/**
 * #1661: on the admin Devices page an expanded key rendered every device it
 * held. With 1,284 devices the page grew to 72,662 px and the count badge read
 * "1284". The expanded key now asks for 50 devices at a time, shows a
 * "Show more" button while more are left, has a search box, and formats the
 * counts.
 *
 * HOW. Renders the REAL `DeviceRow` (expanded) through `renderToStaticMarkup`,
 * with the tRPC client mocked by the file the component loads (the
 * `devices-row-disclosure-button.test.ts` pattern). The mocked
 * `useInfiniteQuery` records what the row asks for and answers with one page.
 *
 * WHAT IT DOES NOT PROVE: that a click on "Show more" fetches the next page,
 * or that typing debounces into a new query. A static render cannot fire
 * events; the server side of both is covered in `trpc-http.test.ts` and
 * `account-keys-devices.test.ts`.
 */
import assert from "node:assert/strict";
import test, { mock } from "node:test";

/** See `devices-range-aria-pressed.test.ts`: `@types/node@20` lacks this. */
interface ModuleMocker {
  module(
    specifier: string,
    options: { namedExports?: Record<string, unknown> },
  ): void;
}

const moduleMock = mock as unknown as ModuleMocker;

interface Page {
  devices: Array<{
    deviceId: string;
    deviceName: string | null;
    createdAt: Date;
    lastValidatedAt: Date;
  }>;
  total: number;
  nextCursor: number | null;
}

interface InfiniteCall {
  input: Record<string, unknown>;
  opts: {
    enabled?: boolean;
    getNextPageParam: (last: Page) => number | undefined;
  };
}

const calls: InfiniteCall[] = [];
let answer: { pages: Page[]; hasNextPage: boolean } = {
  pages: [],
  hasNextPage: false,
};

function page(count: number, total: number, from = 0): Page {
  const next = from + count;
  return {
    devices: Array.from({ length: count }, (_, i) => ({
      deviceId: `device-${from + i}`,
      deviceName: `Mac ${from + i}`,
      createdAt: new Date("2026-08-01T00:00:00Z"),
      lastValidatedAt: new Date("2026-09-01T00:00:00Z"),
    })),
    total,
    nextCursor: next < total ? next : null,
  };
}

moduleMock.module(new URL("../lib/trpc/client.ts", import.meta.url).href, {
  namedExports: {
    api: {
      admin: {
        devices: {
          forLicense: {
            useInfiniteQuery: (
              input: InfiniteCall["input"],
              opts: InfiniteCall["opts"],
            ) => {
              calls.push({ input, opts });
              return {
                data: { pages: answer.pages },
                isLoading: false,
                isFetching: false,
                isFetchingNextPage: false,
                hasNextPage: answer.hasNextPage,
                fetchNextPage: () => undefined,
              };
            },
          },
        },
      },
    },
  },
});

const COMPONENT_PATH =
  "../app/[locale]/user/(authenticated)/devices/DevicesClient.tsx";

const ROW = {
  licenseKeyId: "lk-many",
  email: "many@example.com",
  licenseKey: "HW-AAAA-BBBB-CCCC",
  deviceCount: 1284,
};

async function renderRow(): Promise<string> {
  const { createElement } = await import("react");
  const { renderToStaticMarkup } = await import("react-dom/server");
  const { DeviceRow } = (await import(COMPONENT_PATH)) as {
    DeviceRow: (props: Record<string, unknown>) => null;
  };
  calls.length = 0;

  return renderToStaticMarkup(
    createElement(
      "table",
      null,
      createElement(
        "tbody",
        null,
        createElement(DeviceRow, {
          row: ROW,
          days: 30,
          truncateKey: (k: string) => k,
          expanded: true,
          onToggle: () => undefined,
        }),
      ),
    ),
  );
}

/** The render's text with React's `<!-- -->` text separators removed. */
const plain = (html: string) => html.replace(/<!-- -->/g, "");

const deviceCount = (html: string) =>
  Array.from(html.matchAll(/<bdi class="text-white text-sm">/g)).length;

test("an expanded key asks for 50 devices with no search, and pages by nextCursor", async () => {
  answer = { pages: [page(50, 1284)], hasNextPage: true };
  await renderRow();

  assert.equal(calls.length, 1);
  const [{ input, opts }] = calls;
  assert.deepEqual(input, {
    licenseKeyId: "lk-many",
    days: 30,
    search: undefined,
    limit: 50,
  });
  assert.equal(opts.enabled, true);
  assert.equal(opts.getNextPageParam(page(50, 1284)), 50);
  assert.equal(opts.getNextPageParam(page(34, 1284, 1250)), undefined);
});

test("the first page shows 50 rows, a formatted badge and count, Show more and a search box", async () => {
  answer = { pages: [page(50, 1284)], hasNextPage: true };
  const html = await renderRow();

  assert.equal(deviceCount(html), 50);
  assert.match(html, /rounded-full text-sm font-medium">1,284<\/span>/);
  assert.match(plain(html), /Showing 50 of 1,284 devices</);
  assert.match(html, /<button [^>]*type="button"[^>]*>Show 50 more<\/button>/);
  assert.match(
    html,
    /<input aria-label="Search this key&#x27;s devices by name or id"[^>]*type="search" value=""/,
  );
  // The #1307 disclosure still points at the panel it opened.
  const controls = /aria-controls="([^"]+)"/.exec(html)?.[1];
  assert.ok(controls, "aria-controls is set while open");
  assert.match(html, new RegExp(`<tr id="${controls}">`));
});

test("loaded pages stack, and the last one drops Show more", async () => {
  answer = {
    pages: [page(50, 120), page(50, 120, 50), page(20, 120, 100)],
    hasNextPage: false,
  };
  const html = await renderRow();

  assert.equal(deviceCount(html), 120);
  assert.match(plain(html), /Showing 120 of 120 devices</);
  assert.doesNotMatch(html, /more<\/button>/);
});

test("the last partial page offers only what is left", async () => {
  answer = { pages: [page(50, 70)], hasNextPage: true };
  const html = await renderRow();

  assert.match(html, />Show 20 more<\/button>/);
});
