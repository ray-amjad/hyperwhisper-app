/**
 * #1948: the admin Devices table rendered every Account Key with devices in
 * the range, at once, with no total. With 2,000 keys that is 2,000 rows. The
 * table now asks for 50 keys at a time, shows "Showing X of Y Account Keys"
 * and a "Show more" button while more are left, and has an email / Account
 * Key search box, so paging never hides a key.
 *
 * HOW. Renders the REAL `DevicesClient` through `renderToStaticMarkup`, with
 * the tRPC client mocked by the file the component loads (the
 * `devices-row-paging.test.ts` pattern). The mocked `list.useInfiniteQuery`
 * records what the page asks for and answers with the pages set per test.
 *
 * WHAT IT DOES NOT PROVE: that a click on "Show more" fetches the next page,
 * or that typing debounces into a new query (a static render fires no
 * events), nor the "No Account Keys match that search." copy, which needs a
 * typed search. The server side (paging, total, stable order, literal search)
 * is covered in `trpc-http.test.ts` and `account-keys-devices.test.ts`.
 */
import assert from "node:assert/strict";
import test from "node:test";

import { keepPreviousData } from "@tanstack/react-query";

import { moduleMock, moduleUrl } from "./module-mock";

interface Row {
  licenseKeyId: string;
  email: string;
  licenseKey: string;
  deviceCount: number;
}

interface Page {
  devices: Row[];
  total: number;
  nextCursor: number | null;
}

interface InfiniteCall {
  input: Record<string, unknown>;
  opts: {
    getNextPageParam: (last: Page) => number | undefined;
    placeholderData?: unknown;
  };
}

const calls: InfiniteCall[] = [];

interface State {
  pages: Page[];
  hasNextPage: boolean;
  isFetching: boolean;
  isFetchingNextPage: boolean;
  isPlaceholderData: boolean;
}

const IDLE: State = {
  pages: [],
  hasNextPage: false,
  isFetching: false,
  isFetchingNextPage: false,
  isPlaceholderData: false,
};
let state: State = IDLE;

function page(count: number, total: number, from = 0): Page {
  const next = from + count;
  return {
    devices: Array.from({ length: count }, (_, i) => ({
      licenseKeyId: `key-${from + i}`,
      email: `buyer${from + i}@example.com`,
      licenseKey: `HW-FAKE-${String(from + i).padStart(4, "0")}`,
      deviceCount: 3,
    })),
    total,
    nextCursor: next < total ? next : null,
  };
}

moduleMock.module(moduleUrl("../lib/trpc/client.ts"), {
  namedExports: {
    api: {
      admin: {
        devices: {
          list: {
            useInfiniteQuery: (
              input: InfiniteCall["input"],
              opts: InfiniteCall["opts"],
            ) => {
              calls.push({ input, opts });
              return {
                data: { pages: state.pages },
                isLoading: false,
                isFetching: state.isFetching,
                isFetchingNextPage: state.isFetchingNextPage,
                isPlaceholderData: state.isPlaceholderData,
                hasNextPage: state.hasNextPage,
                fetchNextPage: () => undefined,
                error: null,
                refetch: () => undefined,
              };
            },
          },
          // Each row's (closed) device panel.
          forLicense: {
            useInfiniteQuery: () => ({
              data: undefined,
              isLoading: false,
              isFetching: false,
              isFetchingNextPage: false,
              hasNextPage: false,
              fetchNextPage: () => undefined,
            }),
          },
        },
      },
    },
  },
});

const COMPONENT_PATH =
  "../app/[locale]/user/(authenticated)/devices/DevicesClient.tsx";

async function render(next: Partial<State>): Promise<string> {
  state = { ...IDLE, ...next };
  const { createElement } = await import("react");
  const { renderToStaticMarkup } = await import("react-dom/server");
  const { default: DevicesClient } = (await import(COMPONENT_PATH)) as {
    default: () => null;
  };
  calls.length = 0;
  return renderToStaticMarkup(createElement(DevicesClient));
}

/** The render's text with React's `<!-- -->` text separators removed. */
const plain = (html: string) => html.replace(/<!-- -->/g, "");

/** Key rows drawn in the table (each row's email label). */
const rowCount = (html: string) =>
  Array.from(
    html.matchAll(/<span class="text-\[var\(--dusk-ink\)\] \[overflow-wrap:anywhere\]">/g),
  ).length;

/** The class list of the table card (HeightTween's box). */
const cardClass = (html: string) =>
  /<div class="(rounded-\[20px\] [^"]*)" style="/.exec(html)?.[1] ?? "";

test("the table asks for 50 keys with no search, pages by nextCursor, and keeps the rows on screen", async () => {
  await render({ pages: [page(50, 2000)], hasNextPage: true });

  assert.equal(calls.length, 1);
  const [{ input, opts }] = calls;
  assert.deepEqual(input, { days: 30, search: undefined, limit: 50 });
  assert.equal(opts.getNextPageParam(page(50, 2000)), 50);
  assert.equal(opts.getNextPageParam(page(50, 2000, 1950)), undefined);
  // A range switch or a new search keeps the previous rows, dimmed (#863).
  assert.equal(opts.placeholderData, keepPreviousData);
});

test("2,000 keys: one page of 50 rows, the total, Show more and a search box", async () => {
  const html = await render({ pages: [page(50, 2000)], hasNextPage: true });

  assert.equal(rowCount(html), 50);
  assert.match(plain(html), /Showing 50 of 2,000 Account Keys</);
  assert.match(html, /<button [^>]*type="button"[^>]*>Show 50 more<\/button>/);
  assert.match(
    html,
    /<input aria-label="Search Account Keys by email or key"[^>]*placeholder="Search by email or Account Key\.\.\."[^>]*type="search" value=""/,
  );
});

test("loaded pages stack, and the last one drops Show more", async () => {
  const html = await render({
    pages: [page(50, 120), page(50, 120, 50), page(20, 120, 100)],
    hasNextPage: false,
  });

  assert.equal(rowCount(html), 120);
  assert.match(plain(html), /Showing 120 of 120 Account Keys</);
  assert.doesNotMatch(html, /more<\/button>/);
});

test("the last partial page offers only what is left, and one key reads singular", async () => {
  assert.match(
    await render({ pages: [page(50, 70)], hasNextPage: true }),
    />Show 20 more<\/button>/,
  );
  assert.match(
    plain(await render({ pages: [page(1, 1)] })),
    /Showing 1 of 1 Account Key</,
  );
});

test("a key repeated across two pages renders once", async () => {
  // key-49 gained a device and moved down a slot between the two requests.
  const html = await render({
    pages: [page(50, 120), page(50, 120, 49)],
    hasNextPage: true,
  });

  assert.equal(rowCount(html), 99);
  assert.equal(html.split(">buyer49@example.com<").length - 1, 1);
  assert.match(plain(html), /Showing 99 of 120 Account Keys</);
});

test("Show more in flight keeps the table bright and spins its own button, not Refresh", async () => {
  const html = await render({
    pages: [page(50, 2000)],
    hasNextPage: true,
    isFetching: true,
    isFetchingNextPage: true,
  });

  assert.match(cardClass(html), /\bopacity-100\b/);
  assert.match(html, /<div aria-busy="false" aria-label="Device activations table"/);
  assert.match(
    html,
    /<button [^>]*disabled=""[^>]*type="button"><svg [^>]*animate-spin[^>]*>.*?<\/svg>Loading\.\.\.<\/button>/,
  );
  // Refresh is disabled while any fetch runs, but shows its idle icon.
  const refresh = /<button[^>]*>((?:(?!<\/button>).)*)Refresh<\/button>/.exec(html);
  assert.ok(refresh, "no Refresh button");
  assert.doesNotMatch(refresh[1], /animate-spin/);
});

test("a search or range refetch dims the rows it will replace", async () => {
  const html = await render({
    pages: [page(50, 2000)],
    hasNextPage: true,
    isFetching: true,
    isPlaceholderData: true,
  });

  assert.match(cardClass(html), /\bopacity-50\b/);
  assert.match(html, /<div aria-busy="true" aria-label="Device activations table"/);
  // The Show more button is not mid-fetch.
  assert.match(html, />Show 50 more<\/button>/);
});

test("no keys and no search: the range's empty copy, no caption", async () => {
  const html = await render({ pages: [{ devices: [], total: 0, nextCursor: null }] });

  assert.match(plain(html), /No device activations found in the last 30 days\./);
  assert.doesNotMatch(html, /Showing /);
});
