/**
 * `app/api/auth/[...all]/route.ts`, driven as HTTP.
 *
 * The route is one line: `toNextJsHandler(auth)`. It is still the only door
 * the browser has into Better Auth — sign-in, the session read and the daily
 * session refresh all arrive here — so these cases pin what that door lets
 * through, not how Better Auth works inside:
 *
 * - both exported methods reach the real `auth` instance under `/api/auth`;
 * - the two sign-in methods the portal offers (magic link, license key) are
 *   mounted, and email + password is NOT;
 * - a magic-link POST from a foreign origin, or one that asks to be redirected
 *   off-site, is refused before any handler runs (Better Auth's CSRF check,
 *   which only works while `BETTER_AUTH_URL` names the real origin), and a
 *   cross-site HTML form cannot reach either sign-in endpoint;
 * - `/get-session` answers a POST, which is the browser's half of the
 *   deferred session refresh described in `src/lib/auth.ts`.
 *
 * Every case stops before a database query, so `@/src/db` is real but never
 * used (see `magic-link-send.test.ts` for why `drizzle()` tolerates that). The
 * one mocked module is `lib/clients/resend.ts`, the email edge, which also
 * keeps `src/env/server.mjs` out of the graph. No case reaches it.
 */
import assert from "node:assert/strict";
import { before, mock, test } from "node:test";

/** See credit-routes-harness.ts: @types/node@20 has no `mock.module`. */
interface ModuleMocker {
  module(
    specifier: string,
    options: { namedExports: Record<string, unknown> },
  ): void;
}

const resendCalls: unknown[] = [];

(mock as unknown as ModuleMocker).module(
  new URL("../lib/clients/resend.ts", import.meta.url).href,
  {
    namedExports: {
      resend: {
        emails: {
          send: async (payload: unknown) => {
            resendCalls.push(payload);
            return { data: { id: "unused" }, error: null };
          },
        },
      },
      DEFAULT_FROM_EMAIL: "HyperWhisper <support@hyperwhisper.com>",
    },
  },
);

const ORIGIN = "http://localhost:3000";
const BASE = `${ORIGIN}/api/auth`;
const FOREIGN_ORIGIN = "https://evil.example";

// `auth.ts` reads this once, when `betterAuth()` runs at import time.
process.env.BETTER_AUTH_URL = ORIGIN;

type Handler = (request: Request) => Promise<Response>;
let GET: Handler;
let POST: Handler;

// A VARIABLE specifier, and deferred: a static import would bind before the
// `mock.module` call above, and a literal `.ts` specifier is a TS5097 error
// under this tsconfig.
const ROUTE_PATH = "../app/api/auth/[...all]/route.ts";

before(async () => {
  const route = await import(ROUTE_PATH);
  GET = route.GET;
  POST = route.POST;
});

function postJson(
  path: string,
  body: unknown,
  origin: string = ORIGIN,
): Request {
  return new Request(`${BASE}${path}`, {
    method: "POST",
    headers: { "content-type": "application/json", origin },
    body: JSON.stringify(body),
  });
}

async function readJson(response: Response): Promise<unknown> {
  const text = await response.text();
  return text === "" ? undefined : JSON.parse(text);
}

test("GET reaches Better Auth under /api/auth", async () => {
  const response = await GET(new Request(`${BASE}/ok`));

  assert.equal(response.status, 200);
  assert.deepEqual(await readJson(response), { ok: true });
});

test("GET /get-session with no session cookie answers null, not an error", async () => {
  const response = await GET(new Request(`${BASE}/get-session`));

  assert.equal(response.status, 200);
  assert.equal(await readJson(response), null);
});

test("POST /get-session is accepted, so the browser can finish the deferred refresh", async () => {
  // Without `deferSessionRefresh: true` Better Auth rejects a POST here, and
  // <SessionRefresher /> would fail silently, leaving a hard 90-day sign-out.
  const response = await POST(postJson("/get-session", {}));

  assert.equal(response.status, 200);
  assert.equal(await readJson(response), null);
});

test("POST reaches the magic-link plugin and validates its body", async () => {
  const response = await POST(
    postJson("/sign-in/magic-link", { email: "not-an-address" }),
  );

  assert.equal(response.status, 400);
  assert.deepEqual(await readJson(response), {
    message: "[body.email] Invalid email address",
    code: "VALIDATION_ERROR",
  });
  assert.equal(resendCalls.length, 0, "a rejected body sends no email");
});

test("POST reaches the license-key plugin and validates its body", async () => {
  const response = await POST(postJson("/sign-in/license-key", {}));

  assert.equal(response.status, 400);
  const body = (await readJson(response)) as { code: string; message: string };
  assert.equal(body.code, "VALIDATION_ERROR");
  assert.match(body.message, /^\[body\.licenseKey\]/);
});

test("a magic-link POST from a foreign origin is refused before the handler runs", async () => {
  const response = await POST(
    postJson(
      "/sign-in/magic-link",
      { email: "probe@example.com" },
      FOREIGN_ORIGIN,
    ),
  );

  assert.equal(response.status, 403);
  assert.deepEqual(await readJson(response), {
    message: "Invalid origin",
    code: "INVALID_ORIGIN",
  });
  assert.equal(resendCalls.length, 0, "a refused request sends no email");
});

test("a cross-site form POST cannot reach either sign-in endpoint", async () => {
  // A plain HTML form is the one cross-site request a browser sends with no
  // CORS preflight. The license-key endpoint has no Origin check of its own
  // (Better Auth checks Origin on a cookie-less POST only for endpoints that
  // opt in, and magic-link does), so this content-type refusal is what keeps
  // a hostile page from signing a visitor in to the attacker's account.
  for (const path of ["/sign-in/magic-link", "/sign-in/license-key"]) {
    const response = await POST(
      new Request(`${BASE}${path}`, {
        method: "POST",
        headers: {
          "content-type": "application/x-www-form-urlencoded",
          origin: FOREIGN_ORIGIN,
          "sec-fetch-site": "cross-site",
          "sec-fetch-mode": "navigate",
        },
        body: "email=probe%40example.com&licenseKey=not-a-key",
      }),
    );

    assert.equal(response.status, 415, path);
    assert.equal(
      ((await readJson(response)) as { code: string }).code,
      "UNSUPPORTED_MEDIA_TYPE",
      path,
    );
  }
  assert.equal(resendCalls.length, 0, "a refused request sends no email");
});

test("a magic link that would redirect off-site is refused", async () => {
  const response = await POST(
    postJson("/sign-in/magic-link", {
      email: "probe@example.com",
      callbackURL: `${FOREIGN_ORIGIN}/steal`,
    }),
  );

  assert.equal(response.status, 403);
  assert.deepEqual(await readJson(response), {
    message: "Invalid callbackURL",
    code: "INVALID_CALLBACK_URL",
  });
  assert.equal(resendCalls.length, 0, "a refused request sends no email");
});

test("email + password sign-in and sign-up are not enabled", async () => {
  const signIn = await POST(
    postJson("/sign-in/email", {
      email: "probe@example.com",
      password: "a-password-1",
    }),
  );
  assert.equal(signIn.status, 400);
  assert.equal(
    ((await readJson(signIn)) as { code: string }).code,
    "EMAIL_PASSWORD_DISABLED",
  );

  const signUp = await POST(
    postJson("/sign-up/email", {
      email: "probe@example.com",
      password: "a-password-1",
      name: "Probe",
    }),
  );
  assert.equal(signUp.status, 400);
  assert.equal(
    ((await readJson(signUp)) as { code: string }).code,
    "EMAIL_PASSWORD_SIGN_UP_DISABLED",
  );
});

test("a sign-in endpoint does not answer GET, and an unknown path is a 404", async () => {
  for (const path of ["/sign-in/magic-link", "/sign-in/license-key", "/nope"]) {
    const response = await GET(new Request(`${BASE}${path}`));
    assert.equal(response.status, 404, path);
  }
});
