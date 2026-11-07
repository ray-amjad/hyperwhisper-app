/**
 * worker.ts drops a proxied response's Content-Encoding when the client did
 * not accept it, so the Workers runtime sends the body uncompressed (a plain
 * `curl` of /docs/llms.txt used to get a br body).
 */
import assert from "node:assert/strict";
import test from "node:test";

import {
  acceptsEncoding,
  clientAcceptEncoding,
  withAcceptedEncoding,
} from "../lib/cloudflare/content-encoding";

test("acceptsEncoding follows Accept-Encoding tokens, q-values and *", () => {
  assert.equal(acceptsEncoding(null, "br"), false);
  assert.equal(acceptsEncoding("", "br"), false);
  assert.equal(acceptsEncoding("gzip, deflate, br", "br"), true);
  assert.equal(acceptsEncoding("gzip, deflate, BR", "br"), true);
  assert.equal(acceptsEncoding("gzip", "br"), false);
  assert.equal(acceptsEncoding("identity", "br"), false);
  assert.equal(acceptsEncoding("br;q=0", "br"), false);
  assert.equal(acceptsEncoding("br;q=0.5", "br"), true);
  assert.equal(acceptsEncoding("*", "br"), true);
  assert.equal(acceptsEncoding("*;q=0", "br"), false);
  assert.equal(acceptsEncoding("*, br;q=0", "br"), false);
  assert.equal(acceptsEncoding(null, "identity"), true);
});

function proxied(encoding: string | null) {
  const headers = new Headers({
    "content-type": "text/plain",
    "content-length": "12",
  });
  if (encoding) headers.set("content-encoding", encoding);
  return new Response("# HyperWhisper", { status: 200, headers });
}

function request(acceptEncoding?: string) {
  return new Request("https://www.hyperwhisper.com/docs/llms.txt", {
    headers: acceptEncoding ? { "accept-encoding": acceptEncoding } : {},
  });
}

test("a client that accepts the coding gets the response untouched", () => {
  const response = proxied("br");
  assert.equal(withAcceptedEncoding(request("gzip, br"), response), response);
});

test("a client that sent no Accept-Encoding gets no Content-Encoding", async () => {
  const response = withAcceptedEncoding(request(), proxied("br"));
  assert.equal(response.headers.get("content-encoding"), null);
  assert.equal(response.headers.get("content-length"), null);
  assert.equal(response.headers.get("content-type"), "text/plain");
  assert.equal(response.status, 200);
  assert.equal(await response.text(), "# HyperWhisper");
});

test("a client asking for identity only gets no Content-Encoding", () => {
  const response = withAcceptedEncoding(request("identity"), proxied("gzip"));
  assert.equal(response.headers.get("content-encoding"), null);
});

test("the client's own Accept-Encoding wins over Cloudflare's rewritten one", () => {
  // Cloudflare hands the Worker `Accept-Encoding: br, gzip` and keeps what
  // the client sent in request.cf.clientAcceptEncoding ("" when absent).
  const rewritten = (clientValue: string) =>
    Object.assign(request("br, gzip"), { cf: { clientAcceptEncoding: clientValue } });
  assert.equal(clientAcceptEncoding(rewritten("")), "");
  assert.equal(clientAcceptEncoding(request("gzip")), "gzip");
  assert.equal(clientAcceptEncoding(request()), null);

  const plain = withAcceptedEncoding(rewritten(""), proxied("br"));
  assert.equal(plain.headers.get("content-encoding"), null);
  const browser = proxied("br");
  assert.equal(withAcceptedEncoding(rewritten("gzip, deflate, br"), browser), browser);
});

test("an unencoded response is returned as is", () => {
  const response = proxied(null);
  assert.equal(withAcceptedEncoding(request(), response), response);
});
