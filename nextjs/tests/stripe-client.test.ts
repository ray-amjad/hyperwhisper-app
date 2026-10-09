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
 * So the tests import the REAL module, point the real client at a local HTTP
 * server, make a real API call, and assert the headers that reach the wire.
 * The key below is a fixture: it is not shaped like a Stripe key, and the
 * local server never forwards it.
 */
import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { createServer, type IncomingHttpHeaders, type Server } from "node:http";
import type { AddressInfo } from "node:net";
import { fileURLToPath } from "node:url";
import { promisify } from "node:util";
import { after, before, describe, test } from "node:test";

const FIXTURE_KEY = "fixture-secret-key-not-a-real-stripe-key";
const PINNED_VERSION = "2025-12-15.clover; managed_payments_preview=v1";

type Seen = { method?: string; url?: string; headers: IncomingHttpHeaders };

describe("lib/clients/stripe — the shared Stripe client", () => {
  let server: Server;
  let port: number;
  const seen: Seen[] = [];

  before(async () => {
    server = createServer((req, res) => {
      seen.push({ method: req.method, url: req.url, headers: req.headers });
      res.writeHead(200, { "Content-Type": "application/json" });
      res.end(
        JSON.stringify({
          object: "list",
          url: "/v1/customers",
          has_more: false,
          data: [{ id: "cus_fixture", object: "customer" }],
        }),
      );
    });
    await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
    port = (server.address() as AddressInfo).port;
  });

  after(async () => {
    await new Promise<void>((resolve) => server.close(() => resolve()));
  });

  test("sends the secret key and the Managed Payments API version on a real call", async () => {
    process.env.STRIPE_SECRET_KEY = FIXTURE_KEY;
    const { stripe } = await import("../lib/clients/stripe");

    // Point the real client at the local server. Only the transport target
    // changes; the key, the version and the request building are the module's.
    // `_setApiField` is the setter the constructor itself uses; the public
    // types do not declare it.
    const api = stripe as unknown as { _setApiField(key: string, value: string): void };
    api._setApiField("protocol", "http");
    api._setApiField("host", "127.0.0.1");
    api._setApiField("port", String(port));

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

  test("a missing STRIPE_SECRET_KEY fails the import instead of building an unauthenticated client", async () => {
    // A fresh process, so the module is evaluated again with no key.
    const env = { ...process.env };
    delete env.STRIPE_SECRET_KEY;
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
});
