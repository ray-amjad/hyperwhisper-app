/**
 * The Account Keys email step-up flow, shared by the rotate, delete and issue
 * controls through `use-key-action.ts` (#2986).
 *
 * `keyActionHandlers` is pure: every state setter and collaborator is passed
 * in, so each transition is driven here with fakes. The last tests render the
 * real `useKeyAction` once (with `next-intl`, the tRPC client and the auth
 * client mocked) to pin what the hook wires in: the magic link goes back to
 * this page, and a refusal that asks for it refetches `licensesWithCredits`.
 */
import assert from "node:assert/strict";
import { describe, test } from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";

import { moduleMock, moduleUrl } from "./module-mock";

import type {
  KeyActionDeps,
  KeyActionLinkSentKey,
} from "../components/customer/dashboard/use-key-action";
import type { RotatePhase } from "../components/customer/dashboard/RotateAccountKeyView";

const magicLinkCalls: { email: string; callbackURL: string }[] = [];
let magicLinkError: unknown = null;
let invalidations = 0;

moduleMock.module("next-intl", {
  namedExports: {
    useTranslations:
      () => (key: string, values?: Record<string, string | number>) =>
        values ? `${key}:${JSON.stringify(values)}` : key,
  },
});
moduleMock.module(moduleUrl("../src/lib/auth-client.ts"), {
  namedExports: {
    authClient: {
      signIn: {
        magicLink: async (input: { email: string; callbackURL: string }) => {
          magicLinkCalls.push(input);

          return { error: magicLinkError };
        },
      },
    },
  },
});
moduleMock.module(moduleUrl("../lib/trpc/client.ts"), {
  namedExports: {
    api: {
      useUtils: () => ({
        customer: {
          licensesWithCredits: {
            invalidate: async () => {
              invalidations += 1;
            },
          },
        },
      }),
    },
  },
});

const HOOK_PATH = "../components/customer/dashboard/use-key-action";

type HookModule =
  typeof import("../components/customer/dashboard/use-key-action");

async function loadHook(): Promise<HookModule> {
  return (await import(HOOK_PATH)) as HookModule;
}

/** Fakes for every collaborator, with a log of what each one was told. */
function rig(
  overrides: Partial<{
    emailProven: boolean;
    linkSentKey: KeyActionLinkSentKey;
    linkError: unknown;
  }> = {},
) {
  const log: string[] = [];
  const sent: string[] = [];
  const deps: KeyActionDeps = {
    email: "user@example.test",
    emailProven: overrides.emailProven ?? true,
    linkSentKey: overrides.linkSentKey ?? "linkSent",
    onNotice: (text) => log.push(`notice=${text}`),
    setPhase: (phase: RotatePhase) => log.push(`phase=${phase}`),
    setError: (error) => log.push(`error=${error}`),
    translate: (key, values) =>
      values ? `t(${key},${JSON.stringify(values)})` : `t(${key})`,
    refetchKeys: () => log.push("refetch"),
    sendMagicLink: async (email) => {
      sent.push(email);

      return { error: overrides.linkError ?? null };
    },
  };

  return { deps, log, sent };
}

describe("keyActionHandlers: the view callbacks", () => {
  test("Start clears the notice and the error, then confirms an email session", async () => {
    const { keyActionHandlers } = await loadHook();
    const { deps, log } = rig({ emailProven: true });

    keyActionHandlers(deps)
      .viewHandlers(() => {}, false)
      .onStart();
    assert.deepEqual(log, ["notice=null", "error=null", "phase=confirming"]);
  });

  test("Start opens the email step-up for a session opened with a key", async () => {
    const { keyActionHandlers } = await loadHook();
    const { deps, log } = rig({ emailProven: false });

    keyActionHandlers(deps)
      .viewHandlers(() => {}, false)
      .onStart();
    assert.deepEqual(log, ["notice=null", "error=null", "phase=verify"]);
  });

  test("Cancel clears the error and goes back to idle, notice untouched", async () => {
    const { keyActionHandlers } = await loadHook();
    const { deps, log } = rig();

    keyActionHandlers(deps)
      .viewHandlers(() => {}, false)
      .onCancel();
    assert.deepEqual(log, ["error=null", "phase=idle"]);
  });

  test("Confirm clears the error, shows the busy phase, then runs the mutation", async () => {
    const { keyActionHandlers } = await loadHook();
    const { deps, log } = rig();

    keyActionHandlers(deps)
      .viewHandlers(() => log.push("mutate"), false)
      .onConfirm();
    assert.deepEqual(log, ["error=null", "phase=rotating", "mutate"]);
  });

  test("Confirm while the mutation is pending does nothing (no double submit)", async () => {
    const { keyActionHandlers } = await loadHook();
    const { deps, log } = rig();

    keyActionHandlers(deps)
      .viewHandlers(() => log.push("mutate"), true)
      .onConfirm();
    assert.deepEqual(log, []);
  });
});

describe("keyActionHandlers: the sign-in link", () => {
  for (const key of ["linkSent", "deleteLinkSent", "issueLinkSent"] as const) {
    test(`a sent link goes back to idle and posts ${key}`, async () => {
      const { keyActionHandlers } = await loadHook();
      const { deps, log, sent } = rig({ linkSentKey: key });

      await keyActionHandlers(deps).sendLink();
      assert.deepEqual(sent, ["user@example.test"]);
      assert.deepEqual(log, [
        "error=null",
        "phase=sending",
        "phase=idle",
        `notice=t(${key})`,
      ]);
    });
  }

  test("a failed link stays on the step-up with linkFailed and posts no notice", async () => {
    const { keyActionHandlers } = await loadHook();
    const { deps, log } = rig({ linkError: { message: "nope" } });

    await keyActionHandlers(deps).sendLink();
    assert.deepEqual(log, [
      "error=null",
      "phase=sending",
      "phase=verify",
      "error=t(linkFailed)",
    ]);
  });
});

describe("keyActionHandlers: a refusal", () => {
  test("refetches first when asked, then sets the phase and the translated message", async () => {
    const { keyActionHandlers } = await loadHook();
    const { deps, log } = rig();

    keyActionHandlers(deps).onRefusal({
      phase: "idle",
      message: "errorStale",
      refetch: true,
    });
    assert.deepEqual(log, ["refetch", "phase=idle", "error=t(errorStale)"]);
  });

  test("no message clears the error; no refetch leaves the list alone", async () => {
    const { keyActionHandlers } = await loadHook();
    const { deps, log } = rig();

    keyActionHandlers(deps).onRefusal({
      phase: "verify",
      message: null,
      refetch: false,
    });
    assert.deepEqual(log, ["phase=verify", "error=null"]);
  });

  test("passes the control's translation arguments (Issue's { max })", async () => {
    const { keyActionHandlers } = await loadHook();
    const { deps, log } = rig();

    keyActionHandlers(deps).onRefusal(
      { phase: "idle", message: "issueCap", refetch: true },
      { max: 5 },
    );
    assert.deepEqual(log, [
      "refetch",
      "phase=idle",
      'error=t(issueCap,{"max":5})',
    ]);
  });
});

describe("useKeyAction: what the hook wires in", () => {
  /** Renders the real hook once and hands back what it returned. */
  async function renderHook(notices: (string | null)[]) {
    const { useKeyAction } = await loadHook();
    let result: ReturnType<typeof useKeyAction> | undefined;

    function Probe() {
      result = useKeyAction({
        email: "user@example.test",
        emailProven: false,
        onNotice: (text) => notices.push(text),
        linkSentKey: "deleteLinkSent",
      });

      return null;
    }

    renderToStaticMarkup(createElement(Probe));
    assert.ok(result, "the probe rendered");

    return result;
  }

  test("starts idle with no error", async () => {
    const result = await renderHook([]);

    assert.equal(result.phase, "idle");
    assert.equal(result.error, null);
  });

  test("the magic link goes to the account address and back to this page", async () => {
    const globals = globalThis as { window?: unknown };
    const hadWindow = "window" in globals;
    const previous = globals.window;

    globals.window = { location: { pathname: "/en/user/dashboard" } };
    magicLinkCalls.length = 0;
    magicLinkError = null;
    try {
      const notices: (string | null)[] = [];
      const result = await renderHook(notices);

      result.viewHandlers(() => {}, false).onSendLink();
      // onSendLink does not return the promise; let the mocked call settle.
      await new Promise((resolve) => setImmediate(resolve));
      assert.deepEqual(magicLinkCalls, [
        { email: "user@example.test", callbackURL: "/en/user/dashboard" },
      ]);
      assert.deepEqual(notices, ["deleteLinkSent"]);
    } finally {
      if (hadWindow) globals.window = previous;
      else delete globals.window;
    }
  });

  test("a refusal that asks for it invalidates licensesWithCredits", async () => {
    invalidations = 0;
    const result = await renderHook([]);

    result.onRefusal({
      phase: "closed",
      message: "errorUnavailable",
      refetch: true,
    });
    assert.equal(invalidations, 1);
    result.onRefusal({
      phase: "idle",
      message: "errorRateLimited",
      refetch: false,
    });
    assert.equal(invalidations, 1);
  });
});
