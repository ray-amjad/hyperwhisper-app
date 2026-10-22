/**
 * `lib/clients/stripe.ts` builds the ONE Stripe client every money path
 * shares: checkout, the credit routes, refunds, the customer portal and the
 * webhook. Every other test mocks it at the module boundary, so before this
 * file nothing proved what the real client sends.
 *
 * Two things in it are silently expensive when they drift:
 *
 * - The `Stripe-Version` header. Checkout runs on Managed Payments, which is
 *   a private preview reached ONLY through the
 *   `; managed_payments_preview=v1` suffix. Drop the suffix and Stripe still
 *   answers, but on a plain API version that does not know the preview.
 * - The secret key. The client must authenticate with `STRIPE_SECRET_KEY`
 *   and nothing else.
 *
 * The module also builds `stripeRefundApi` (#755), the admin refund flow's
 * client, pinned to the older `2025-02-24.acacia` version that flow was
 * tested against. Its version and key are proved on the wire the same way.
 *
 * So the tests import the REAL module, point the real client at a local HTTP
 * server, make a real API call, and assert the headers that reach the wire.
 * The key below is a fixture: it is not shaped like a Stripe key, and the
 * local server never forwards it.
 */
import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { createRequire } from "node:module";
import { createServer, type IncomingHttpHeaders, type Server } from "node:http";
import type { AddressInfo } from "node:net";
import { fileURLToPath, pathToFileURL } from "node:url";
import { promisify } from "node:util";
import { after, before, describe, test } from "node:test";

const FIXTURE_KEY = "fixture-secret-key-not-a-real-stripe-key";
const PINNED_VERSION = "2025-12-15.clover; managed_payments_preview=v1";
const REFUND_PINNED_VERSION = "2025-02-24.acacia";

type Seen = { method?: string; url?: string; headers: IncomingHttpHeaders; body: string };

/** Points a real client at the local server; only the transport target changes. */
function pointAt(client: unknown, port: number): void {
  // `_setApiField` is the setter the constructor itself uses; the public
  // types do not declare it.
  const api = client as { _setApiField(key: string, value: string): void };
  api._setApiField("protocol", "http");
  api._setApiField("host", "127.0.0.1");
  api._setApiField("port", String(port));
}

describe("lib/clients/stripe — the shared Stripe client", () => {
  let server: Server;
  let port: number;
  const seen: Seen[] = [];

  before(async () => {
    server = createServer((req, res) => {
      let body = "";
      req.on("data", (chunk) => (body += chunk));
      req.on("end", () => {
        seen.push({ method: req.method, url: req.url, headers: req.headers, body });
        res.writeHead(200, { "Content-Type": "application/json" });
        if (req.url?.startsWith("/v1/checkout/sessions/")) {
          res.end(
            JSON.stringify({
              id: "cs_fixture",
              object: "checkout.session",
              payment_intent: "pi_fixture",
            }),
          );
        } else if (req.url === "/v1/refunds") {
          res.end(JSON.stringify({ id: "re_fixture", object: "refund" }));
        } else {
          res.end(
            JSON.stringify({
              object: "list",
              url: "/v1/customers",
              has_more: false,
              data: [{ id: "cus_fixture", object: "customer" }],
            }),
          );
        }
      });
    });
    await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
    port = (server.address() as AddressInfo).port;
  });

  after(async () => {
    await new Promise<void>((resolve) => server.close(() => resolve()));
  });

  test("sends the secret key and the Managed Payments API version on a real call", async () => {
    // The client reads the key through `src/env/server.mjs` (#682), which
    // snapshots process.env on first import. The fixture key is not shaped like
    // a Stripe key and the other secrets are absent, so skip the format check
    // here; `env-server-schema.test.ts` covers the check itself.
    process.env.SKIP_ENV_VALIDATION = "1";
    process.env.STRIPE_SECRET_KEY = FIXTURE_KEY;
    const { stripe } = await import("../lib/clients/stripe");

    // Point the real client at the local server. Only the transport target
    // changes; the key, the version and the request building are the module's.
    pointAt(stripe, port);

    const customers = await stripe.customers.list({ limit: 1 });

    assert.equal(seen.length, 1);
    const [req] = seen;
    assert.equal(req.method, "GET");
    assert.equal(req.url, "/v1/customers?limit=1");
    assert.equal(req.headers.authorization, `Bearer ${FIXTURE_KEY}`);
    assert.equal(req.headers["stripe-version"], PINNED_VERSION);
    // The parsed response comes back through the real client.
    assert.deepEqual(
      customers.data.map((c) => c.id),
      ["cus_fixture"],
    );
  });

  test("the refund client sends the secret key and the acacia API version on its two calls", async () => {
    process.env.SKIP_ENV_VALIDATION = "1";
    process.env.STRIPE_SECRET_KEY = FIXTURE_KEY;
    const { stripeRefundApi } = await import("../lib/clients/stripe");
    pointAt(stripeRefundApi, port);
    seen.length = 0;

    // The same two calls, with the same arguments, the admin refund flow
    // makes in server/api/routers/admin/customers.ts.
    const session = await stripeRefundApi.checkout.sessions.retrieve("cs_fixture");
    await stripeRefundApi.refunds.create(
      { payment_intent: "pi_fixture" },
      { idempotencyKey: "idem_fixture" },
    );

    assert.equal(session.payment_intent, "pi_fixture");
    assert.equal(seen.length, 2);
    const [retrieve, refund] = seen;
    assert.equal(retrieve.method, "GET");
    assert.equal(retrieve.url, "/v1/checkout/sessions/cs_fixture");
    assert.equal(refund.method, "POST");
    assert.equal(refund.url, "/v1/refunds");
    assert.equal(refund.body, "payment_intent=pi_fixture");
    assert.equal(refund.headers["idempotency-key"], "idem_fixture");
    for (const req of seen) {
      assert.equal(req.headers.authorization, `Bearer ${FIXTURE_KEY}`);
      assert.equal(req.headers["stripe-version"], REFUND_PINNED_VERSION);
    }
  });

  test("a missing STRIPE_SECRET_KEY fails the import instead of building an unauthenticated client", async () => {
    // A fresh process, so the module is evaluated again with no key.
    const env = { ...process.env };
    delete env.STRIPE_SECRET_KEY;
    // With validation on, `src/env/server.mjs` refuses a missing key before
    // the client is built. This proves the client's own refusal behind it.
    env.SKIP_ENV_VALIDATION = "1";
    const modulePath = fileURLToPath(new URL("../lib/clients/stripe.ts", import.meta.url));
    const script = `import(${JSON.stringify(modulePath)}).then(
      () => { console.log("IMPORTED"); process.exit(0); },
      (e) => { console.log("REJECTED:" + e.message); process.exit(3); },
    );`;

    const run = promisify(execFile);
    const result = await run(process.execPath, ["--import", "tsx", "-e", script], { env }).then(
      (r) => ({ code: 0, stdout: r.stdout }),
      (e: { code: number; stdout: string }) => ({ code: e.code, stdout: e.stdout }),
    );

    assert.equal(result.code, 3, `expected the import to reject, got: ${result.stdout}`);
    assert.match(result.stdout, /REJECTED:Neither apiKey nor config\.authenticator provided/);
  });

  test("with no STRIPE_SECRET_KEY, neither client is handed a fallback key", async () => {
    // The module builds two clients, so the real SDK's refusal above fires on
    // whichever is built first and cannot tell a fallback in the other apart.
    // Here the `stripe` package is a recorder in a fresh process, and every
    // constructor must have been handed exactly what the env holds: nothing.
    const env = { ...process.env };
    delete env.STRIPE_SECRET_KEY;
    env.SKIP_ENV_VALIDATION = "1";
    const modulePath = fileURLToPath(new URL("../lib/clients/stripe.ts", import.meta.url));
    // nextjs has no `"type": "module"`, so tsx loads the client as CommonJS and
    // its `import Stripe from "stripe"` requires the package's CJS entry.
    const stripeEntry = pathToFileURL(createRequire(import.meta.url).resolve("stripe")).href;
    const script = `
      const { mock } = await import("node:test");
      const keys = [];
      class RecordingStripe { constructor(key) { keys.push(key === undefined ? "<undefined>" : key); } }
      mock.module(${JSON.stringify(stripeEntry)}, { defaultExport: RecordingStripe });
      await import(${JSON.stringify(modulePath)});
      console.log("KEYS:" + JSON.stringify(keys));
    `;

    const run = promisify(execFile);
    const { stdout } = await run(
      process.execPath,
      ["--import", "tsx", "--experimental-test-module-mocks", "--input-type=module", "-e", script],
      { env },
    );

    const keys = JSON.parse(stdout.split("KEYS:")[1]);
    assert.deepEqual(keys, ["<undefined>", "<undefined>"]);
  });
});
