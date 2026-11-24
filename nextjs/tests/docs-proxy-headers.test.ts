/**
 * worker.ts strips credentials on the Mintlify docs proxy (#2636): no
 * `Cookie` or `Authorization` goes to Mintlify, and no `Set-Cookie` from
 * Mintlify reaches the client. Other paths keep their headers.
 */
import assert from "node:assert/strict";
import test from "node:test";

import {
  fetchDocsProxy,
  isDocsProxyPath,
  withoutRequestCredentials,
  withoutSetCookie,
} from "../lib/cloudflare/docs-proxy-headers";

test("every Mintlify rewrite source is a docs proxy path", () => {
  for (const path of [
    "/docs",
    "/docs/",
    "/docs/getting-started",
    "/docs/llms.txt",
    "/docs.rsc",
    "/DOCS/Getting-Started",
    "/%64ocs/quickstart",
    "/_mintlify/api/csp-report",
    "/api/request",
    "/mintlify-assets/_next/static/app.js",
  ]) {
    assert.equal(isDocsProxyPath(path), true, path);
  }
});

test("site paths are not docs proxy paths", () => {
  for (const path of [
    "/",
    "/en",
    "/en/docs",
    "/docsearch",
    "/api/auth/get-session",
    "/api/trpc/customer.profile",
    "/api/request/other",
    "/mintlify-assets",
    "/mintlify-assets/",
  ]) {
    assert.equal(isDocsProxyPath(path), false, path);
  }
});

test("Cookie and Authorization are not forwarded; other headers are", () => {
  const request = new Request("https://www.hyperwhisper.com/docs", {
    headers: {
      cookie: "__Secure-better-auth.session_token=secret",
      authorization: "Bearer secret",
      accept: "text/html",
      "accept-encoding": "br, gzip",
      "user-agent": "test",
    },
  });
  const stripped = withoutRequestCredentials(request);

  assert.equal(stripped.headers.get("cookie"), null);
  assert.equal(stripped.headers.get("authorization"), null);
  assert.equal(stripped.headers.get("accept"), "text/html");
  assert.equal(stripped.headers.get("accept-encoding"), "br, gzip");
  assert.equal(stripped.headers.get("user-agent"), "test");
  assert.equal(stripped.url, request.url);
  assert.equal(stripped.method, request.method);
});

test("a request with no credentials passes through as is", () => {
  const request = new Request("https://www.hyperwhisper.com/docs");

  assert.equal(withoutRequestCredentials(request), request);
});

test("a POST body survives the stripping", async () => {
  const request = new Request("https://www.hyperwhisper.com/api/request", {
    method: "POST",
    headers: { cookie: "a=b", "content-type": "application/json" },
    body: JSON.stringify({ url: "https://example.com" }),
  });
  const stripped = withoutRequestCredentials(request);

  assert.equal(stripped.method, "POST");
  assert.equal(stripped.headers.get("cookie"), null);
  assert.equal(await stripped.text(), '{"url":"https://example.com"}');
});

test("Set-Cookie is dropped from the response; status, body and other headers stay", async () => {
  const headers = new Headers({
    "content-type": "text/html",
    "cache-control": "public, max-age=60",
  });

  headers.append("set-cookie", "mintlify=1; Path=/");
  headers.append("set-cookie", "other=2; Path=/; Domain=hyperwhisper.com");

  const response = withoutSetCookie(
    new Response("<html>docs</html>", {
      status: 404,
      statusText: "Not Found",
      headers,
    }),
  );

  assert.equal(response.headers.get("set-cookie"), null);
  assert.deepEqual(response.headers.getSetCookie(), []);
  assert.equal(response.status, 404);
  assert.equal(response.statusText, "Not Found");
  assert.equal(response.headers.get("content-type"), "text/html");
  assert.equal(response.headers.get("cache-control"), "public, max-age=60");
  assert.equal(await response.text(), "<html>docs</html>");
});

test("a response with no Set-Cookie passes through as is", () => {
  const response = new Response("ok");

  assert.equal(withoutSetCookie(response), response);
});

test("fetchDocsProxy strips both directions around the handler", async () => {
  let seen: Request | undefined;
  const response = await fetchDocsProxy(
    new Request("https://www.hyperwhisper.com/docs/quickstart", {
      headers: { cookie: "session=secret", authorization: "Bearer secret" },
    }),
    async (request) => {
      seen = request;

      return new Response("docs", {
        headers: { "set-cookie": "tracker=1; Path=/", "x-mintlify": "1" },
      });
    },
  );

  assert.ok(seen);
  assert.equal(seen.headers.get("cookie"), null);
  assert.equal(seen.headers.get("authorization"), null);
  assert.equal(response.headers.get("set-cookie"), null);
  assert.equal(response.headers.get("x-mintlify"), "1");
  assert.equal(await response.text(), "docs");
});
