/**
 * #827: the admin Devices and Customers Refresh buttons show the shared
 * `<Spinner />` and go `disabled` while a fetch is in flight. They key on
 * `isFetching`, not `isLoading`: in react-query 5 `isLoading` is true only on
 * a key's FIRST fetch, so a `refetch()` on a page that already has data left
 * the old spinning arrow still and the button enabled.
 *
 * HOW. Renders the REAL page components through `renderToStaticMarkup`, with
 * the tRPC client mocked by the file the components load (see
 * `authenticated-error-alerts.test.ts`). The list query answers with data,
 * `isLoading: false`, and `isFetching` flipped per test: a refetch in flight.
 *
 * WHAT IT DOES NOT PROVE: that a click starts the refetch, or the animation.
 */
import assert from "node:assert/strict";
import test from "node:test";

import { moduleMock, moduleUrl } from "./module-mock";

/** Flipped per test; read by both mocked list queries. */
let fetching = false;

/** Flipped per test; read by the mocked Stripe portal mutation. */
let portalPending = false;

function listQuery(data: unknown) {
  return () => ({
    data,
    isLoading: false,
    isFetching: fetching,
    error: null,
    refetch: () => undefined,
  });
}

function idleMutation() {
  return {
    mutate: () => undefined,
    reset: () => undefined,
    isPending: false,
    error: null,
    data: undefined,
  };
}

moduleMock.module(moduleUrl("../lib/trpc/client.ts"), {
  namedExports: {
    api: {
      customer: {
        billingProviders: {
          useQuery: () => ({ data: { hasStripe: true, hasPolar: false } }),
        },
        stripePortalUrl: {
          useMutation: () => ({ ...idleMutation(), isPending: portalPending }),
        },
      },
      useUtils: () => ({
        admin: { customers: { list: { invalidate: () => undefined } } },
      }),
      admin: {
        devices: { list: { useQuery: listQuery({ devices: [] }) } },
        customers: {
          list: {
            useQuery: listQuery({ customers: [], total: 0, totalPages: 0 }),
          },
          grant: { useMutation: idleMutation },
          refund: { useMutation: idleMutation },
          addCredits: { useMutation: idleMutation },
          updateEmail: { useMutation: idleMutation },
        },
      },
    },
  },
});

const AUTH = "../app/[locale]/user/(authenticated)";
const BILLING_PATH = "../components/customer/dashboard/BillingCard.tsx";
const PAGES = {
  devices: `${AUTH}/devices/DevicesClient.tsx`,
  customers: `${AUTH}/customers/CustomersClient.tsx`,
};

async function refreshButton(path: string): Promise<{ attrs: string; inner: string }> {
  const { createElement } = await import("react");
  const { renderToStaticMarkup } = await import("react-dom/server");
  const { default: Page } = (await import(path)) as { default: () => null };
  const html = renderToStaticMarkup(createElement(Page));

  const buttons = Array.from(
    html.matchAll(/<button\b([^>]*)>([\s\S]*?)<\/button>/g),
  ).filter((m) => m[2].trim().endsWith("Refresh"));

  assert.equal(buttons.length, 1, "exactly 1 Refresh button");

  return { attrs: buttons[0][1], inner: buttons[0][2] };
}

// lucide-react names its glyph's class after the icon.
const SPINNER = /lucide-loader-circle/;

for (const [name, path] of Object.entries(PAGES)) {
  test(`${name}: a refetch in flight shows the spinner and disables Refresh`, async () => {
    fetching = true;
    const { attrs, inner } = await refreshButton(path);

    assert.match(inner, SPINNER);
    assert.match(inner, /animate-spin/);
    assert.match(attrs, /\bdisabled=""/);
  });

  test(`${name}: idle shows the still refresh arrow and an enabled Refresh`, async () => {
    fetching = false;
    const { attrs, inner } = await refreshButton(path);

    assert.doesNotMatch(inner, SPINNER);
    assert.doesNotMatch(inner, /animate-spin/);
    assert.doesNotMatch(attrs, /\bdisabled=""/);
  });
}

async function renderBilling(): Promise<string> {
  const { createElement } = await import("react");
  const { renderToStaticMarkup } = await import("react-dom/server");
  const { default: BillingCard } = (await import(BILLING_PATH)) as {
    default: () => null;
  };

  return renderToStaticMarkup(createElement(BillingCard));
}

test("Stripe Portal keeps its label and names the pending spinner", async () => {
  portalPending = true;
  const html = await renderBilling();

  assert.match(html, /role="status"[^>]*aria-label="Opening"|aria-label="Opening"[^>]*role="status"/);
  assert.match(html, SPINNER);
  assert.match(html, />Stripe Portal<\/button>/);
  assert.doesNotMatch(html, /Opening\.\.\./);
});

test("Stripe Portal idle shows no spinner", async () => {
  portalPending = false;
  const html = await renderBilling();

  assert.doesNotMatch(html, SPINNER);
  assert.match(html, />Stripe Portal<\/button>/);
});
