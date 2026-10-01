/**
 * The #968 wiring seam for `components/landing/DownloadModal.tsx`: the email
 * field has a name of its own, and its `aria-describedby` points at the line
 * that is actually on screen below it.
 *
 * HeroUI names a label-less `Input` by its placeholder. The modal now passes an
 * explicit `aria-label` (the issue allows reusing `downloadModal.emailPlaceholder`
 * for it, so no locale file changes). The line under the field is EITHER the
 * description OR the error alert, never both, so `aria-describedby` must follow
 * whichever is rendered or it dangles.
 *
 * HOW. Every HeroUI part is stubbed by the file the component loads (see
 * `credits-purchase-email-wiring.test.ts` for why a bare-name mock binds
 * nothing). The Modal stubs render their children, so the two `<p>` lines reach
 * the static markup; the `Input` stub records its props. The error state is
 * reached through the component's own `onError`: the tRPC `useMutation` stub
 * calls it once during the modal's render, a render-phase update React applies
 * inside one `renderToStaticMarkup`.
 *
 * WHAT IT DOES NOT PROVE. That HeroUI puts these props on the real `<input>`
 * (read in its source: a caller's `aria-label` replaces the placeholder
 * fallback, and a caller's `aria-describedby` is passed through as is), or what
 * a browser's accessibility tree computes. That is the verify round's job.
 */
import assert from "node:assert/strict";
import test, { beforeEach, mock } from "node:test";
import { pathToFileURL } from "node:url";
import { createElement, type ReactElement, type ReactNode } from "react";
import { renderToStaticMarkup } from "react-dom/server";

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

function resolvedUrl(specifier: string): string {
  return pathToFileURL(require.resolve(specifier)).href;
}

function moduleUrl(relative: string): string {
  return new URL(relative, import.meta.url).href;
}

interface InputProps {
  type?: string;
  placeholder?: string;
  "aria-label"?: string;
  "aria-describedby"?: string;
}

const inputs: InputProps[] = [];
let mutationHooks = 0;
/** The error the next render's mutation reports, once. */
let scenario: { error?: string } = {};
let fired = false;

function StubInput(props: InputProps): null {
  inputs.push(props);

  return null;
}

function Passthrough({ children }: { children?: ReactNode }): ReactElement {
  return createElement("div", null, children);
}

moduleMock.module(resolvedUrl("@heroui/input"), {
  namedExports: { Input: StubInput },
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
  namedExports: { Button: () => null },
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
    useDownloadModal: () => ({ isOpen: true, closeModal: () => undefined }),
  },
});
moduleMock.module(moduleUrl("../lib/trpc/client.ts"), {
  namedExports: {
    api: {
      download: {
        recordDownload: {
          useMutation: (options: {
            onError: (err: { message: string }) => void;
          }) => {
            mutationHooks += 1;
            if (scenario.error !== undefined && !fired) {
              fired = true;
              options.onError({ message: scenario.error });
            }

            return { mutate: () => undefined, isPending: false };
          },
        },
      },
    },
  },
});

const COMPONENT_PATH = "../components/landing/DownloadModal.tsx";

async function render(
  next: typeof scenario = {},
): Promise<{ input: InputProps; html: string }> {
  scenario = next;
  const { default: DownloadModal } = (await import(COMPONENT_PATH)) as {
    default: () => ReactElement;
  };
  const html = renderToStaticMarkup(createElement(DownloadModal));

  assert.ok(mutationHooks > 0, "the tRPC mock never bound");
  const input = inputs.at(-1);

  assert.ok(input, "the Input mock never bound");

  return { input, html };
}

/** The element in `html` whose id is `id`, which must be there exactly once. */
function elementById(html: string, id: string): string {
  const escaped = id.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  const matches = html.match(
    new RegExp(`<p[^>]*\\sid="${escaped}"[^>]*>([^<]*)</p>`, "g"),
  );

  assert.equal(matches?.length, 1, `expected one <p id="${id}"> in ${html}`);

  return matches[0];
}

beforeEach(() => {
  inputs.length = 0;
  mutationHooks = 0;
  scenario = {};
  fired = false;
});

test("the modal email field has a name that is not only the placeholder", async () => {
  const { input } = await render();

  assert.equal(input.type, "email");
  assert.equal(input["aria-label"], "downloadModal.emailPlaceholder");
});

test("with no error the field is described by the description line", async () => {
  const { input, html } = await render();
  const describedBy = input["aria-describedby"];

  assert.ok(describedBy, "no aria-describedby");
  assert.match(elementById(html, describedBy), /downloadModal\.description/);
  assert.doesNotMatch(html, /role="alert"/);
});

test("with an error the field is described by the error alert", async () => {
  const { input, html } = await render({ error: "Rate limited" });
  const describedBy = input["aria-describedby"];

  assert.ok(describedBy, "no aria-describedby");
  const target = elementById(html, describedBy);

  assert.match(target, /role="alert"/);
  assert.match(target, /Rate limited/);
  assert.doesNotMatch(html, /downloadModal\.description/);
});
