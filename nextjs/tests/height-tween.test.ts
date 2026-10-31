/**
 * #2757: the admin Devices table card stepped to its new height in one frame
 * on a range switch, the defect #916 fixed on Customers. The #916
 * `HeightTween` moved out of CustomersClient to `components/ui/HeightTween`,
 * and both pages wrap their table card in it with `trigger={data}`.
 *
 * HOW. The tween's layout-effect body is `tweenOnTriggerChange`, driven here
 * with stand-in elements and a fake `window` (rAF queue, timers,
 * matchMedia), as tests/flip-rows.test.ts does: it pins the old height, starts
 * the tween two frames later, unpins on the box's own `transitionend`, skips
 * the first value after `undefined`, and does nothing under reduced motion.
 * The wiring is read from the real `DevicesClient` server markup (tRPC
 * mocked) and from the two pages' source.
 *
 * WHAT IT DOES NOT PROVE: the frames a browser paints. The PR's per-frame
 * probe measures the card's height during a range switch.
 */
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

import { moduleMock, moduleUrl } from "./module-mock";

moduleMock.module(moduleUrl("../lib/trpc/client.ts"), {
  namedExports: {
    api: {
      admin: {
        devices: {
          list: {
            useQuery: () => ({
              data: { devices: [] },
              isLoading: false,
              isFetching: false,
              isPlaceholderData: false,
              error: null,
              refetch: () => undefined,
            }),
          },
        },
      },
    },
  },
});

type Tween = typeof import("../components/ui/HeightTween");

async function tweenModule(): Promise<Tween> {
  return (await import("../components/ui/HeightTween")) as Tween;
}

/** A fake window: frames and timers run only when the test says so. */
function fakeWindow(reducedMotion = false) {
  let frames: Array<() => void> = [];
  const timers = new Map<number, () => void>();
  let nextId = 1;
  const win = {
    matchMedia: (query: string) => ({
      matches: reducedMotion && query.includes("reduce"),
    }),
    requestAnimationFrame: (callback: () => void) => {
      frames.push(callback);

      return nextId++;
    },
    cancelAnimationFrame: () => undefined,
    setTimeout: (callback: () => void) => {
      const id = nextId++;

      timers.set(id, callback);

      return id;
    },
    clearTimeout: (id: number) => timers.delete(id),
  };

  (globalThis as { window?: unknown }).window = win;

  return {
    /** Runs the frame callbacks queued so far (one browser frame). */
    frame() {
      const queued = frames;

      frames = [];
      for (const callback of queued) callback();
    },
    timers,
  };
}

/** A box whose layout height is `height`, holding content `inner` tall. */
function fakeBox(height: number, innerHeight: number) {
  const listeners = new Set<(event: TransitionEvent) => void>();
  const element = {
    style: { height: "", overflow: "" } as Record<string, string>,
    offsetHeight: height,
    clientHeight: height - 2, // a 1 px border top and bottom
    addEventListener: (_: string, l: (event: TransitionEvent) => void) =>
      listeners.add(l),
    removeEventListener: (_: string, l: (event: TransitionEvent) => void) =>
      listeners.delete(l),
  };
  const inner = { offsetHeight: innerHeight };

  return {
    element,
    inner,
    end(propertyName: string, target: unknown = element) {
      for (const l of Array.from(listeners)) {
        l({ target, propertyName } as unknown as TransitionEvent);
      }
    },
    listeners,
  };
}

test("a change of trigger pins the old height, then tweens to the new one two frames later", async () => {
  const { tweenOnTriggerChange, HEIGHT_TWEEN_MS } = await tweenModule();
  const win = fakeWindow();
  // The rows shrank: the box now lays out at 102 px, its content at 100.
  const box = fakeBox(102, 100);
  const state = { settledHeight: 802, tween: null };

  tweenOnTriggerChange(
    state,
    box.element as unknown as HTMLElement,
    box.inner as unknown as HTMLElement,
    { devices: ["old"] },
  );

  assert.equal(box.element.style.height, "802px", "pinned at the old height");
  assert.equal(box.element.style.overflow, "clip");
  assert.ok(state.tween, "a tween is in flight");

  win.frame();
  assert.equal(
    box.element.style.height,
    "802px",
    "the first frame draws the new content at the old height",
  );

  win.frame();
  assert.equal(box.element.style.height, "102px", "the tween target");
  assert.equal(win.timers.size, 1, "a safety unpin is armed");
  assert.ok(HEIGHT_TWEEN_MS >= 200 && HEIGHT_TWEEN_MS <= 400);

  // A child's transition bubbling up does not end it; the box's own does.
  box.end("height", { child: true });
  box.end("opacity");
  assert.equal(box.element.style.height, "102px");
  box.end("height");
  assert.equal(box.element.style.height, "", "unpinned to height: auto");
  assert.equal(box.element.style.overflow, "");
  assert.equal(state.tween, null);
  assert.equal(state.settledHeight, 102);
  assert.equal(box.listeners.size, 0);
});

test("the first value after undefined lands at once (the first load does not tween)", async () => {
  const { tweenOnTriggerChange } = await tweenModule();

  fakeWindow();
  const box = fakeBox(4139, 4137);
  const state = { settledHeight: 375, tween: null };

  tweenOnTriggerChange(
    state,
    box.element as unknown as HTMLElement,
    box.inner as unknown as HTMLElement,
    undefined,
  );

  assert.equal(box.element.style.height, "");
  assert.equal(state.tween, null);
});

test("under reduced motion the box takes its new height at once", async () => {
  const { tweenOnTriggerChange } = await tweenModule();

  fakeWindow(true);
  const box = fakeBox(102, 100);
  const state = { settledHeight: 802, tween: null };

  tweenOnTriggerChange(
    state,
    box.element as unknown as HTMLElement,
    box.inner as unknown as HTMLElement,
    { devices: ["old"] },
  );

  assert.equal(box.element.style.height, "");
  assert.equal(state.tween, null);
});

test("no settled height yet, or no height change: nothing is pinned", async () => {
  const { tweenOnTriggerChange } = await tweenModule();

  fakeWindow();
  for (const settledHeight of [null, 102]) {
    const box = fakeBox(102, 100);
    const state = { settledHeight, tween: null };

    tweenOnTriggerChange(
      state,
      box.element as unknown as HTMLElement,
      box.inner as unknown as HTMLElement,
      { devices: [] },
    );
    assert.equal(box.element.style.height, "", String(settledHeight));
    assert.equal(state.tween, null);
  }
});

test("a second change mid-tween starts from where the box is drawn now", async () => {
  const { tweenOnTriggerChange } = await tweenModule();
  const win = fakeWindow();
  const box = fakeBox(102, 100);
  const state = { settledHeight: 802, tween: null };
  const asElement = box.element as unknown as HTMLElement;
  const asInner = box.inner as unknown as HTMLElement;

  tweenOnTriggerChange(state, asElement, asInner, { a: 1 });
  win.frame();
  win.frame();
  // Mid-tween the browser draws it at 500 px; the next rows want 302.
  (globalThis as { getComputedStyle?: unknown }).getComputedStyle = () => ({
    height: "500px",
  });
  box.element.offsetHeight = 302;
  box.element.clientHeight = 300;
  box.inner.offsetHeight = 300;
  tweenOnTriggerChange(state, asElement, asInner, { a: 2 });

  assert.equal(box.element.style.height, "500px");
  win.frame();
  win.frame();
  assert.equal(box.element.style.height, "302px");
});

const DEVICES = readFileSync(
  new URL(
    "../app/[locale]/user/(authenticated)/devices/DevicesClient.tsx",
    import.meta.url,
  ),
  "utf8",
);
const CUSTOMERS = readFileSync(
  new URL(
    "../app/[locale]/user/(authenticated)/customers/CustomersClient.tsx",
    import.meta.url,
  ),
  "utf8",
);

test("Devices and Customers both use the shared HeightTween, keyed on the query data", () => {
  for (const [name, source] of [
    ["Devices", DEVICES],
    ["Customers", CUSTOMERS],
  ] as const) {
    assert.match(
      source,
      /import \{ HeightTween \} from "@\/components\/ui\/HeightTween";/,
      name,
    );
    assert.doesNotMatch(source, /function HeightTween\b/, `${name} keeps no copy`);
    assert.match(
      source,
      /<HeightTween\s+className=\{`\$\{CARD\} [^`]*`\}\s+\/\/[^\n]*\n\s+transition="opacity 150ms [^"]+"\s+trigger=\{data\}\s*>/,
      `${name}: the table card tweens on data and keeps its dim`,
    );
  }
  // The per-key device search list (DeviceRow) tweens on its first page, so
  // a search swap tweens and a "Show more" append does not (below).
  assert.match(DEVICES, /const firstPage = data\?\.pages\[0\];/);
  assert.match(DEVICES, /<HeightTween trigger=\{firstPage\}>\s*\{isLoading \?/);
});

test("react-query keeps the first page's identity on an append and changes it on a new search", async () => {
  const { QueryClient, InfiniteQueryObserver } = await import(
    "@tanstack/react-query"
  );
  // The app's defaults (lib/trpc/query-client): structural sharing is on.
  const client = new QueryClient();
  type Page = { devices: string[]; nextCursor: number | null };
  const observe = (search: string) =>
    new InfiniteQueryObserver<Page>(client, {
      queryKey: ["forLicense", search],
      queryFn: ({ pageParam }) =>
        Promise.resolve({
          devices: [`${search}-${String(pageParam)}`],
          nextCursor: (pageParam as number) < 2 ? (pageParam as number) + 1 : null,
        }),
      initialPageParam: 0,
      getNextPageParam: (last) => last.nextCursor ?? undefined,
    });

  const all = observe("");
  const unsubscribe = all.subscribe(() => undefined);
  const first = (await all.refetch()).data?.pages[0];
  const appended = await all.fetchNextPage();

  assert.equal(appended.data?.pages.length, 2);
  assert.equal(appended.data?.pages[0], first, "Show more keeps the first page");
  // A refetch that brings the same rows keeps it too.
  assert.equal((await all.refetch()).data?.pages[0], first);

  const narrowed = observe("mac");
  const unsubscribeNarrowed = narrowed.subscribe(() => undefined);

  assert.notEqual(
    (await narrowed.refetch()).data?.pages[0],
    first,
    "a new search is a new first page",
  );
  unsubscribe();
  unsubscribeNarrowed();
  client.clear();
});

test("the Devices card renders with the opacity dim and the height tween, never `all`", async () => {
  const { createElement } = await import("react");
  const { renderToStaticMarkup } = await import("react-dom/server");
  const COMPONENT_PATH =
    "../app/[locale]/user/(authenticated)/devices/DevicesClient.tsx";
  const { default: DevicesClient } = (await import(COMPONENT_PATH)) as {
    default: () => null;
  };
  const html = renderToStaticMarkup(createElement(DevicesClient));
  const card = /<div class="rounded-\[20px\] [^"]*" style="([^"]*)"/.exec(html);

  assert.ok(card, "no Devices table card");
  assert.equal(
    card[1],
    "transition:opacity 150ms cubic-bezier(0.4, 0, 0.2, 1), height 300ms var(--motion-ease-smooth-out)",
  );
  assert.match(html, /<div aria-busy="false" class="overflow-x-auto">/);
});
