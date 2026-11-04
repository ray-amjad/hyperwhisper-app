/**
 * #2950: pressing Enter in `components/landing/DownloadModal.tsx`'s email
 * field while a submission is in flight must not fire a second
 * `recordDownload.mutate()` (a second welcome email, then "Too many requests").
 * The Download button is disabled/loading while pending; the Enter path now
 * has the same guard, plus a synchronous one for two keydowns that land before
 * React re-renders with `isPending: true`.
 *
 * HOW. The harness of `download-modal-email-wiring.test.ts`: every HeroUI part
 * is stubbed by the file the component loads, the stubs record their props,
 * and the modal renders once through `renderToStaticMarkup`. There is no DOM to
 * type into, so `react` is re-exported with a `useState` wrapper that seeds the
 * email field (the only `useState("")`) with a typed address. The handlers the
 * stubs recorded are then called after the render, as a browser would.
 *
 * WHAT IT DOES NOT PROVE. That HeroUI's real `Input` forwards `onKeyDown` from
 * the `<input>`, or how a real React Query mutation orders its callbacks
 * (it calls the hook's `onError`/`onSuccess`, then `onSettled`; the stub below
 * replays that order by hand).
 */
import assert from "node:assert/strict";
import test, { beforeEach } from "node:test";
import { createRequire } from "node:module";
import { pathToFileURL } from "node:url";
import type { ReactElement, ReactNode } from "react";

import { moduleMock, moduleUrl } from "./module-mock";

const req = createRequire(import.meta.url);

function resolvedUrl(specifier: string): string {
  return pathToFileURL(req.resolve(specifier)).href;
}

const realReact = req("react") as typeof import("react");
// Taken before the mock below, which can patch the shared CJS exports object.
const realUseState = realReact.useState;
const TYPED_EMAIL = "bh-nx-download-1@example.test";

moduleMock.module(resolvedUrl("react"), {
  defaultExport: realReact,
  namedExports: {
    ...realReact,
    useState: (init: unknown) => {
      if (init === "") {
        const [, set] = realUseState(init);

        return [TYPED_EMAIL, set];
      }

      return realUseState(init);
    },
  },
});

interface InputProps {
  onKeyDown?: (e: { key: string }) => void;
}
interface ButtonProps {
  isLoading?: boolean;
  onClick?: () => void;
}
interface MutationOptions {
  onError: (err: {
    message: string;
    data: { code: string; httpStatus: number; path: string } | null;
  }) => void;
  onSettled?: () => void;
}

const inputs: InputProps[] = [];
const buttons: ButtonProps[] = [];
let mutateCalls = 0;
/** What the mutation reports on the next render. */
let pending = false;
let lastOptions: MutationOptions | undefined;

function Passthrough({ children }: { children?: ReactNode }): ReactElement {
  return realReact.createElement("div", null, children);
}

moduleMock.module(resolvedUrl("@heroui/input"), {
  namedExports: {
    Input: (props: InputProps) => {
      inputs.push(props);

      return null;
    },
  },
});
moduleMock.module(resolvedUrl("@heroui/modal"), {
  namedExports: {
    Modal: Passthrough,
    ModalContent: Passthrough,
    ModalHeader: Passthrough,
    ModalBody: Passthrough,
    ModalFooter: Passthrough,
  },
});
moduleMock.module(resolvedUrl("@heroui/button"), {
  namedExports: {
    Button: (props: ButtonProps) => {
      buttons.push(props);

      return null;
    },
  },
});
moduleMock.module(resolvedUrl("next/image"), { defaultExport: () => null });
moduleMock.module("next-intl", {
  namedExports: {
    useTranslations:
      (namespace: string) =>
      (key: string): string =>
        `${namespace}.${key}`,
  },
});
moduleMock.module(moduleUrl("../src/i18n/navigation.ts"), {
  namedExports: { useRouter: () => ({ push: () => undefined }) },
});
moduleMock.module(moduleUrl("../contexts/DownloadModalContext.tsx"), {
  namedExports: {
    useDownloadModal: () => ({
      isOpen: true,
      platform: null,
      closeModal: () => undefined,
    }),
  },
});
moduleMock.module(moduleUrl("../lib/trpc/client.ts"), {
  namedExports: {
    api: {
      download: {
        recordDownload: {
          useMutation: (options: MutationOptions) => {
            lastOptions = options;

            return {
              mutate: () => {
                mutateCalls += 1;
              },
              isPending: pending,
            };
          },
        },
      },
    },
  },
});

const COMPONENT_PATH = "../components/landing/DownloadModal.tsx";

/** Renders the modal once and returns the handlers its stubs recorded. */
async function render(): Promise<{
  enter: () => void;
  download: ButtonProps;
  options: MutationOptions;
}> {
  const { default: DownloadModal } = (await import(COMPONENT_PATH)) as {
    default: () => ReactElement;
  };

  const { renderToStaticMarkup } = await import("react-dom/server");

  renderToStaticMarkup(realReact.createElement(DownloadModal));

  const input = inputs.at(-1);

  assert.ok(input?.onKeyDown, "the Input mock never bound");
  const download = buttons[0];

  assert.ok(download, "the Button mock never bound");
  assert.ok(lastOptions, "the tRPC mock never bound");

  return {
    enter: () => input.onKeyDown?.({ key: "Enter" }),
    download,
    options: lastOptions,
  };
}

beforeEach(() => {
  inputs.length = 0;
  buttons.length = 0;
  mutateCalls = 0;
  pending = false;
  lastOptions = undefined;
});

test("Enter while recordDownload is pending does not submit again", async () => {
  pending = true;
  const { enter, download } = await render();

  assert.equal(
    download.isLoading,
    true,
    "precondition: the Download button shows the pending state",
  );

  enter();

  assert.equal(
    mutateCalls,
    0,
    `Enter while pending fired recordDownload.mutate ${mutateCalls} time(s)`,
  );
});

test("two Enters before React re-renders submit once", async () => {
  // Both keydowns reach the same render, where isPending is still false.
  const { enter } = await render();

  enter();
  enter();

  assert.equal(mutateCalls, 1, `two Enters fired ${mutateCalls} mutate(s)`);
});

test("a Download click right after Enter, before the re-render, does not submit again", async () => {
  const { enter, download } = await render();

  enter();
  download.onClick?.();

  assert.equal(mutateCalls, 1, `Enter + click fired ${mutateCalls} mutate(s)`);
});

test("Enter after a failed request submits again", async () => {
  const { enter, options } = await render();

  enter();
  assert.equal(mutateCalls, 1);

  // React Query: the hook's onError, then its onSettled.
  options.onError({
    message: "Something went wrong",
    data: {
      code: "INTERNAL_SERVER_ERROR",
      httpStatus: 500,
      path: "download.recordDownload",
    },
  });
  options.onSettled?.();

  enter();

  assert.equal(
    mutateCalls,
    2,
    `Enter after a failed request fired ${mutateCalls} mutate(s) in total`,
  );
});

test("a key other than Enter does not submit", async () => {
  await render();

  inputs.at(-1)?.onKeyDown?.({ key: "a" });

  assert.equal(mutateCalls, 0);
});
