/**
 * customer.stripePortalUrl over a real tRPC HTTP request, when Stripe refuses
 * to open the billing portal (#2082).
 *
 * The dashboard's BillingCard prints `error.message` as is, and the tRPC
 * `errorFormatter` only rewrites a DB error's 5xx message. So whatever message
 * the procedure throws is what the customer reads. A Stripe error's message is
 * developer text (it names the customer id and the key mode); the customer must
 * get fixed copy, and the Stripe detail must still reach the server log.
 *
 * Better Auth, the database layer, the Stripe client and the validated env are
 * replaced with `mock.module`; the fetch adapter, superjson, the context
 * factory, the auth middleware, the error formatter and the router run for real.
 */
import assert from "node:assert/strict";
import { afterEach, mock, test } from "node:test";

import { fetchRequestHandler } from "@trpc/server/adapters/fetch";
import superjson from "superjson";

const STRIPE_RAW =
  "No such customer: 'cus_portalerror'; a similar object exists in live mode, but a test mode key was used to make this request.";

/** What the fake Stripe client does on the next portal session call. */
const behaviour = {
  portalError: null as Error | null,
  portalUrl: "https://billing.stripe.test/session/bps_ok",
};

function moduleUrl(relative: string): string {
  return new URL(relative, import.meta.url).href;
}

// `mock.module` is real in Node 22 (behind --experimental-test-module-mocks,
// which `npm test` passes) but the pinned @types/node@20 does not declare it.
const moduleMock = mock as unknown as {
  module(
    specifier: string,
    options: { namedExports: Record<string, unknown> },
  ): void;
};

moduleMock.module(moduleUrl("../src/lib/auth.ts"), {
  namedExports: {
    auth: {
      api: {
        getSession: async () => ({
          user: {
            id: "user_portal_error",
            email: "portal-error@example.test",
            role: "user",
          },
        }),
      },
    },
    getPortalSession: async () => null,
  },
});

moduleMock.module(moduleUrl("../src/lib/db-layer.ts"), {
  namedExports: {
    getAccountKeysByEmail: async () => [
      {
        id: "key_row_portal_error",
        key: "HW-TEST-PORT-ALER",
        email: "portal-error@example.test",
        userId: "user_portal_error",
        status: "active",
        polarLicenseKeyId: null,
        polarCustomerId: null,
        stripeCustomerId: "cus_portalerror",
        stripeSessionId: null,
        createdAt: new Date("2026-01-01T00:00:00Z"),
      },
    ],
    getCreditBalancesForUsers: async () => new Map(),
    getPaidCreditGrantsForUsers: async () => [],
  },
});

moduleMock.module(moduleUrl("../src/env/server.mjs"), {
  namedExports: { env: {} },
});

moduleMock.module(moduleUrl("../lib/clients/stripe.ts"), {
  namedExports: {
    stripe: {
      billingPortal: {
        sessions: {
          create: async () => {
            if (behaviour.portalError) throw behaviour.portalError;
            return { url: behaviour.portalUrl };
          },
        },
      },
    },
  },
});

/** A Stripe SDK error the way the SDK throws one for a rejected customer. */
function stripeInvalidRequestError(): Error {
  const err = new Error(STRIPE_RAW) as Error & Record<string, unknown>;
  err.type = "StripeInvalidRequestError";
  err.statusCode = 400;
  err.code = "resource_missing";
  return err;
}

/** POSTs the mutation through the real fetch adapter, as the browser does. */
async function callStripePortalUrl() {
  const { customerRouter } = await import("@/server/api/routers/customer");
  const { createTRPCContext } = await import("@/server/api/trpc");

  const req = new Request("http://localhost:3000/api/trpc/stripePortalUrl", {
    method: "POST",
    headers: {
      "content-type": "application/json",
      cookie: "hw-session=opaque",
    },
    body: JSON.stringify({ json: null }),
  });
  const res = await fetchRequestHandler({
    endpoint: "/api/trpc",
    req,
    router: customerRouter,
    createContext: () => createTRPCContext({ headers: req.headers }),
  });
  return {
    status: res.status,
    body: (await res.json()) as Record<string, unknown>,
  };
}

/** Captures console.error for one call, rendered as Node would print it. */
function captureConsoleError() {
  const original = console.error;
  const lines: string[] = [];
  console.error = (...args: unknown[]) => {
    lines.push(
      args
        .map((a) =>
          a instanceof Error ? `${a.message} ${JSON.stringify(a)}` : String(a),
        )
        .join(" "),
    );
  };
  return {
    lines,
    restore() {
      console.error = original;
    },
  };
}

afterEach(() => {
  behaviour.portalError = null;
});

test("a Stripe failure in stripePortalUrl reaches the customer as fixed copy, not Stripe's text", async () => {
  const { STRIPE_PORTAL_ERROR_MESSAGE } = await import(
    "@/server/api/routers/customer"
  );
  behaviour.portalError = stripeInvalidRequestError();

  const log = captureConsoleError();
  let result;
  try {
    result = await callStripePortalUrl();
  } finally {
    log.restore();
  }

  const error = superjson.deserialize(
    result.body.error as Parameters<typeof superjson.deserialize>[0],
  ) as { message: string; data: { code: string; httpStatus: number } };

  assert.equal(result.status, 500);
  assert.equal(error.data.code, "INTERNAL_SERVER_ERROR");
  assert.equal(error.message, STRIPE_PORTAL_ERROR_MESSAGE);
  assert.doesNotMatch(
    JSON.stringify(result.body),
    /No such customer|cus_|test mode key/,
    "raw Stripe API text is sent to the customer",
  );
  // The operator still sees why Stripe refused.
  assert.ok(
    log.lines.some(
      (line) => line.includes(STRIPE_RAW) && line.includes("resource_missing"),
    ),
    `Stripe's error is not in the server log: ${JSON.stringify(log.lines)}`,
  );
});

test("stripePortalUrl still returns the portal URL when Stripe opens a session", async () => {
  const result = await callStripePortalUrl();

  assert.equal(result.status, 200);
  const data = superjson.deserialize(
    (
      result.body.result as {
        data: Parameters<typeof superjson.deserialize>[0];
      }
    ).data,
  );
  assert.deepEqual(data, { url: behaviour.portalUrl });
});
