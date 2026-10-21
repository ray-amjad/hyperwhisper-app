/**
 * #2013: server error tracking through the real `onRequestError` in
 * `instrumentation.ts` and the real Sentry SDK. Only Sentry's transport is
 * swapped, for one that records the envelopes it would have sent.
 *
 * One error escaping a route handler must reach Sentry as exactly ONE event,
 * and nothing in that event may carry an Account Key, an email address, the
 * request's URL, headers, cookies or IP, or a query's bound values.
 */
import assert from "node:assert/strict";
import { after, before, test } from "node:test";

import * as Sentry from "@sentry/nextjs";

import {
  LEAKY_EMAIL,
  LEAKY_KEY,
  LEAKY_SQL,
  leakyDbError,
} from "./db-error-fixture";

const COOKIE = "better-auth.session_token=s3ss10n-c00k1e";
const CLIENT_IP = "203.0.113.7";

type Envelope = { type?: string; payload: unknown }[];
const envelopes: Envelope[] = [];

function parseEnvelope(body: string | Uint8Array): Envelope {
  const text = typeof body === "string" ? body : new TextDecoder().decode(body);
  const lines = text.split("\n").filter((line) => line.length > 0);
  const items: Envelope = [];

  for (let i = 1; i + 1 < lines.length; i += 2) {
    items.push({
      type: JSON.parse(lines[i]!).type,
      payload: JSON.parse(lines[i + 1]!),
    });
  }

  return items;
}

function events(): Record<string, unknown>[] {
  return envelopes
    .flat()
    .filter((item) => item.type === "event")
    .map((item) => item.payload as Record<string, unknown>);
}

function leakyRequest() {
  return {
    path: `/api/license/validate?licenseKey=${LEAKY_KEY}&email=${encodeURIComponent(LEAKY_EMAIL)}`,
    method: "POST",
    headers: {
      authorization: `Bearer ${LEAKY_KEY}`,
      cookie: COOKIE,
      "x-forwarded-for": CLIENT_IP,
      "x-real-ip": CLIENT_IP,
    },
  };
}

const routeContext = {
  routerKind: "App Router" as const,
  routePath: "/api/license/validate",
  routeType: "route" as const,
  renderSource: undefined,
  revalidateReason: undefined,
  renderType: undefined,
};

const savedDsn = process.env.SENTRY_DSN;
const savedRuntime = process.env.NEXT_RUNTIME;
let config: typeof import("../sentry.server.config");
let instrumentation: typeof import("../instrumentation");

before(async () => {
  delete process.env.SENTRY_DSN;
  process.env.NEXT_RUNTIME = "nodejs";
  config = await import("../sentry.server.config");
  instrumentation = await import("../instrumentation");
});

after(async () => {
  await Sentry.close(2_000);
  if (savedDsn === undefined) delete process.env.SENTRY_DSN;
  else process.env.SENTRY_DSN = savedDsn;
  if (savedRuntime === undefined) delete process.env.NEXT_RUNTIME;
  else process.env.NEXT_RUNTIME = savedRuntime;
});

test("with no SENTRY_DSN, register starts nothing and onRequestError sends nothing", async () => {
  await instrumentation.register();
  assert.equal(config.isErrorTrackingEnabled(), false);
  assert.equal(Sentry.isInitialized(), false);
  await instrumentation.onRequestError(
    new Error("boom"),
    leakyRequest(),
    routeContext,
  );
  assert.equal(envelopes.length, 0);
});

test("a route-handler error sends exactly one event, with nothing private in it", async () => {
  process.env.SENTRY_DSN = "https://publickey@o0.ingest.sentry.io/0";
  const started = config.initServerErrorTracking({
    transport: (options) =>
      Sentry.createTransport(options, async (request) => {
        envelopes.push(parseEnvelope(request.body));

        return { statusCode: 200 };
      }),
  });

  assert.equal(started, true);

  // A real drizzle DrizzleQueryError wrapping a real pg error: its message is
  // the SQL plus the bound email and licence key.
  const error = leakyDbError();

  assert.ok(String(error.message).includes(LEAKY_EMAIL));
  await instrumentation.onRequestError(error, leakyRequest(), routeContext);
  await Sentry.flush(2_000);

  const sent = events();

  assert.equal(sent.length, 1);
  const serialized = JSON.stringify(envelopes);

  for (const secret of [
    LEAKY_KEY,
    LEAKY_EMAIL,
    encodeURIComponent(LEAKY_EMAIL),
    "s3ss10n-c00k1e",
    CLIENT_IP,
    "licenseKey=",
  ]) {
    assert.ok(!serialized.includes(secret), `event leaks ${secret}`);
  }

  const event = sent[0]!;

  assert.equal(event.request, undefined);
  assert.equal(event.user, undefined);
  assert.equal(event.server_name, undefined);
  assert.equal(event.breadcrumbs, undefined);
  assert.deepEqual(event.tags, {
    route: "/api/license/validate",
    routeType: "route",
    method: "POST",
  });
  assert.equal(event.transaction, "POST /api/license/validate");

  const values = (
    event.exception as {
      values: {
        type: string;
        value: string;
        stacktrace?: { frames: unknown[] };
      }[];
    }
  ).values;
  const top = values[values.length - 1]!;

  // The SQL (column names, `$n` placeholders) stays readable; the bound
  // values after `params:` do not.
  assert.ok(top.value.startsWith(`Failed query: ${LEAKY_SQL}`), top.value);
  assert.match(top.value, /params: \[redacted\]/);
  assert.ok((top.stacktrace?.frames.length ?? 0) > 0);
});

test("scrubMessage cuts keys, emails, quoted values, auth and Stripe secrets", () => {
  const out = config.scrubMessage(
    `user ${LEAKY_EMAIL} key hw-ab12-cd34-ef56-gh78 Bearer abc.def ` +
      `sk_live_abc123 whsec_xyz postgres://u:pw@db.example/x ` +
      `invalid input syntax for type uuid: "${LEAKY_KEY}" Key (email)=(${LEAKY_EMAIL}) exists`,
  );

  for (const secret of [
    LEAKY_EMAIL,
    LEAKY_KEY,
    "hw-ab12",
    "abc.def",
    "sk_live_abc123",
    "whsec_xyz",
    ":pw@",
  ]) {
    assert.ok(!out.includes(secret), `message leaks ${secret}: ${out}`);
  }
  assert.ok(out.includes("postgres://[redacted]@db.example/x"));
  assert.ok(out.includes("Key (email)=([redacted])"));
});

test("scrubEvent drops a tag, context or frame field outside the allow-list", () => {
  const out = config.scrubEvent({
    type: undefined,
    tags: { route: "/x", licenseKey: LEAKY_KEY },
    contexts: { runtime: { name: "node" }, os: { name: "linux" } },
    extra: { body: LEAKY_EMAIL },
    exception: {
      values: [
        {
          type: "Error",
          value: "x",
          stacktrace: {
            frames: [
              {
                filename: "app/api/x/route.ts",
                function: "POST",
                lineno: 3,
                vars: { email: LEAKY_EMAIL },
                context_line: LEAKY_KEY,
              },
            ],
          },
        },
      ],
    },
  });
  const serialized = JSON.stringify(out);

  assert.ok(!serialized.includes(LEAKY_KEY));
  assert.ok(!serialized.includes(LEAKY_EMAIL));
  assert.deepEqual(out.tags, { route: "/x" });
  assert.deepEqual(out.contexts, { runtime: { name: "node" } });
});
