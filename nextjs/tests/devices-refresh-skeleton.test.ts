/**
 * #2789: on the admin Devices page a Refresh after a failed first load
 * brought the skeleton back by fading it in over 400 ms, and faded the real
 * header out over the skeleton's own, instead of showing the loading state at
 * once as before #2770. The layers followed `useRevealAfterLoad(loading)`,
 * whose flag only turns false in an effect a render after the load starts, so
 * that load's first frame still carried the last load's settled classes.
 *
 * HOW. Renders the REAL `DevicesClient` through `renderToStaticMarkup` with
 * the tRPC client mocked (see `devices-skeleton-crossfade.test.ts`) and
 * `useRevealAfterLoad` mocked to return true while the query is loading:
 * exactly the first render of a Refresh after a failed first load (the
 * failed load had ended, so the flag is still true). Every layer must already
 * show its loading state, with no transition to fade into it.
 *
 * WHAT IT DOES NOT PROVE: the painted frames. The PR's per-frame probe
 * samples the skeleton's opacity in the first frame of such a Refresh.
 */
import assert from "node:assert/strict";
import test from "node:test";

import { moduleMock, moduleUrl } from "./module-mock";

let loading = true;
let revealed = true;

moduleMock.module(moduleUrl("../lib/use-reveal-after-load.ts"), {
  namedExports: { useRevealAfterLoad: () => revealed },
});

moduleMock.module(moduleUrl("../lib/trpc/client.ts"), {
  namedExports: {
    api: {
      admin: {
        devices: {
          list: {
            useQuery: () => ({
              data: loading
                ? undefined
                : {
                    devices: [
                      {
                        licenseKeyId: "k1",
                        email: "a@example.com",
                        licenseKey: "hw-0000-0000-0000",
                        deviceCount: 2,
                      },
                    ],
                  },
              isLoading: loading,
              isFetching: loading,
              isPlaceholderData: false,
              error: null,
              refetch: () => undefined,
            }),
          },
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

async function render(): Promise<string> {
  const { createElement } = await import("react");
  const { renderToStaticMarkup } = await import("react-dom/server");
  const { default: DevicesClient } = (await import(COMPONENT_PATH)) as {
    default: () => null;
  };

  return renderToStaticMarkup(createElement(DevicesClient));
}

function classOf(html: string, marker: RegExp): string {
  const tag = new RegExp(
    `<[a-z]+\\b[^>]*class="([^"]*${marker.source}[^"]*)"`,
  ).exec(html);

  assert.ok(tag, `no element with ${marker}`);

  return tag[1];
}

function realHead(html: string): string {
  const head =
    /<table class="\[grid-area:1\/1\] self-start w-full min-w-\[36rem\]"><thead class="([^"]*)">/.exec(
      html,
    );

  assert.ok(head, "the real table's header");

  return head[1];
}

test("first render of a Refresh after a failed load: the skeleton shows at full opacity at once", async () => {
  loading = true;
  revealed = true;
  const html = await render();
  const skeleton = classOf(html, /\bt-skel-skeleton\b/);

  assert.match(skeleton, /\bopacity-100\b/);
  assert.doesNotMatch(skeleton, /\bopacity-0\b/);
  assert.doesNotMatch(skeleton, /\btransition-/, "it snaps in, never fades in");

  // The real header hides at once under the skeleton's own: no 400 ms ghost.
  const head = realHead(html);

  assert.match(head, /\bopacity-0\b/);
  assert.doesNotMatch(head, /\btransition-/);

  const rows = classOf(html, /\bt-skel-content\b/);

  assert.match(rows, /\bopacity-0\b/);
  assert.doesNotMatch(rows, /\btransition-/);

  // The bars pulse from the first frame.
  assert.equal(html.match(/animate-pulse/g)?.length, 15, "5 skeleton rows");
  assert.doesNotMatch(html, /animation-play-state/);
});

test("the render that ends a load keeps the 400 ms cross-fade, starting hidden", async () => {
  // The flag is still false two frames before it flips: the rows paint
  // hidden with the fade already on, so the reveal tweens.
  loading = false;
  revealed = false;
  const html = await render();
  const rows = classOf(html, /\bt-skel-content\b/);

  assert.match(rows, /\bopacity-0\b/);
  for (const c of [
    "transition-[opacity,filter]",
    "duration-[400ms]",
    "motion-reduce:transition-none",
  ]) {
    assert.ok(rows.split(" ").includes(c), c);
  }
});
