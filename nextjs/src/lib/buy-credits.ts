// Relative, not the `@/` alias: `tests/*.test.ts` imports this module directly
// under `tsx`, and a sibling import that needs no path mapping cannot break
// there (`auth-license-key-plugin.ts:6-7` does the same).
import { isRecord } from "./type-guards";

/**
 * The endpoint the dashboard's buy-credits button posts to. It is the same
 * route the public `/credits` form uses, and it answers a refusal with
 * `{ error: "..." }` and a 4xx/5xx status on six separate paths — see
 * `app/api/checkout/credits/route.ts`.
 */
export const BUY_CREDITS_ENDPOINT = "/api/checkout/credits";

/**
 * The single name every report for this flow carries. It is the thing #737
 * says the old `console.error("Failed to create checkout:", err)` was missing,
 * and it is the only key a dashboard can group a lost sale by.
 */
export const BUY_CREDITS_OPERATION = "buy_credits";

/**
 * The slice of the request the seam builds. Declared here rather than reused
 * from the DOM's `RequestInit` so a test can assert on all three fields
 * without a cast, and so the seam stays free of anything browser-shaped. It
 * IS assignable to `RequestInit`, so the component can still hand this
 * straight to the global `fetch`.
 */
export interface BuyCreditsRequestInit {
  method: string;
  headers: Record<string, string>;
  body: string;
}

/**
 * The slice of the response the seam reads — the three members, and nothing
 * more. A real `Response` is assignable to it, and so is a plain object
 * literal in a test whose `json()` returns the body synchronously.
 *
 * `json` returns `unknown` on purpose: the real one returns `Promise<any>`,
 * which would otherwise spread `any` through the whole failing branch and
 * silently erase the `typeof data.error === "string"` guard below.
 */
export interface BuyCreditsResponse {
  ok: boolean;
  status: number;
  json: () => unknown;
}

export type BuyCreditsFetch = (
  input: string,
  init: BuyCreditsRequestInit,
) => Promise<BuyCreditsResponse>;

export interface BuyCreditsRequest {
  /** A key is required HERE. The null case is the factory's job. */
  licenseKey: string;
  amount: number;
  fetchImpl: BuyCreditsFetch;
  navigate: (destination: string) => void;
  onError: (message: string) => void;
  reportError: (error: unknown, properties: Record<string, unknown>) => void;
  /** Translated `buyCredits.errorCheckout` — the refusal fallback. */
  checkoutErrorMessage: string;
  /** Translated `buyCredits.errorGeneric` — the network fallback. */
  genericErrorMessage: string;
}

/**
 * Creates a credit checkout session and navigates to it ONLY when the server
 * actually made one.
 *
 * This is the whole of #737. The old handler read `data.checkoutUrl` and did
 * nothing at all when it was absent: no message, no report, just a spinner
 * that stopped. A 400 ("Amount too large") and a 500 both looked to the
 * customer exactly like a button that does not work, and no record of the
 * lost sale existed anywhere.
 *
 * Returns whether it navigated. That boolean is the entire busy-flag
 * decision — `true` means the document is on its way to Stripe and nothing
 * should be re-armed, `false` means the customer is still on the dashboard
 * and the tier buttons have to come back to life. It never touches React
 * state itself; `createBuyCreditsHandler` owns that.
 *
 * No browser global appears in this file, and none must ever be added — a
 * test asserts on the source text itself. A navigation assigned directly in
 * here, rather than handed out through the injected `navigate`, would land
 * somewhere no test in this repo can observe.
 */
export async function buyCreditsAndRedirect({
  licenseKey,
  amount,
  fetchImpl,
  navigate,
  onError,
  reportError,
  checkoutErrorMessage,
  genericErrorMessage,
}: BuyCreditsRequest): Promise<boolean> {
  let response: BuyCreditsResponse;

  // The try wraps the REQUEST and nothing else. A broader one would also
  // catch a throw from `navigate`, from `onError` or from `reportError`
  // below, and then report the failure a second time down the network path
  // with a message about a network that was never at fault.
  try {
    response = await fetchImpl(BUY_CREDITS_ENDPOINT, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ licenseKey, amount }),
    });
  } catch (thrown) {
    // Deliberate: the developer still gets the throw itself, which never
    // reaches the customer.
    // eslint-disable-next-line no-console
    console.error(
      `[${BUY_CREDITS_OPERATION}] The checkout request threw. ` +
        `No checkout session was created.`,
      thrown,
    );
    onError(genericErrorMessage);
    reportError(thrown, { operation: BUY_CREDITS_OPERATION });

    return false;
  }

  // `json()` REJECTS on a body that is not JSON, and a 500 from a proxy or an
  // unhandled route throw is usually an HTML page. `CreditsPurchase.tsx:95`
  // lets that rejection fall into its network `catch`, so a server fault is
  // shown to the customer as "Something went wrong" with no status recorded.
  // Reading it here instead keeps the failing branch below in charge, with
  // the real status attached.
  let data: unknown = null;

  try {
    data = await response.json();
  } catch {
    data = null;
  }

  if (response.ok && isRecord(data) && typeof data.checkoutUrl === "string") {
    navigate(data.checkoutUrl);

    return true;
  }

  // `response.ok &&` is load-bearing and not decoration. The route answers a
  // refusal with a JSON body, and a body that carries BOTH an `error` and a
  // stale `checkoutUrl` would otherwise send the customer to a checkout
  // Stripe has already refused.

  // Deliberate: the status is for the developer. It is not in the copy the
  // customer reads.
  // eslint-disable-next-line no-console
  console.error(
    `[${BUY_CREDITS_OPERATION}] The server refused the checkout: ` +
      `${response.status}. No checkout session was created.`,
  );

  // The server's own `error` string IS shown — unlike #870's sign-out copy,
  // this one is written by our own route for this customer ("Amount too
  // large", "Insufficient credits"), and the issue's Done-when pins it on
  // screen verbatim. An EMPTY string is the trap: it is a `string`, it
  // passes `typeof`, and it would paint an empty alert region that reads as
  // a button that still does nothing (`sign-out.ts:50-55`). So it falls
  // through to the translated copy.
  onError(
    isRecord(data) && typeof data.error === "string" && data.error !== ""
      ? data.error
      : checkoutErrorMessage,
  );

  // The properties are built HERE so a test can assert on what is NOT in
  // them. `licenseKey` is a credential (#737, #739), and this app's PostHog
  // init has no `before_send` and no redaction, so anything passed ships
  // verbatim. Never add the licence key, the request body, or `data`.
  reportError(
    new Error(`${BUY_CREDITS_OPERATION} failed: ${response.status}`),
    {
      operation: BUY_CREDITS_OPERATION,
      status: response.status,
    },
  );

  return false;
}

/**
 * The tier a checkout is in flight for: the dollar amount of a preset, or the
 * `"custom"` sentinel for the free-entry box. `null` is "nothing in flight".
 */
export type BuyCreditsTier = number | "custom";

export interface BuyCreditsHandlerRequest {
  /**
   * Nullable here, and only here. A dashboard customer with no active licence
   * has no wallet to top up, so the handler refuses before it touches state.
   */
  licenseKey: string | null;
  fetchImpl: BuyCreditsFetch;
  navigate: (destination: string) => void;
  reportError: (error: unknown, properties: Record<string, unknown>) => void;
  setBusy: (tier: BuyCreditsTier | null) => void;
  setError: (message: string | null) => void;
  checkoutErrorMessage: string;
  genericErrorMessage: string;
}

/**
 * Builds the click handler `CloudCreditsCard` hands to its tier buttons.
 *
 * Every decision the button makes lives HERE, not in the component, because a
 * component in this repo is reachable only through `renderToStaticMarkup` —
 * no click can be dispatched, so a rule written inline in the component is a
 * rule nothing can test. The component's only remaining job is to CALL this
 * in its render body and wire the result to `onClick`, which a static render
 * can prove.
 *
 * The busy flag is asymmetric on purpose (#881 review round 1, finding 1, and
 * `sign-out.ts:93-101`). `navigate` assigns the document's `location.href`,
 * which only SCHEDULES a navigation: the document stays live and interactive for the
 * whole page load that follows. The card's old `finally { setLoadingTier(null) }`
 * cleared it unconditionally, which re-armed every tier button during the
 * Stripe redirect and invited a second checkout session — a second charge —
 * for the same top-up. So busy is cleared on the failure paths ONLY.
 */
export function createBuyCreditsHandler({
  licenseKey,
  fetchImpl,
  navigate,
  reportError,
  setBusy,
  setError,
  checkoutErrorMessage,
  genericErrorMessage,
}: BuyCreditsHandlerRequest): (
  amount: number,
  tier?: BuyCreditsTier,
) => Promise<void> {
  return async function handleBuyCredits(
    amount: number,
    tier: BuyCreditsTier = amount,
  ): Promise<void> {
    // Before any state is touched: a spinner that starts for a checkout that
    // was never attempted is the same silence #737 is about.
    if (!licenseKey) return;

    // A second click must not sit next to the previous click's failure
    // (`sign-out.ts:110`).
    setError(null);
    setBusy(tier);

    const navigated = await buyCreditsAndRedirect({
      licenseKey,
      amount,
      fetchImpl,
      navigate,
      onError: setError,
      reportError,
      checkoutErrorMessage,
      genericErrorMessage,
    });

    // Only when we are staying on this page. See the asymmetry note above.
    if (!navigated) setBusy(null);
  };
}
