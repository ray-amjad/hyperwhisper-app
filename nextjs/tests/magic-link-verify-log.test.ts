/**
 * #2638: every `GET /api/auth/magic-link/verify` writes exactly ONE structured
 * `auth.magic_link_verify` line, in the shape `logSignIn` writes for the
 * Account Key path, and that line never carries the email address or the
 * token.
 *
 * The handler is the REAL Better Auth handler built from `src/lib/auth.ts`'s
 * own options (its `magicLink` plugin, its `sendMagicLink`, its `hooks` and
 * `databaseHooks`), as `magic-link-dead-link-message.test.ts` builds it. Only
 * the database is swapped for Better Auth's memory adapter (wrapped so a test
 * can make a create fail) and the Resend client is stubbed so the emailed link
 * can be read back.
 */
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import test from "node:test";

import { memoryAdapter } from "better-auth/adapters/memory";

import { captureConsole, moduleMock, moduleUrl } from "./module-mock";

const ORIGIN = "http://localhost:3000";
const CALLBACK = "/en/user/licenses";
const ERROR_CALLBACK = "/en/user/sign-in?returnTo=%2Fen%2Fuser%2Flicenses";

process.env.SKIP_ENV_VALIDATION = "1";
process.env.BETTER_AUTH_URL = ORIGIN;
process.env.BETTER_AUTH_SECRET =
  "test-secret-for-2638-magic-link-verify-log-0123456789";

// ---------------------------------------------------------------------------
// Collaborator stubs: Resend (to read the emailed link) and `sha256Hex` (real
// digest, except it throws on one sentinel input so a test can fault the log
// line itself).
// ---------------------------------------------------------------------------

const sentEmails: Array<{ text?: string }> = [];

moduleMock.module(moduleUrl("../lib/clients/resend.ts"), {
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

const HASH_FAULT_IP = "198.51.100.99";

function realSha256Hex(value: string): string {
  return createHash("sha256").update(value, "utf8").digest("hex");
}

moduleMock.module(moduleUrl("../lib/shared/sha256.ts"), {
  namedExports: {
    sha256Hex: (value: string): string => {
      if (value === HASH_FAULT_IP) throw new Error("sha256 fault for #2638");
      return realSha256Hex(value);
    },
  },
});

// ---------------------------------------------------------------------------
// The handler.
// ---------------------------------------------------------------------------

type Row = Record<string, unknown>;
type Handler = (req: Request) => Promise<Response>;

interface Harness {
  handler: Handler;
  store: Record<string, Row[]>;
  /** A model name here makes the adapter's `create` for it return null. */
  failCreate: Set<string>;
}

async function realAuthOnMemory(
  overrides: Record<string, unknown> = {},
): Promise<Harness> {
  const { betterAuth } = await import("better-auth");
  const { auth } = await import("../src/lib/auth");
  const store: Record<string, Row[]> = {
    user: [],
    session: [],
    account: [],
    verification: [],
  };
  const failCreate = new Set<string>();
  const memory = memoryAdapter(store);
  const database = (options: Parameters<typeof memory>[0]) => {
    const adapter = memory(options);

    return {
      ...adapter,
      create: (async (args: { model: string }) =>
        failCreate.has(args.model)
          ? null
          : adapter.create(args as never)) as typeof adapter.create,
    };
  };
  const instance = betterAuth({
    ...auth.options,
    database,
    // The app's limiter is Upstash-backed; it is not what this file tests.
    rateLimit: { enabled: false },
    ...overrides,
  });

  return { handler: instance.handler, store, failCreate };
}

async function requestLink(h: Harness, email: string): Promise<string> {
  const before = sentEmails.length;
  const res = await h.handler(
    new Request(`${ORIGIN}/api/auth/sign-in/magic-link`, {
      method: "POST",
      headers: { "content-type": "application/json", origin: ORIGIN },
      body: JSON.stringify({
        email,
        callbackURL: CALLBACK,
        errorCallbackURL: ERROR_CALLBACK,
      }),
    }),
  );

  assert.equal(res.status, 200, `send failed: ${await res.text()}`);
  const link = /https?:\/\/\S+\/magic-link\/verify\?\S+/.exec(
    sentEmails[before]?.text ?? "",
  )?.[0];

  assert.ok(link, "no verify link in the email body");

  return link;
}

function tokenOf(link: string): string {
  const token = new URL(link).searchParams.get("token");

  assert.ok(token, "the link has no token");

  return token;
}

function unknownTokenLink(): string {
  const url = new URL(`${ORIGIN}/api/auth/magic-link/verify`);

  url.searchParams.set("token", "unknown-token-for-2638-test");
  url.searchParams.set("callbackURL", CALLBACK);
  url.searchParams.set("errorCallbackURL", ERROR_CALLBACK);

  return url.toString();
}

interface VerifyLine {
  event: string;
  outcome: string;
  reason?: string;
  userId?: string;
  newUser?: boolean;
  ipHash: string | null;
}

/** Opens `link` and returns the response plus every console line it wrote. */
async function open(
  h: Harness,
  link: string,
  ip = "203.0.113.7, 10.0.0.1",
): Promise<{ res: Response; lines: string[] }> {
  const capture = captureConsole(["log", "warn", "error"]);

  try {
    const res = await h.handler(
      new Request(link, {
        redirect: "manual",
        headers: { "x-forwarded-for": ip },
      }),
    );

    return { res, lines: capture.lines };
  } finally {
    capture.restore();
  }
}

/**
 * Asserts exactly one `auth.magic_link_verify` line, at `level`, carrying
 * neither the address nor the token (nor does any other line the request
 * wrote), and returns it parsed.
 */
function theOneLine(
  lines: string[],
  level: "log" | "warn" | "error",
  secrets: string[],
): VerifyLine {
  const ours = lines.filter((l) => l.includes('"auth.magic_link_verify"'));

  assert.equal(ours.length, 1, `expected one verify line, got ${ours.length}`);
  assert.ok(
    ours[0].startsWith(`${level} `),
    `logged at the wrong level: ${ours[0]}`,
  );
  for (const line of lines) {
    for (const secret of secrets) {
      assert.ok(!line.includes(secret), `a log line leaked ${secret}: ${line}`);
    }
  }

  return JSON.parse(ours[0].slice(level.length + 1)) as VerifyLine;
}

const IP_HASH = realSha256Hex("203.0.113.7").slice(0, 12);

// ---------------------------------------------------------------------------
// The three Done-when cases.
// ---------------------------------------------------------------------------

test("#2638: a valid link logs ok with the userId, and newUser only on the first sign-in", async () => {
  const h = await realAuthOnMemory();
  const email = "verify-log-valid@example.com";

  const first = await requestLink(h, email);
  const opened = await open(h, first);

  assert.equal(opened.res.status, 302);
  const line = theOneLine(opened.lines, "log", [email, tokenOf(first)]);
  const user = h.store.user.find((u) => u.email === email);

  assert.ok(user, "the first sign-in did not create the user");
  assert.deepEqual(line, {
    event: "auth.magic_link_verify",
    outcome: "ok",
    userId: user.id,
    newUser: true,
    ipHash: IP_HASH,
  });

  // A second link for the same address signs the existing user in.
  const second = await requestLink(h, email);
  const again = await open(h, second);

  assert.deepEqual(theOneLine(again.lines, "log", [email, tokenOf(second)]), {
    event: "auth.magic_link_verify",
    outcome: "ok",
    userId: user.id,
    newUser: false,
    ipHash: IP_HASH,
  });
});

test("#2638: a reused link logs rejected / INVALID_TOKEN", async () => {
  const h = await realAuthOnMemory();
  const email = "verify-log-reused@example.com";
  const link = await requestLink(h, email);

  await open(h, link);
  const reused = await open(h, link);

  assert.equal(reused.res.status, 302);
  assert.deepEqual(theOneLine(reused.lines, "warn", [email, tokenOf(link)]), {
    event: "auth.magic_link_verify",
    outcome: "rejected",
    reason: "INVALID_TOKEN",
    ipHash: IP_HASH,
  });
});

test("#2638: an unknown token logs rejected / INVALID_TOKEN", async () => {
  const h = await realAuthOnMemory();
  const link = unknownTokenLink();
  const opened = await open(h, link);

  assert.equal(opened.res.status, 302);
  assert.deepEqual(theOneLine(opened.lines, "warn", [tokenOf(link)]), {
    event: "auth.magic_link_verify",
    outcome: "rejected",
    reason: "INVALID_TOKEN",
    ipHash: IP_HASH,
  });
});

// ---------------------------------------------------------------------------
// Server-side failures, and the edges of the classification.
// ---------------------------------------------------------------------------

for (const [model, reason] of [
  ["session", "failed_to_create_session"],
  ["user", "failed_to_create_user"],
] as const) {
  test(`#2638: ${reason} logs error at console.error`, async () => {
    const h = await realAuthOnMemory();
    const email = `verify-log-${model}@example.com`;
    const link = await requestLink(h, email);

    h.failCreate.add(model);
    const opened = await open(h, link);

    assert.equal(opened.res.status, 302);
    assert.equal(
      new URL(
        opened.res.headers.get("location") ?? "",
        ORIGIN,
      ).searchParams.get("error"),
      reason,
    );
    assert.deepEqual(
      theOneLine(opened.lines, "error", [email, tokenOf(link)]),
      {
        event: "auth.magic_link_verify",
        outcome: "error",
        reason,
        ipHash: IP_HASH,
      },
    );
  });
}

test("#2638: a refused callback origin logs rejected with its code, no IP header gives a null ipHash", async () => {
  const h = await realAuthOnMemory();
  const url = new URL(unknownTokenLink());

  url.searchParams.set("callbackURL", "https://attacker.example/steal");
  const capture = captureConsole(["log", "warn", "error"]);
  let res: Response;

  try {
    res = await h.handler(new Request(url, { redirect: "manual" }));
  } finally {
    capture.restore();
  }

  assert.equal(res.status, 403);
  // Only OUR line is checked for the URL here: Better Auth's own logger
  // already prints the refused callback URL (pre-existing, not this hook).
  const line = theOneLine(capture.lines, "warn", []);

  assert.ok(!JSON.stringify(line).includes("attacker.example"));
  assert.deepEqual(line, {
    event: "auth.magic_link_verify",
    outcome: "rejected",
    reason: "INVALID_CALLBACK_URL",
    ipHash: null,
  });
});

test("#2638: a fault inside the log line does not break the sign-in", async () => {
  const h = await realAuthOnMemory();
  const email = "verify-log-fault@example.com";
  const link = await requestLink(h, email);
  const opened = await open(h, link, HASH_FAULT_IP);

  assert.equal(opened.res.status, 302);
  assert.equal(
    new URL(opened.res.headers.get("location") ?? "", ORIGIN).pathname,
    CALLBACK,
  );
  assert.match(opened.res.headers.get("set-cookie") ?? "", /session_token=/);
  assert.deepEqual(opened.lines, [
    'error {"event":"auth.magic_link_verify_log_failed"}',
  ]);
});

// ---------------------------------------------------------------------------
// The hook only logs: status, Location and cookie names are what the same
// options produce with no `hooks` at all.
// ---------------------------------------------------------------------------

test("#2638: the verify responses are unchanged by the log hook", async () => {
  async function responses(h: Harness, email: string) {
    const link = await requestLink(h, email);
    const shape = (res: Response) => ({
      status: res.status,
      location: res.headers.get("location"),
      cookies: (res.headers.get("set-cookie") ?? "")
        .split(/,(?=\s*[\w.-]+=)/)
        .map((c) => c.split("=")[0].trim())
        .filter(Boolean)
        .sort(),
    });

    return {
      ok: shape((await open(h, link)).res),
      reused: shape((await open(h, link)).res),
      unknown: shape((await open(h, unknownTokenLink())).res),
    };
  }

  const withHook = await responses(
    await realAuthOnMemory(),
    "verify-log-shape@example.com",
  );
  const withoutHook = await responses(
    await realAuthOnMemory({ hooks: undefined }),
    "verify-log-shape@example.com",
  );

  assert.deepEqual(withHook, withoutHook);
  assert.equal(withHook.ok.status, 302);
  assert.equal(withHook.ok.location, `${ORIGIN}${CALLBACK}`);
  assert.ok(withHook.ok.cookies.some((c) => c.endsWith("session_token")));
  assert.equal(
    withHook.reused.location,
    `${ORIGIN}/en/user/sign-in?returnTo=%2Fen%2Fuser%2Flicenses&error=INVALID_TOKEN`,
  );
  assert.equal(withHook.unknown.location, withHook.reused.location);
});

test("#2638: a successful send logs one ok line with the recipient hash only", async () => {
  const h = await realAuthOnMemory();
  const email = "Verify-Log-Send@Example.com";
  const capture = captureConsole(["log", "warn", "error"]);

  try {
    await requestLink(h, email);
  } finally {
    capture.restore();
  }

  assert.deepEqual(capture.lines, [
    `log ${JSON.stringify({
      event: "auth.magic_link_send",
      outcome: "ok",
      recipientHash: realSha256Hex(email.toLowerCase()).slice(0, 12),
    })}`,
  ]);
});
