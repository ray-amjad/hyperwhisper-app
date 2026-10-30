/**
 * The pieces of issuing and deleting Account Keys that the end-to-end suite
 * (`account-key-manage.test.ts`) does not open up:
 *
 * - `deleteRefusal` / `issueRefusal`: what a refused call leaves on screen;
 * - `RotateAccountKeyView` as the delete and issue controls use it: its own
 *   ids, the red tone, and a disabled button with its reason (the last live
 *   key, the cap);
 * - the "key added" and "key deleted" emails: a prefix only, the name escaped.
 */
import assert from "node:assert/strict";
import { describe, test } from "node:test";
import {
  Children,
  isValidElement,
  type ReactElement,
  type ReactNode,
} from "react";
import { renderToStaticMarkup } from "react-dom/server";

import type { RotateAccountKeyViewProps } from "../components/customer/dashboard/RotateAccountKeyView";

process.env.SKIP_ENV_VALIDATION = "1";

// Built from parts so no credential-shaped literal sits in the source.
const KEY = ["HW", "MMMM", "NNNN", "PPPP", "WWWW"].join("-");

// A variable specifier: a literal `.tsx` one is TS5097 under this tsconfig.
const VIEW_PATH = "../components/customer/dashboard/RotateAccountKeyView.tsx";

type Refusal = (code: string | undefined) => {
  phase: string;
  message: string | null;
  refetch: boolean;
};

interface ViewModule {
  default: (props: RotateAccountKeyViewProps) => ReactElement;
  deleteRefusal: Refusal;
  issueRefusal: Refusal;
}

async function load(): Promise<ViewModule> {
  return (await import(VIEW_PATH)) as ViewModule;
}

describe("deleteRefusal: what a refused delete leaves on screen", () => {
  test("CONFLICT: the key changed, back to idle and refetch", async () => {
    const { deleteRefusal } = await load();
    assert.deepEqual(deleteRefusal("CONFLICT"), {
      phase: "idle",
      message: "deleteErrorStale",
      refetch: true,
    });
  });

  test("NOT_FOUND: the key is gone, the control closes and refetches", async () => {
    const { deleteRefusal } = await load();
    assert.deepEqual(deleteRefusal("NOT_FOUND"), {
      phase: "closed",
      message: "deleteErrorUnavailable",
      refetch: true,
    });
  });

  test("PRECONDITION_FAILED: the last live key, idle with the reason and refetch", async () => {
    const { deleteRefusal } = await load();
    assert.deepEqual(deleteRefusal("PRECONDITION_FAILED"), {
      phase: "idle",
      message: "deleteLastKey",
      refetch: true,
    });
  });

  test("FORBIDDEN opens the email step-up; a rate limit idles; other faults keep the retry", async () => {
    const { deleteRefusal } = await load();
    assert.equal(deleteRefusal("FORBIDDEN").phase, "verify");
    assert.deepEqual(deleteRefusal("TOO_MANY_REQUESTS"), {
      phase: "idle",
      message: "manageErrorRateLimited",
      refetch: false,
    });
    assert.deepEqual(deleteRefusal("INTERNAL_SERVER_ERROR"), {
      phase: "confirming",
      message: "deleteErrorGeneric",
      refetch: false,
    });
    assert.equal(deleteRefusal(undefined).message, "deleteErrorGeneric");
  });
});

describe("issueRefusal: what a refused issue leaves on screen", () => {
  test("CONFLICT: the cap, idle with the reason and refetch", async () => {
    const { issueRefusal } = await load();
    assert.deepEqual(issueRefusal("CONFLICT"), {
      phase: "idle",
      message: "issueCap",
      refetch: true,
    });
  });

  test("PRECONDITION_FAILED: no live key any more, idle and refetch", async () => {
    const { issueRefusal } = await load();
    assert.deepEqual(issueRefusal("PRECONDITION_FAILED"), {
      phase: "idle",
      message: "issueErrorUnavailable",
      refetch: true,
    });
  });

  test("FORBIDDEN opens the email step-up; a rate limit idles; other faults keep the retry", async () => {
    const { issueRefusal } = await load();
    assert.equal(issueRefusal("FORBIDDEN").phase, "verify");
    assert.deepEqual(issueRefusal("TOO_MANY_REQUESTS"), {
      phase: "idle",
      message: "manageErrorRateLimited",
      refetch: false,
    });
    assert.deepEqual(issueRefusal("INTERNAL_SERVER_ERROR"), {
      phase: "confirming",
      message: "issueErrorGeneric",
      refetch: false,
    });
    assert.equal(issueRefusal(undefined).message, "issueErrorGeneric");
  });
});

// ---------------------------------------------------------------------------
// The view as the delete and issue controls use it
// ---------------------------------------------------------------------------

const LABELS = {
  rotate: "Delete key",
  confirmTitle: "Delete this Account Key?",
  confirmBody: "Devices using this key will stop working.",
  confirm: "Delete key",
  cancel: "Cancel",
  rotating: "Deleting…",
  verifyTitle: "Confirm by email",
  verifyBody: "A key sign-in cannot delete a key.",
  sendLink: "Email me a sign-in link",
  sendingLink: "Sending…",
};

type ButtonProps = {
  onClick?: () => void;
  disabled?: boolean;
  children?: ReactNode;
};

function props(
  overrides: Partial<RotateAccountKeyViewProps> = {},
  clicks: string[] = [],
): RotateAccountKeyViewProps {
  return {
    licenseId: "row_1",
    phase: "idle",
    labels: LABELS,
    notice: null,
    error: null,
    onStart: () => clicks.push("start"),
    onConfirm: () => clicks.push("confirm"),
    onSendLink: () => clicks.push("sendLink"),
    onCancel: () => clicks.push("cancel"),
    idPrefix: "delete-key",
    tone: "danger",
    ...overrides,
  };
}

function buttons(node: ReactNode): ReactElement<ButtonProps>[] {
  const found: ReactElement<ButtonProps>[] = [];
  const walk = (n: ReactNode) => {
    Children.forEach(n, (child) => {
      if (!isValidElement(child)) return;
      const el = child as ReactElement<ButtonProps>;

      if (el.type === "button") found.push(el);
      walk(el.props.children);
    });
  };

  walk(node);

  return found;
}

describe("RotateAccountKeyView for delete and issue", () => {
  test("disabledReason: one disabled button described by the reason, no handler reachable", async () => {
    const { default: View } = await load();
    const clicks: string[] = [];
    const tree = View(
      props({ disabledReason: "Your only key can't be deleted." }, clicks),
    );
    const all = buttons(tree);

    assert.equal(all.length, 1);
    assert.equal(all[0].props.disabled, true);
    const html = renderToStaticMarkup(tree);

    assert.match(html, /aria-describedby="delete-key-row_1-reason"/);
    assert.match(
      html,
      /id="delete-key-row_1-reason"[^>]*>Your only key can&#x27;t be deleted\./,
    );
    assert.deepEqual(clicks, []);
  });

  test("a refusal that says something else shows beside the reason (CONFLICT, rate limit)", async () => {
    const { default: View } = await load();
    const html = renderToStaticMarkup(
      View(
        props({
          disabledReason: "Your only key can't be deleted.",
          error: "This key has changed since the page loaded.",
        }),
      ),
    );

    assert.match(html, /role="alert"[^>]*>This key has changed/);
    assert.ok(html.includes("Your only key can&#x27;t be deleted."));
  });

  test("a refusal that repeats the reason word for word is shown once", async () => {
    const { default: View } = await load();
    const reason = "Your only key can't be deleted.";
    const html = renderToStaticMarkup(
      View(props({ disabledReason: reason, error: reason })),
    );

    assert.equal(html.split("Your only key can&#x27;t be deleted.").length - 1, 1);
    assert.ok(!/role="alert"[^>]*>Your only key/.test(html));
  });

  test("without a reason the button is live and starts the confirmation", async () => {
    const { default: View } = await load();
    const clicks: string[] = [];
    const all = buttons(View(props({}, clicks)));

    assert.equal(all.length, 1);
    assert.notEqual(all[0].props.disabled, true);
    all[0].props.onClick?.();
    assert.deepEqual(clicks, ["start"]);
  });

  test("the reason gives way once the panel is open", async () => {
    const { default: View } = await load();
    const html = renderToStaticMarkup(
      View(props({ phase: "confirming", disabledReason: "Not now." })),
    );

    assert.ok(!html.includes("Not now."));
    assert.ok(html.includes(LABELS.confirmBody));
  });

  test("the confirmation uses its own ids and the red tone", async () => {
    const { default: View } = await load();
    const html = renderToStaticMarkup(View(props({ phase: "confirming" })));

    assert.match(html, /id="delete-key-row_1"/);
    assert.match(html, /aria-labelledby="delete-key-row_1-title"/);
    assert.ok(html.includes("bg-red-600"));
    assert.ok(!html.includes("bg-amber-600"));
    assert.ok(!html.includes("rotate-key-row_1"));
  });

  test("defaults stay as rotation had them: rotate-key ids, amber", async () => {
    const { default: View } = await load();
    const html = renderToStaticMarkup(
      View(
        props({ phase: "confirming", idPrefix: undefined, tone: undefined }),
      ),
    );

    assert.match(html, /id="rotate-key-row_1"/);
    assert.ok(html.includes("bg-amber-600"));
  });
});

// ---------------------------------------------------------------------------
// The emails
// ---------------------------------------------------------------------------

describe("key-issued and key-deleted emails", () => {
  const base = {
    customerName: "<b>Ada</b>",
    customerEmail: "ada@example.test",
    supportEmail: "hi@support.hyperwhisper.com",
  };

  function assertPrefixOnly(html: string, textPart: string): void {
    for (const part of [html, textPart]) {
      assert.ok(!part.includes(KEY), "no full key in the email");
      assert.ok(part.includes(KEY.substring(0, 7)), "the prefix is shown");
    }

    assert.ok(!html.includes("<b>Ada</b>"));
    assert.ok(html.includes("&lt;b&gt;Ada&lt;/b&gt;"));
  }

  test("key-issued names only the new key's prefix and escapes the name", async () => {
    const { keyIssuedEmailHtml, keyIssuedEmailText } = await import(
      "../lib/templates/key-issued-email"
    );
    const data = {
      ...base,
      newLicenseKey: KEY,
      issuedAt: new Date("2026-10-30T10:00:00Z"),
    };

    assertPrefixOnly(keyIssuedEmailHtml(data), keyIssuedEmailText(data));
  });

  test("key-deleted names only the deleted key's prefix and says it no longer works", async () => {
    const { keyDeletedEmailHtml, keyDeletedEmailText } = await import(
      "../lib/templates/key-deleted-email"
    );
    const data = {
      ...base,
      deletedLicenseKey: KEY,
      deletedAt: new Date("2026-10-30T10:00:00Z"),
    };
    const html = keyDeletedEmailHtml(data);
    const textPart = keyDeletedEmailText(data);

    assertPrefixOnly(html, textPart);
    assert.match(textPart, /no longer works on any device/);
  });
});

describe("cloudPurgeConfirmed: when the owner is told the key may linger", () => {
  test("only every cloud app confirming counts; a failure or a skipped purge does not", async () => {
    const { cloudPurgeConfirmed } = await import(
      "../lib/services/cloud-license-cache"
    );

    assert.equal(cloudPurgeConfirmed({ purged: 2, failed: 0, skipped: false }), true);
    assert.equal(cloudPurgeConfirmed({ purged: 1, failed: 1, skipped: false }), false);
    assert.equal(cloudPurgeConfirmed({ purged: 0, failed: 0, skipped: true }), false);
  });
});

describe("the Account Keys card's one notice: only the latest line shows", () => {
  async function loadNotice() {
    return import("../components/customer/dashboard/key-card-notice");
  }

  /** Every place a line can render on a card with keys a and b. */
  function onScreen(
    mod: Awaited<ReturnType<typeof loadNotice>>,
    state: ReturnType<typeof mod.postKeyCardNotice>,
  ): string[] {
    const { NOTICE_AT, keyCardNoticeAt } = mod;
    const places = [
      NOTICE_AT.card,
      NOTICE_AT.issue,
      ...["a", "b"].flatMap((id) => [NOTICE_AT.rotate(id), NOTICE_AT.deleteKey(id)]),
    ];

    return places.flatMap((at) => keyCardNoticeAt(state, at) ?? []);
  }

  test("add then delete: the delete line replaces the add line (the verify defect)", async () => {
    const mod = await loadNotice();
    const { NOTICE_AT, postKeyCardNotice } = mod;
    let state = postKeyCardNotice(null, NOTICE_AT.issue, "New key added.");

    state = postKeyCardNotice(state, NOTICE_AT.card, "Key deleted.");
    assert.deepEqual(onScreen(mod, state), ["Key deleted."]);
  });

  test("any sequence of add, delete, rotate and sent links leaves exactly the latest line", async () => {
    const mod = await loadNotice();
    const { NOTICE_AT, postKeyCardNotice } = mod;
    const steps: Array<[string, string]> = [
      [NOTICE_AT.card, "Key deleted."],
      [NOTICE_AT.issue, "New key added."],
      [NOTICE_AT.rotate("a"), "Key rotated."],
      [NOTICE_AT.issue, "New key added."],
      [NOTICE_AT.deleteKey("b"), "Sign-in link sent."],
      [NOTICE_AT.card, "Key deleted."],
      [NOTICE_AT.rotate("b"), "Key rotated."],
    ];
    let state = null as ReturnType<typeof postKeyCardNotice>;

    for (const [at, text] of steps) {
      state = postKeyCardNotice(state, at, text);
      assert.deepEqual(onScreen(mod, state), [text], `after "${text}" at ${at}`);
    }
  });

  test("a control starting over clears only its own line", async () => {
    const mod = await loadNotice();
    const { NOTICE_AT, postKeyCardNotice } = mod;
    const added = postKeyCardNotice(null, NOTICE_AT.issue, "New key added.");

    // Opening Rotate on a key does not wipe the add line...
    assert.deepEqual(
      onScreen(mod, postKeyCardNotice(added, NOTICE_AT.rotate("a"), null)),
      ["New key added."],
    );
    // ...but opening Add again does.
    assert.deepEqual(onScreen(mod, postKeyCardNotice(added, NOTICE_AT.issue, null)), []);
  });
});
