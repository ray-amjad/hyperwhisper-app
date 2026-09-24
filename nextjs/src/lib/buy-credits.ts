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
 * The `stage` a report carries when the throw did NOT come from the checkout
 * request (#947 review round 1, finding 1).
 *
 * `buyCreditsAndRedirect` classifies a request that failed and reports it with
 * a `status`, or with no `status` at all when the request itself threw. A
 * throw from one of the OTHER collaborators — the navigation, the message
 * setter, or the reporter itself — is neither of those, and reporting it down
 * either of those paths would blame a network that was never at fault. It gets
 * this stage instead, so a dashboard can tell a lost sale from a broken
 * browser extension.
 */
export const BUY_CREDITS_HANDLER_STAGE = "handler";

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

  // The server's own `error` string is shown on a 4xx ONLY (#947 review
  // round 2, finding 3).
  //
  // Read `app/api/checkout/credits/route.ts` alongside this. Its 4xx bodies
  // are written for THIS customer about THIS request — "Amount too large",
  // "Invalid license key", "License is revoked", "License has no email
  // associated" — each of them a sentence that tells the customer what to do
  // next, and #737's `## Proposed fix` and `## Done when` both pin them on
  // screen verbatim. That is why they are shown, and unlike #870's sign-out
  // copy they are safe to show.
  //
  // Its 5xx body is a different animal entirely. `route.ts:218-227` answers
  // EVERY unhandled throw — a Stripe outage, a DB fault, a missing env var —
  // with the same `{ error: "Failed to create checkout session", details: <the
  // throw's own message> }`. Painting that verbatim into a 40-locale
  // `role="alert"` region gives every non-English customer an untranslated
  // English sentence that tells them nothing they can act on, and it is the
  // one body on this route whose wording is an internal fault description
  // rather than a message to a customer. So a 5xx takes the translated
  // `checkoutErrorMessage` and the raw string stays in the report and the
  // console line, where the developer reads it.
  //
  // An EMPTY string is the other trap, and it is unchanged: `""` is a
  // `string`, it passes `typeof`, and it would paint an empty alert region
  // that reads as a button that still does nothing (`sign-out.ts:50-55`). So
  // it falls through to the translated copy too.
  const serverMessage =
    isRecord(data) && typeof data.error === "string" ? data.error : "";

  onError(
    response.status < 500 && serverMessage !== ""
      ? serverMessage
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
  /**
   * Takes over the busy-flag release for the ONE exit this factory does not
   * run itself: a navigation that has been SCHEDULED and may still never
   * happen. See the asymmetry note on `createBuyCreditsHandler` and
   * `src/lib/abandoned-redirect.ts`, which is what the card passes here.
   *
   * It is injected rather than called directly because the release depends on
   * page-lifecycle events, and no such thing may appear in this file — a test
   * asserts on the source text itself.
   */
  onRedirectScheduled: (release: () => void) => void;
  checkoutErrorMessage: string;
  genericErrorMessage: string;
}

/**
 * Records a throw that escaped `buyCreditsAndRedirect` — which can only be a
 * collaborator failing, never a failed request.
 *
 * `buyCreditsAndRedirect` wraps the REQUEST and nothing else, on purpose: a
 * broader `try` there would catch a throw from the navigation, from `onError`
 * or from `reportError` and report the same failure a second time down the
 * network path. That reasoning is kept. The classification of a failed request
 * stays one concern; catching a collaborator is a different one, and it
 * belongs out here where it can be named for what it is.
 */
function reportHandlerFault(
  thrown: unknown,
  reportError: (error: unknown, properties: Record<string, unknown>) => void,
): void {
  // Deliberate: the developer gets the throw itself, which never reaches the
  // customer.
  // eslint-disable-next-line no-console
  console.error(
    `[${BUY_CREDITS_OPERATION}] A collaborator threw outside the checkout ` +
      `request. The busy flag is released anyway so the card is not left dead.`,
    thrown,
  );

  try {
    reportError(thrown, {
      operation: BUY_CREDITS_OPERATION,
      stage: BUY_CREDITS_HANDLER_STAGE,
    });
  } catch {
    // `reportError` is itself one of the collaborators that can be the
    // thrower — an ad-blocker that stubs `posthog.captureException` is the
    // reported case — so its own failure must not become the rejection this
    // whole path exists to prevent. The console line above is what is left.
  }
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
 * THE BUSY FLAG HAS EXACTLY ONE OWNER ON EVERY EXIT, AND NO EXIT HAS NONE.
 * That is the whole rule, and it is what #947 review round 1 findings 1 and 3
 * are two halves of.
 *
 * The flag is asymmetric on purpose (#881 review round 1, finding 1, and
 * `sign-out.ts:93-101`). `navigate` assigns the document's `location.href`,
 * which only SCHEDULES a navigation: the document stays live and interactive
 * for the whole page load that follows. The card's old
 * `finally { setLoadingTier(null) }` cleared it unconditionally, which re-armed
 * every tier button during the Stripe redirect and invited a second checkout
 * session — a second charge — for the same top-up. So this function must not
 * clear it there, and it does not.
 *
 * But "not here" is not "nowhere". Every exit below hands the release to
 * somebody:
 *
 * - a refused checkout, a thrown request, or a collaborator that threw: the
 *   customer is still on the dashboard, so the release runs right here;
 * - a scheduled navigation: the release is handed to `onRedirectScheduled`,
 *   which the card implements with the page-lifecycle watcher in
 *   `src/lib/abandoned-redirect.ts`. A navigation the customer abandons —
 *   Escape, an unreachable Stripe host, or Back with this document restored
 *   from the bfcache — therefore still re-arms the card. Before that, this
 *   branch simply dropped the flag on the floor and only a reload brought the
 *   buttons back.
 *
 * AND IT NEVER REJECTS. `CloudCreditsCard.tsx` calls this with `void` and no
 * `.catch`, which is correct for a click handler and fatal for a promise that
 * can reject: the customer would get an unhandled rejection AND a dead buy
 * block. A throw from the navigation, from `setError` or from `reportError`
 * (an ad-blocker that stubs `posthog.captureException` is enough) is caught,
 * named for what it is by `reportHandlerFault`, and the flag is released
 * anyway.
 */
export function createBuyCreditsHandler({
  licenseKey,
  fetchImpl,
  navigate,
  reportError,
  setBusy,
  setError,
  onRedirectScheduled,
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

    let navigated = false;

    try {
      navigated = await buyCreditsAndRedirect({
        licenseKey,
        amount,
        fetchImpl,
        navigate,
        onError: setError,
        reportError,
        checkoutErrorMessage,
        genericErrorMessage,
      });
    } catch (thrown) {
      // Not a failed request — `buyCreditsAndRedirect` has already caught,
      // classified and reported any of those. Only a collaborator reaches
      // here, so it is reported as one and never a second time as a network
      // fault. `navigated` is still false, which is correct: a navigation that
      // threw is a navigation that did not happen.
      reportHandlerFault(thrown, reportError);
    }

    // A plain statement and not a `finally`, because the `catch` above is
    // total: nothing can leave the block by throwing, so control always
    // arrives here and the release below is the last thing in the handler.
    // Its own `try` is the belt to that braces — a release that threw would be
    // the rejection this whole shape exists to prevent.
    try {
      if (navigated) onRedirectScheduled(() => setBusy(null));
      else setBusy(null);
    } catch (thrown) {
      // Unreachable in a real browser: the only host that can fail here is the
      // same one `navigate` just assigned to. Left in because the cost is four
      // lines and the alternative is an unhandled rejection.
      reportHandlerFault(thrown, reportError);
    }
  };
}
