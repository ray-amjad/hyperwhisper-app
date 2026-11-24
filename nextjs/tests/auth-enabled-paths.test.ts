/**
 * Better Auth's HTTP surface is an allow-list (`src/lib/auth-enabled-paths.ts`).
 *
 * A license-key sign-in mints a normal session cookie. With the built-in
 * routes left mounted, that cookie can list and revoke sessions and rewrite
 * the user. The website never calls those routes (web uses the five paths
 * below; desktop and mobile never call `/api/auth/*`), so they are disabled.
 *
 * Driven through the real `/api/auth/[...all]` route, the same way
 * `auth-catch-all-route.test.ts` drives it. Disabled paths answer before any
 * session lookup, so the cases send a session cookie and still get 404 — a
 * key-minted session cannot reach them. Enabled paths still reach their
 * handlers and stop at validation, before a database query.
 */
import assert from "node:assert/strict";
import { before, test } from "node:test";

import { moduleMock, moduleUrl } from "./module-mock";

import {
  DISABLED_AUTH_PATHS,
  ENABLED_AUTH_PATHS,
  normalizeAuthPath,
} from "../src/lib/auth-enabled-paths";

moduleMock.module(moduleUrl("../lib/clients/email.ts"), {
  namedExports: {
    sendEmail: async () => ({ data: { id: "unused" }, error: null }),
    DEFAULT_FROM_EMAIL: "HyperWhisper <support@hyperwhisper.com>",
  },
});

const ORIGIN = "http://localhost:3000";
const BASE = `${ORIGIN}/api/auth`;

// `auth.ts` reads this once, when `betterAuth()` runs at import time.
process.env.SKIP_ENV_VALIDATION = "1";
process.env.BETTER_AUTH_URL = ORIGIN;

type Handler = (request: Request) => Promise<Response>;
let GET: Handler;
let POST: Handler;

const ROUTE_PATH = "../app/api/auth/[...all]/route.ts";

before(async () => {
  const route = await import(ROUTE_PATH);
  GET = route.GET;
  POST = route.POST;
});

/**
 * The method each built-in route is actually registered with. A POST to a
 * GET-only route is already 404, which would hide a path we forgot to disable.
 */
const DISABLED_BY_METHOD: ReadonlyArray<{
  path: (typeof DISABLED_AUTH_PATHS)[number];
  method: "GET" | "POST";
}> = [
  { path: "/account-info", method: "GET" },
  { path: "/change-email", method: "POST" },
  { path: "/change-password", method: "POST" },
  { path: "/delete-user", method: "POST" },
  { path: "/delete-user/callback", method: "GET" },
  { path: "/error", method: "GET" },
  { path: "/get-access-token", method: "POST" },
  { path: "/link-social", method: "POST" },
  { path: "/list-accounts", method: "GET" },
  { path: "/list-sessions", method: "GET" },
  { path: "/ok", method: "GET" },
  { path: "/refresh-token", method: "POST" },
  { path: "/request-password-reset", method: "POST" },
  { path: "/reset-password", method: "POST" },
  { path: "/revoke-other-sessions", method: "POST" },
  { path: "/revoke-session", method: "POST" },
  { path: "/revoke-sessions", method: "POST" },
  { path: "/send-verification-email", method: "POST" },
  { path: "/sign-in/email", method: "POST" },
  { path: "/sign-in/social", method: "POST" },
  { path: "/sign-up/email", method: "POST" },
  { path: "/unlink-account", method: "POST" },
  { path: "/update-session", method: "POST" },
  { path: "/update-user", method: "POST" },
  { path: "/verify-email", method: "GET" },
  { path: "/verify-password", method: "POST" },
];

const KEY_SESSION_COOKIE = "better-auth.session_token=account-key-session.sig";

function request(
  method: "GET" | "POST",
  path: string,
  body?: unknown,
  cookie?: string,
): Request {
  return new Request(`${BASE}${path}`, {
    method,
    headers: {
      origin: ORIGIN,
      ...(cookie ? { cookie } : {}),
      ...(body !== undefined ? { "content-type": "application/json" } : {}),
    },
    ...(body !== undefined ? { body: JSON.stringify(body) } : {}),
  });
}

async function call(
  method: "GET" | "POST",
  path: string,
  body?: unknown,
  cookie?: string,
) {
  const response = await (method === "GET" ? GET : POST)(
    request(method, path, body, cookie),
  );
  const text = await response.text();

  return { status: response.status, text };
}

test("the allow-list is the five routes the website calls, and nothing else", async () => {
  assert.deepEqual(ENABLED_AUTH_PATHS, [
    "/get-session",
    "/magic-link/verify",
    "/sign-in/license-key",
    "/sign-in/magic-link",
    "/sign-out",
  ]);

  for (const path of ENABLED_AUTH_PATHS) {
    assert.equal(
      (DISABLED_AUTH_PATHS as readonly string[]).includes(path),
      false,
      path,
    );
  }

  assert.deepEqual(
    DISABLED_BY_METHOD.map((entry) => entry.path),
    [...DISABLED_AUTH_PATHS],
  );

  assert.equal(
    normalizeAuthPath(`${BASE}/get-session/`, "/api/auth"),
    "/get-session",
  );
  assert.equal(
    normalizeAuthPath(`${BASE}/callback/google?code=1`, "/api/auth"),
    "/callback/google",
  );

  // The denylist is what Better Auth unmounts; the plugin is the default
  // deny. `nextCookies()` has to stay the last plugin.
  const { auth } = await import("../src/lib/auth");
  const plugins = auth.options.plugins?.map((plugin) => plugin.id) ?? [];

  assert.deepEqual(auth.options.disabledPaths, [...DISABLED_AUTH_PATHS]);
  assert.equal(plugins[0], "auth-path-allow-list");
  assert.equal(plugins.at(-1), "next-cookies");
});

test("disabled built-in routes 404 even with a session cookie", async () => {
  for (const { path, method } of DISABLED_BY_METHOD) {
    const response = await call(
      method,
      path,
      method === "POST"
        ? { name: "Probe", newEmail: "probe@example.com" }
        : undefined,
      KEY_SESSION_COOKIE,
    );

    assert.equal(response.status, 404, `${method} ${path}`);
    assert.equal(response.text, "Not Found", `${method} ${path}`);
  }
});

test("dynamic and unknown routes are not on the allow-list", async () => {
  // `disabledPaths` matches the path exactly, so these two registered
  // patterns (`/callback/:id`, `/reset-password/:token`) only stay closed
  // because of the allow-list. `/nope` was already a router 404.
  for (const path of ["/callback/google", "/reset-password/not-a-token", "/nope"]) {
    const response = await call("GET", path, undefined, KEY_SESSION_COOKIE);

    assert.equal(response.status, 404, path);
    assert.equal(response.text, "Not Found", path);
  }
});

test("the routes the portal calls still reach their handlers", async () => {
  const getSession = await call("GET", "/get-session");
  assert.equal(getSession.status, 200);
  assert.equal(getSession.text, "null");

  const refresh = await call("POST", "/get-session", {});
  assert.equal(refresh.status, 200);
  assert.equal(refresh.text, "null");

  const magicLink = await call("POST", "/sign-in/magic-link", {
    email: "not-an-address",
  });
  assert.equal(magicLink.status, 400);
  assert.match(magicLink.text, /VALIDATION_ERROR/);

  const licenseKey = await call("POST", "/sign-in/license-key", {});
  assert.equal(licenseKey.status, 400);
  assert.match(licenseKey.text, /VALIDATION_ERROR/);

  const verify = await call("GET", "/magic-link/verify");
  assert.equal(verify.status, 400);
  assert.match(verify.text, /VALIDATION_ERROR/);

  const signOut = await call("POST", "/sign-out", {});
  assert.equal(signOut.status, 200);
  assert.match(signOut.text, /"success":true/);
});
