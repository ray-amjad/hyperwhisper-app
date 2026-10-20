/**
 * #2070: a magic link that was already used, has expired, or was opened first
 * by an email scanner lands on `/[locale]/user/sign-in` with a message that
 * says so, and a way to ask for a new one.
 *
 * Before the fix `SignInClient.tsx` sent `authClient.signIn.magicLink()` no
 * `errorCallbackURL`, so Better Auth sent the failure to the `callbackURL`
 * (`/en/user/dashboard?error=INVALID_TOKEN`). `proxy.ts` bounced the
 * signed-out visitor to `/en/user/sign-in?returnTo=%2Fen%2Fuser%2Fdashboard`,
 * dropping the query, and the sign-in form never read `?error` anyway.
 *
 * This file walks that chain end to end, one real piece at a time:
 *
 * 1. the REAL `SignInClient` is rendered and its email form submitted; the
 *    arguments it hands `authClient.signIn.magicLink` are captured;
 * 2. those arguments are POSTed to the REAL Better Auth handler, built from
 *    `src/lib/auth.ts`'s own options (its real `magicLink` plugin and its real
 *    `sendMagicLink`), with only the database swapped for Better Auth's memory
 *    adapter and the Resend client stubbed so the emailed URL can be read;
 * 3. the emailed link is opened once (it signs in), then again, and a second
 *    link is opened after it expired; each dead link's redirect must keep the
 *    error code AND the `returnTo`;
 * 4. that redirect target goes through the REAL `proxy.ts` as a signed-out
 *    request, which must not bounce it (bouncing is what dropped the query);
 * 5. the REAL `SignInClient` is rendered at that URL, in `en` and `de`, and
 *    must show the translated message on the Email tab.
 *
 * WHAT IT DOES NOT PROVE: the browser hop between steps (cookies, the client
 * commit after the redirect) and the real Postgres adapter. The verify round
 * drives those in Chromium against a real server and database.
 */
import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import test, { mock } from "node:test";

import { memoryAdapter } from "better-auth/adapters/memory";
import { NextIntlClientProvider } from "next-intl";
import { NextRequest } from "next/server";
import { createElement, type ReactElement } from "react";
import * as realJsxRuntime from "react/jsx-runtime";
import { renderToStaticMarkup } from "react-dom/server";

/** See credit-routes-harness.ts: @types/node@20 has no `mock.module`. */
interface ModuleMocker {
  module(
    specifier: string,
    options: { namedExports?: Record<string, unknown> },
  ): void;
}

const moduleMock = mock as unknown as ModuleMocker;

const ORIGIN = "http://localhost:3000";
const EMAIL = "dead-link-probe@example.com";
const RETURN_TO = "/en/user/licenses";

// `auth.ts` reads these once, when `betterAuth()` runs at import time.
// BETTER_AUTH_URL arrives through `src/env/server.mjs`, which snapshots
// process.env on its first import (#682); the other required secrets are
// absent here, so its format check is skipped.
process.env.SKIP_ENV_VALIDATION = "1";
process.env.BETTER_AUTH_URL = ORIGIN;
process.env.BETTER_AUTH_SECRET =
  "test-secret-for-2070-dead-magic-link-0123456789";

// ---------------------------------------------------------------------------
// The Resend edge: the one stub on the server side. The emailed URL is read
// back out of the plain-text body the real `sendMagicLink` builds.
// ---------------------------------------------------------------------------

const sentEmails: Array<{ text?: string }> = [];

moduleMock.module(new URL("../lib/clients/resend.ts", import.meta.url).href, {
  namedExports: {
    resend: {
      emails: {
        send: async (payload: { text?: string }) => {
          sentEmails.push(payload);
          return { data: { id: "unused" }, error: null };
        },
      },
    },
    DEFAULT_FROM_EMAIL: "HyperWhisper <support@hyperwhisper.com>",
  },
});

// ---------------------------------------------------------------------------
// The client side: the same no-DOM render-phase technique as
// `sign-in-error-announced.test.ts`. When `submitEmailForm` is set, the Email
// tab button's own `onClick` and then the email form's own `onSubmit` are
// fired while the component builds them.
// ---------------------------------------------------------------------------

let searchParams = new URLSearchParams();
let submitEmailForm = false;
let switchedTab = false;
let submitted = false;
let lastInputId: string | undefined;

interface Props {
  id?: string;
  children?: unknown;
  onClick?: () => void;
  onSubmit?: () => void;
}

function fireSubmit(type: unknown, rawProps: unknown): void {
  if (!submitEmailForm) return;
  const props = rawProps as Props;

  if (type === "input") lastInputId = props.id;

  if (!switchedTab && type === "button" && props.children === "Email") {
    switchedTab = true;
    props.onClick?.();

    return;
  }

  if (!submitted && type === "form" && lastInputId === "email") {
    submitted = true;
    props.onSubmit?.();
  }
}

type JsxFn = (type: unknown, props: unknown, key?: unknown) => unknown;
const realJsx = realJsxRuntime.jsx as unknown as JsxFn;
const realJsxs = realJsxRuntime.jsxs as unknown as JsxFn;

moduleMock.module("react/jsx-runtime", {
  namedExports: {
    Fragment: realJsxRuntime.Fragment,
    jsx: (type: unknown, props: unknown, key?: unknown) => {
      fireSubmit(type, props);

      return realJsx(type, props, key);
    },
    jsxs: (type: unknown, props: unknown, key?: unknown) => {
      fireSubmit(type, props);

      return realJsxs(type, props, key);
    },
  },
});

moduleMock.module("next/navigation", {
  namedExports: {
    useRouter: () => ({ push: () => {} }),
    useSearchParams: () => searchParams,
    useParams: () => ({ locale: "en" }),
  },
});

interface MagicLinkArgs {
  email: string;
  callbackURL: string;
  errorCallbackURL?: string;
}

const magicLinkCalls: MagicLinkArgs[] = [];

// Also keeps Better Auth's browser client out of the graph. The call is
// recorded synchronously; the promise never settles, so the render stays on
// the form.
moduleMock.module("../src/lib/auth-client", {
  namedExports: {
    authClient: {
      signIn: {
        magicLink: (args: MagicLinkArgs) => {
          magicLinkCalls.push(args);
          return new Promise(() => {});
        },
      },
    },
  },
});

const COMPONENT_PATH = "../app/[locale]/user/sign-in/SignInClient.tsx";

function messages(locale: string): Record<string, Record<string, string>> {
  return JSON.parse(
    readFileSync(
      new URL(`../messages/${locale}.json`, import.meta.url),
      "utf8",
    ),
  ) as Record<string, Record<string, string>>;
}

async function renderSignIn(locale: string, query: URLSearchParams) {
  searchParams = query;
  switchedTab = false;
  submitted = false;
  lastInputId = undefined;
  const { default: SignInClient } = (await import(COMPONENT_PATH)) as {
    default: () => ReactElement;
  };

  return renderToStaticMarkup(
    createElement(NextIntlClientProvider, {
      locale,
      messages: messages(locale),
      timeZone: "UTC",
      children: createElement(SignInClient),
    }),
  );
}

// ---------------------------------------------------------------------------
// The server side: Better Auth built from the app's own options.
// ---------------------------------------------------------------------------

type Row = Record<string, unknown>;
const store: Record<string, Row[]> = {
  user: [],
  session: [],
  account: [],
  verification: [],
};

async function realAuthOnMemory() {
  const { betterAuth } = await import("better-auth");
  const { auth } = await import("../src/lib/auth");

  return betterAuth({
    ...auth.options,
    database: memoryAdapter(store),
    // The app's limiter is Upstash-backed; it is not what this file tests.
    rateLimit: { enabled: false },
  });
}

async function requestLink(
  handler: (req: Request) => Promise<Response>,
  body: MagicLinkArgs,
): Promise<string> {
  const before = sentEmails.length;
  const res = await handler(
    new Request(`${ORIGIN}/api/auth/sign-in/magic-link`, {
      method: "POST",
      headers: { "content-type": "application/json", origin: ORIGIN },
      body: JSON.stringify(body),
    }),
  );

  assert.equal(res.status, 200, `send failed: ${await res.text()}`);
  assert.equal(sentEmails.length, before + 1, "no email was sent");
  const link = /https?:\/\/\S+\/magic-link\/verify\?\S+/.exec(
    sentEmails[before].text ?? "",
  )?.[0];

  assert.ok(link, "no verify link in the email body");

  return link;
}

/** Opens a link as a signed-out browser would: no cookie, no redirect follow. */
async function open(
  handler: (req: Request) => Promise<Response>,
  link: string,
) {
  return handler(new Request(link, { redirect: "manual" }));
}

async function signedOutThroughProxy(target: URL) {
  const { default: proxy } = await import("../proxy");

  return proxy(
    new NextRequest(target, { headers: { "sec-fetch-dest": "document" } }),
  );
}

function assertDeadLinkLanding(location: string | null): URL {
  assert.ok(location, "the dead link did not redirect");
  const url = new URL(location, ORIGIN);

  assert.equal(url.pathname, "/en/user/sign-in", `landed on ${url}`);
  assert.equal(url.searchParams.get("error"), "INVALID_TOKEN");
  assert.equal(
    url.searchParams.get("returnTo"),
    RETURN_TO,
    "returnTo was lost on the way",
  );

  return url;
}

function assertShowsMessage(html: string, message: string) {
  assert.ok(html.includes(message), "the dead-link message is not on the page");
  assert.match(
    html,
    /<div [^>]*role="alert"[^>]*><p [^>]*id="magic-link-error"[^>]*>/,
  );
  // On the Email tab, with its form there to ask for a new link.
  assert.match(html, /<button aria-pressed="true"[^>]*>Email<\/button>/);
  assert.match(
    html,
    /id="email"[^>]*aria-describedby="magic-link-error"|aria-describedby="magic-link-error"[^>]*id="email"/,
  );
}

test("#2070: a used or expired magic link lands on sign-in with a message", async () => {
  // 1. The sign-in form, opened with a returnTo, sends a magic link.
  submitEmailForm = true;
  await renderSignIn("en", new URLSearchParams({ returnTo: RETURN_TO }));
  submitEmailForm = false;

  assert.equal(magicLinkCalls.length, 1, "the email form never sent a link");
  const sent = { ...magicLinkCalls[0], email: EMAIL };

  assert.equal(sent.callbackURL, RETURN_TO);
  assert.ok(sent.errorCallbackURL, "no errorCallbackURL was sent");

  // 2-3. The real handler sends it; the first click signs in.
  const { handler } = await realAuthOnMemory();
  const link = await requestLink(handler, sent);
  const first = await open(handler, link);

  assert.equal(first.status, 302);
  assert.equal(
    new URL(first.headers.get("location") ?? "", ORIGIN).pathname,
    RETURN_TO,
  );
  assert.match(first.headers.get("set-cookie") ?? "", /session_token=/);

  // The second click (or the user's click after a scanner's).
  const reused = await open(handler, link);

  assert.equal(reused.status, 302);
  const landing = assertDeadLinkLanding(reused.headers.get("location"));

  // A link opened after its 10 minutes.
  const expiring = await requestLink(handler, sent);
  for (const row of store.verification)
    row.expiresAt = new Date(Date.now() - 1000);
  assertDeadLinkLanding(
    (await open(handler, expiring)).headers.get("location"),
  );

  // 4. A signed-out visitor is not bounced off the sign-in page, so the query
  //    that carries the error survives.
  const proxied = await signedOutThroughProxy(landing);

  assert.equal(
    proxied.headers.get("location"),
    null,
    "proxy redirected the landing",
  );

  // 5. The page the user sees, in English and in a second locale.
  for (const locale of ["en", "de"]) {
    const html = await renderSignIn(locale, landing.searchParams);

    assertShowsMessage(html, messages(locale).signIn.magicLinkExpired);
  }
});

test("#2070: any other verify failure gets the generic link message", async () => {
  const html = await renderSignIn(
    "en",
    new URLSearchParams({
      error: "failed_to_create_session",
      returnTo: RETURN_TO,
    }),
  );

  assertShowsMessage(html, messages("en").signIn.magicLinkFailed);
});

test("#2070: no ?error, no message, and the Account Key tab is first", async () => {
  const html = await renderSignIn(
    "en",
    new URLSearchParams({ returnTo: RETURN_TO }),
  );

  assert.doesNotMatch(html, /magic-link-error/);
  assert.match(html, /<button aria-pressed="true"[^>]*>Account Key<\/button>/);
});

test("#2070: every locale carries both messages, translated", () => {
  const en = messages("en").signIn;
  const locales = readdirSync(new URL("../messages/", import.meta.url))
    .filter((f) => f.endsWith(".json"))
    .map((f) => f.replace(/\.json$/, ""));

  assert.equal(locales.length, 40);
  for (const locale of locales) {
    const own = messages(locale).signIn;

    for (const key of ["magicLinkExpired", "magicLinkFailed"]) {
      assert.equal(typeof own?.[key], "string", `${locale}.signIn.${key}`);
      if (locale !== "en") {
        assert.notEqual(
          own[key],
          en[key],
          `${locale}.signIn.${key} is English`,
        );
      }
    }
  }
});
