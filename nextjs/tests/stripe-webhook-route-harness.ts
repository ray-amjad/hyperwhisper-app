/**
 * Test harness for `app/api/webhooks/stripe/route.ts` — the HTTP door Stripe
 * knocks on when money moves.
 *
 * This is the ROUTE, not the service behind it. The service
 * (`lib/services/stripe-webhook.ts`) already has its own harness and its own
 * suite. The route owns a different job, and that job was untested: it decides
 * whether a request is authentic at all (the HMAC gate), whether the session
 * is paid, which of the three handlers receives it, and what status a handler
 * fault turns into. A wrong answer on any of those either drops a paid
 * purchase or accepts a forged one.
 *
 * The route reaches two collaborators at import time: the Stripe client
 * (`lib/clients/stripe.ts`, for `webhooks.constructEvent`) and the three
 * handlers in `lib/services/stripe-webhook.ts`. Both are replaced here with
 * `mock.module`, so the tests run the REAL route module with no Stripe
 * account, no database and no network.
 *
 * Mocking at the module boundary is what the repo's CLAUDE.md asks for: the
 * signature check stays in the source and no test key and no bypass is added
 * to it. The harness does not weaken the gate — it stands in for Stripe's own
 * verifier and is told, per test, to accept or to refuse.
 *
 * The mocks are installed when this module is evaluated, and `loadRoute()` is
 * the only way the tests reach the route. A test file that imports the route
 * path itself would bind to the real collaborators, so do not.
 */
import { mock } from "node:test";

import { NextRequest } from "next/server";

import { formatLogArgs } from "./db-error-fixture";

export interface ConstructEventCall {
  body: string;
  signature: string;
  secret: string;
}

export interface CreditPurchaseCall {
  session: Record<string, unknown>;
  eventId: string;
  eventType: string;
}

/** Everything the route asked its collaborators to do, in call order. */
export const calls = {
  constructEvent: [] as ConstructEventCall[],
  licensePurchase: [] as Record<string, unknown>[],
  creditPurchase: [] as CreditPurchaseCall[],
  chargeRefunded: [] as Record<string, unknown>[],
};

/** What the collaborators answer. Each test sets only what it cares about. */
export const behaviour = {
  /**
   * Event the stand-in verifier returns. `null` means the signature did not
   * check out, so the verifier throws — exactly as Stripe's own does.
   */
  verifiedEvent: null as Record<string, unknown> | null,
  /** Message the verifier throws with when `verifiedEvent` is null. */
  verifyError: "No signatures found matching the expected signature for payload",
  /** Thrown by `handleLicensePurchase` when set. */
  licenseError: null as unknown,
  /** Thrown by `handleCreditPurchase` when set. */
  creditError: null as unknown,
  /** Thrown by `handleChargeRefunded` when set. */
  refundError: null as unknown,
};

export function resetHarness(): void {
  calls.constructEvent.length = 0;
  calls.licensePurchase.length = 0;
  calls.creditPurchase.length = 0;
  calls.chargeRefunded.length = 0;

  behaviour.verifiedEvent = null;
  behaviour.verifyError =
    "No signatures found matching the expected signature for payload";
  behaviour.licenseError = null;
  behaviour.creditError = null;
  behaviour.refundError = null;
}

/**
 * A `checkout.session.completed`-shaped event. Only the fields the route reads
 * are set: the id, the type, the payment status and the purchase metadata.
 */
export function checkoutEvent(
  overrides: {
    id?: string;
    type?: string;
    sessionId?: string;
    paymentStatus?: string;
    purchaseType?: string | undefined;
    metadata?: Record<string, string> | undefined;
  } = {},
): Record<string, unknown> {
  const metadata =
    "metadata" in overrides
      ? overrides.metadata
      : overrides.purchaseType === undefined
        ? {}
        : { purchase_type: overrides.purchaseType };

  return {
    id: overrides.id ?? "evt_1",
    type: overrides.type ?? "checkout.session.completed",
    data: {
      object: {
        id: overrides.sessionId ?? "cs_1",
        payment_status: overrides.paymentStatus ?? "paid",
        metadata,
      },
    },
  };
}

/** A `charge.refunded`-shaped event. */
export function refundEvent(
  overrides: { id?: string; chargeId?: string } = {},
): Record<string, unknown> {
  return {
    id: overrides.id ?? "evt_refund_1",
    type: "charge.refunded",
    data: { object: { id: overrides.chargeId ?? "ch_1", amount_refunded: 500 } },
  };
}

/**
 * The route logs on every branch, including the success ones. Swallow it so
 * the run stays readable, and keep the lines so a failing test can still be
 * diagnosed — and so a test can assert what was logged.
 */
export const logLines: string[] = [];

const realConsole = { error: console.error, log: console.log };

export function silenceRouteLogging(): void {
  const capture =
    (level: string) =>
    (...args: unknown[]): void => {
      logLines.push(`${level} ${formatLogArgs(args)}`);
    };
  console.error = capture("error");
  console.log = capture("log");
}

export function restoreRouteLogging(): void {
  console.error = realConsole.error;
  console.log = realConsole.log;
}

function moduleUrl(relative: string): string {
  return new URL(relative, import.meta.url).href;
}

/**
 * `mock.module` is a real Node 22 API (behind `--experimental-test-module-mocks`,
 * which `npm test` passes) but this repo pins `@types/node@20`, which has no
 * declaration for it. Narrow the tracker to the one method used here rather
 * than bumping the types in a test-only change.
 */
interface ModuleMocker {
  module(
    specifier: string,
    options: { namedExports: Record<string, unknown> },
  ): void;
}

const moduleMock = mock as unknown as ModuleMocker;

moduleMock.module(moduleUrl("../lib/clients/stripe.ts"), {
  namedExports: {
    stripe: {
      webhooks: {
        constructEvent: (
          body: string,
          signature: string,
          secret: string,
        ): Record<string, unknown> => {
          calls.constructEvent.push({ body, signature, secret });
          if (!behaviour.verifiedEvent) {
            throw new Error(behaviour.verifyError);
          }
          return behaviour.verifiedEvent;
        },
      },
    },
  },
});

moduleMock.module(moduleUrl("../lib/services/stripe-webhook.ts"), {
  namedExports: {
    handleLicensePurchase: async (
      session: Record<string, unknown>,
    ): Promise<void> => {
      calls.licensePurchase.push(session);
      if (behaviour.licenseError) throw behaviour.licenseError;
    },
    handleCreditPurchase: async (
      session: Record<string, unknown>,
      eventId: string,
      eventType: string,
    ): Promise<void> => {
      calls.creditPurchase.push({ session, eventId, eventType });
      if (behaviour.creditError) throw behaviour.creditError;
    },
    handleChargeRefunded: async (
      charge: Record<string, unknown>,
    ): Promise<void> => {
      calls.chargeRefunded.push(charge);
      if (behaviour.refundError) throw behaviour.refundError;
    },
  },
});

/** Builds the POST Stripe sends: a raw JSON body plus the signature header. */
export function webhookRequest(
  body: unknown,
  headers: Record<string, string> = {},
): NextRequest {
  return new NextRequest("https://hyperwhisper.test/api/webhooks/stripe", {
    method: "POST",
    headers: { "content-type": "application/json", ...headers },
    body: typeof body === "string" ? body : JSON.stringify(body),
  });
}

/**
 * Loads the route AFTER the mocks above are installed, so it binds to them.
 * Tests call this, never `import` the route path directly.
 */
export const loadStripeWebhookRoute = () =>
  import("@/app/api/webhooks/stripe/route");
