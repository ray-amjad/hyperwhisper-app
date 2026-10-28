/**
 * #1242: on the admin Devices page the first load's skeleton rows were
 * replaced by the real table in one frame. The skeleton now sits in the same
 * grid slot as the table (the #1236 /older-versions cross-fade) and fades out
 * over 400 ms while the rows fade in; under reduced motion both swap at once.
 * A `<tbody>` cannot share a grid slot, so the skeleton is its own table under
 * an invisible copy of the header.
 *
 * HOW. Renders the REAL `DevicesClient` through `renderToStaticMarkup` with
 * the tRPC client mocked (see `devices-range-aria-pressed.test.ts`), once
 * mid-first-load and once with the rows already cached, and reads the
 * skeleton and the rows. The fade-out after the load runs in effects, which
 * server markup never reaches, so its wiring is read from the source.
 *
 * WHAT IT DOES NOT PROVE: the painted frames. The PR's per-frame probe
 * samples both layers' opacity during a first load.
 */
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

import { moduleMock, moduleUrl } from "./module-mock";

let loading = true;

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

async function render(): Promise<string> {
  const { createElement } = await import("react");
  const { renderToStaticMarkup } = await import("react-dom/server");
  const { default: DevicesClient } = (await import(COMPONENT_PATH)) as {
    default: () => null;
  };

  return renderToStaticMarkup(createElement(DevicesClient));
}

const FADE = [
  "transition-[opacity,filter]",
  "duration-[400ms]",
  "motion-reduce:transition-none",
];

function classOf(html: string, marker: RegExp): string {
  const tag = new RegExp(`<[a-z]+\\b[^>]*class="([^"]*${marker.source}[^"]*)"`).exec(
    html,
  );

  assert.ok(tag, `no element with ${marker}`);

  return tag[1];
}

test("mid-first-load: the skeleton shares the table's grid slot and fades, under an invisible header", async () => {
  loading = true;
  const html = await render();
  const box = classOf(html, /\bt-skel\b(?!-)/);

  assert.match(box, /\bgrid\b/);
  assert.match(box, /\brelative\b/);

  const skeleton = classOf(html, /\bt-skel-skeleton\b/);

  assert.match(skeleton, /\[grid-area:1\/1\]/);
  assert.match(skeleton, /\bself-start\b/);
  assert.match(skeleton, /\bopacity-100\b/);
  for (const c of FADE) assert.ok(skeleton.split(" ").includes(c), c);
  assert.match(
    html,
    /<div aria-hidden="true" class="t-skel-skeleton [^"]*"><table class="w-full"><thead class="invisible"><tr[^>]*><th[^>]*>Email<\/th><th[^>]*>Account Key<\/th><th[^>]*>Devices<\/th>/,
  );
  assert.equal(html.match(/animate-pulse/g)?.length, 15, "5 skeleton rows");

  // The real table is in the same slot, its rows hidden until revealed.
  assert.match(html, /<table class="\[grid-area:1\/1\] w-full"><thead>/);
  const rows = classOf(html, /\bt-skel-content\b/);

  assert.match(rows, /\bopacity-0\b/);
  for (const c of FADE) assert.ok(rows.split(" ").includes(c), c);
});

test("a mount with the rows already cached shows no skeleton", async () => {
  loading = false;
  const html = await render();

  assert.doesNotMatch(html, /t-skel-skeleton/);
  assert.doesNotMatch(html, /animate-pulse/);
  assert.match(html, /a@example\.com/);
});

test("after the load the skeleton stays to fade out, out of the flow, and goes when its fade ends", () => {
  const source = readFileSync(new URL(COMPONENT_PATH, import.meta.url), "utf8");

  // Mounted past the load, until its own fade ends (or at once under
  // reduced motion, which runs no transition).
  assert.match(source, /useState\(!loading\)/);
  assert.match(
    source,
    /const skeletonShown = !skeletonGone && !\(revealed && reduceMotion\);/,
  );
  assert.match(source, /\{skeletonShown && \(\s*<div\s+aria-hidden/);
  // Out of the flow once the rows land, so the table alone sets the height.
  assert.match(
    source,
    /loading\s*\?\s*"\[grid-area:1\/1\] self-start"\s*:\s*"absolute inset-0 overflow-hidden"/,
  );
  // Fades on the same signal that fades the rows in.
  assert.match(
    source,
    /\$\{revealed \? "opacity-0 blur-\[2px\]" : "opacity-100 blur-\[0px\]"\}/,
  );
  assert.match(
    source,
    /revealed &&\s*event\.target === event\.currentTarget &&\s*event\.propertyName === "opacity"\s*\)\s*\{\s*setSkeletonGone\(true\);/,
  );
});
