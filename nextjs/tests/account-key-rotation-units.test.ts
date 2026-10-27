/**
 * The pieces of Account Key rotation that the end-to-end suite
 * (`account-key-rotation.test.ts`) does not open up:
 *
 * - `RotateAccountKeyView`: hook-free, so it is CALLED as a function and its
 *   real `onClick`s are invoked (the `CloudCreditsCardView` pattern, #737);
 * - `purgeCloudLicenseCache`: every request carries a timeout `signal` and the
 *   secret, an unset secret sends nothing, a failure never throws and no log
 *   line carries the key;
 * - the "key replaced" email: names the new key's prefix only.
 */
import assert from "node:assert/strict";
import { describe, test } from "node:test";
import { Children, Fragment, createElement, isValidElement, type ReactElement, type ReactNode } from "react";
import { renderToStaticMarkup } from "react-dom/server";

import { consoleSilencer } from "./module-mock";

import type { RotateAccountKeyViewProps } from "../components/customer/dashboard/RotateAccountKeyView";

process.env.SKIP_ENV_VALIDATION = "1";

// Built from parts so no credential-shaped literal sits in the source.
const KEY = ["HW", "QQQQ", "RRRR", "SSSS", "TTTT"].join("-");

// ---------------------------------------------------------------------------
// RotateAccountKeyView
// ---------------------------------------------------------------------------

// A variable specifier: a literal `.tsx` one is TS5097 under this tsconfig.
const VIEW_PATH = "../components/customer/dashboard/RotateAccountKeyView.tsx";

type View = (props: RotateAccountKeyViewProps) => ReactElement;

async function loadView(): Promise<View> {
  return ((await import(VIEW_PATH)) as { default: View }).default;
}

const LABELS = {
  rotate: "Rotate key",
  confirmTitle: "Replace this Account Key?",
  confirmBody: "Devices using the old key will need the new one.",
  confirm: "Rotate key",
  cancel: "Cancel",
  rotating: "Rotating…",
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
    onCancel: () => clicks.push("cancel"),
    ...overrides,
  };
}

/** Every `<button>` in the tree the view returned, without rendering children components. */
function buttons(node: ReactNode): ReactElement<{ onClick?: () => void; disabled?: boolean; children?: ReactNode }>[] {
  const found: ReactElement<{ onClick?: () => void; disabled?: boolean; children?: ReactNode }>[] = [];
  const walk = (n: ReactNode) => {
    Children.forEach(n, (child) => {
      if (!isValidElement(child)) return;
      const el = child as ReactElement<{ children?: ReactNode; onClick?: () => void }>;
      if (el.type === "button") found.push(el);
      walk(el.props.children);
    });
  };
  walk(node);
  return found;
}

function text(node: ReactNode): string {
  return renderToStaticMarkup(createElement(Fragment, null, node)).replace(/<[^>]+>/g, " ");
}

describe("RotateAccountKeyView", () => {
  test("idle: one Rotate button, wired to onStart, no confirmation shown", async () => {
    const View = await loadView();
    const clicks: string[] = [];
    const tree = View(props({}, clicks));
    const all = buttons(tree);
    assert.equal(all.length, 1);
    all[0].props.onClick?.();
    assert.deepEqual(clicks, ["start"]);
    assert.ok(!renderToStaticMarkup(tree).includes(LABELS.confirmBody));
  });

  test("confirming: the warning about devices, confirm and cancel reach their handlers", async () => {
    const View = await loadView();
    const clicks: string[] = [];
    const tree = View(props({ phase: "confirming" }, clicks));
    const html = renderToStaticMarkup(tree);
    assert.match(html, /role="group"/);
    assert.ok(html.includes(LABELS.confirmTitle));
    assert.ok(html.includes(LABELS.confirmBody));
    const [confirm, cancel] = buttons(tree);
    confirm.props.onClick?.();
    cancel.props.onClick?.();
    assert.deepEqual(clicks, ["confirm", "cancel"]);
    assert.equal(confirm.props.disabled, false);
  });

  test("rotating: both buttons disabled and the busy label shown", async () => {
    const View = await loadView();
    const tree = View(props({ phase: "rotating" }));
    const [confirm, cancel] = buttons(tree);
    assert.equal(confirm.props.disabled, true);
    assert.equal(cancel.props.disabled, true);
    assert.ok(text(confirm.props.children).includes(LABELS.rotating));
  });

  test("the notice and the error render in their live regions", async () => {
    const View = await loadView();
    const html = renderToStaticMarkup(
      View(props({ notice: "Key rotated.", error: "Too many rotations." })),
    );
    assert.match(html, /role="status"[^>]*>Key rotated\./);
    assert.match(html, /role="alert"[^>]*>Too many rotations\./);
  });
});

// ---------------------------------------------------------------------------
// purgeCloudLicenseCache
// ---------------------------------------------------------------------------

interface Sent {
  url: string;
  init: RequestInit;
}

async function loadPurge() {
  return import("../lib/services/cloud-license-cache");
}

describe("purgeCloudLicenseCache", () => {
  test("posts the key to every cloud, with the secret and a timeout signal", async () => {
    const { purgeCloudLicenseCache, CLOUD_PURGE_PATH } = await loadPurge();
    const sent: Sent[] = [];
    const outcome = await purgeCloudLicenseCache(KEY, {
      secret: "s3cret",
      urls: ["https://a.test", "https://b.test"],
      fetch: (async (url: string, init: RequestInit) => {
        sent.push({ url, init });
        return new Response("{}", { status: 200 });
      }) as unknown as typeof fetch,
    });
    assert.deepEqual(outcome, { purged: 2, failed: 0, skipped: false });
    assert.deepEqual(
      sent.map((s) => s.url),
      [`https://a.test${CLOUD_PURGE_PATH}`, `https://b.test${CLOUD_PURGE_PATH}`],
    );
    for (const { init } of sent) {
      assert.equal(init.method, "POST");
      assert.ok(init.signal instanceof AbortSignal, "every cloud fetch carries a signal");
      assert.equal(new Headers(init.headers).get("x-internal-secret"), "s3cret");
      assert.deepEqual(JSON.parse(String(init.body)), { account_key: KEY });
    }
  });

  test("no secret: nothing is sent, and it says so", async () => {
    const { purgeCloudLicenseCache } = await loadPurge();
    const lines: string[] = [];
    const quiet = consoleSilencer(["warn"], lines);
    quiet.silence();
    let called = false;
    try {
      const outcome = await purgeCloudLicenseCache(KEY, {
        secret: undefined,
        urls: ["https://a.test"],
        fetch: (async () => {
          called = true;
          return new Response();
        }) as unknown as typeof fetch,
      });
      assert.deepEqual(outcome, { purged: 0, failed: 0, skipped: true });
    } finally {
      quiet.restore();
    }
    assert.equal(called, false);
    assert.equal(lines.length, 1);
  });

  test("a refusal or a network error is counted, never thrown, never logs the key", async () => {
    const { purgeCloudLicenseCache } = await loadPurge();
    const lines: string[] = [];
    const quiet = consoleSilencer(["warn", "error", "log"], lines);
    quiet.silence();
    try {
      const outcome = await purgeCloudLicenseCache(KEY, {
        secret: "s3cret",
        urls: ["https://ok.test", "https://down.test", "https://refuses.test"],
        fetch: (async (url: string) => {
          if (url.startsWith("https://down.test")) {
            throw new TypeError(`fetch failed for ${KEY}`);
          }
          return new Response("{}", { status: url.startsWith("https://refuses.test") ? 401 : 200 });
        }) as unknown as typeof fetch,
      });
      assert.deepEqual(outcome, { purged: 1, failed: 2, skipped: false });
    } finally {
      quiet.restore();
    }
    assert.equal(lines.length, 2);
    assert.ok(lines.some((l) => l.includes("down.test") && l.includes("TypeError")));
    assert.ok(lines.some((l) => l.includes("refuses.test") && l.includes("HTTP 401")));
    for (const line of lines) assert.ok(!line.includes(KEY));
  });

  test("cloudPurgeUrls: the prod+staging default only on the production deployment", async () => {
    const { cloudPurgeUrls, DEFAULT_CLOUD_URLS } = await loadPurge();
    assert.deepEqual(cloudPurgeUrls(undefined, "production"), [...DEFAULT_CLOUD_URLS]);
    // A preview or local site must never tell the production cloud anything.
    assert.deepEqual(cloudPurgeUrls(undefined, "preview"), []);
    assert.deepEqual(cloudPurgeUrls(undefined, "development"), []);
    assert.deepEqual(cloudPurgeUrls(undefined, undefined), []);
    // Configured: used as given, on any deployment.
    assert.deepEqual(cloudPurgeUrls(" https://a.test/ , https://b.test//,", "preview"), [
      "https://a.test",
      "https://b.test",
    ]);
    assert.deepEqual(cloudPurgeUrls("", "production"), []);
  });

  test("no cloud app configured: nothing is sent, quietly", async () => {
    const { purgeCloudLicenseCache } = await loadPurge();
    let called = false;
    const outcome = await purgeCloudLicenseCache(KEY, {
      secret: "s3cret",
      urls: [],
      fetch: (async () => {
        called = true;
        return new Response();
      }) as unknown as typeof fetch,
    });
    assert.deepEqual(outcome, { purged: 0, failed: 0, skipped: true });
    assert.equal(called, false);
  });
});

// ---------------------------------------------------------------------------
// The "key replaced" email
// ---------------------------------------------------------------------------

describe("key-rotated email", () => {
  test("names only the new key's prefix, in both parts, and escapes the name", async () => {
    const { keyRotatedEmailHtml, keyRotatedEmailText } = await import(
      "../lib/templates/key-rotated-email"
    );
    const data = {
      customerName: "<b>Ada</b>",
      customerEmail: "ada@example.test",
      newLicenseKey: KEY,
      rotatedAt: new Date("2026-10-27T10:00:00Z"),
      supportEmail: "hi@support.hyperwhisper.com",
    };
    const html = keyRotatedEmailHtml(data);
    const textPart = keyRotatedEmailText(data);
    for (const part of [html, textPart]) {
      assert.ok(!part.includes(KEY), "no full key in the email");
      assert.ok(part.includes(KEY.substring(0, 7)), "the prefix is shown");
    }
    assert.ok(!html.includes("<b>Ada</b>"));
    assert.ok(html.includes("&lt;b&gt;Ada&lt;/b&gt;"));
  });
});
