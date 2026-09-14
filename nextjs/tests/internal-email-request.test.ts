import assert from "node:assert/strict";
import test from "node:test";
import { NextRequest } from "next/server";

import { parseInternalEmailRequest } from "../app/api/internal/email-request";

const INTERNAL_SECRET = "test-internal-secret";

function request(body: string, secret = INTERNAL_SECRET): NextRequest {
  return new NextRequest("http://localhost/api/internal/test", {
    method: "POST",
    headers: {
      "content-type": "application/json",
      "x-internal-secret": secret,
    },
    body,
  });
}

async function errorFrom(body: string, secret = INTERNAL_SECRET) {
  const result = await parseInternalEmailRequest(request(body, secret));
  assert.ok("response" in result);
  return {
    status: result.response.status,
    body: await result.response.json(),
  };
}

test.before(() => {
  process.env.HYPERWHISPER_INTERNAL_SECRET = INTERNAL_SECRET;
});

test("normalizes a valid mixed-case email", async () => {
  const result = await parseInternalEmailRequest(
    request(JSON.stringify({ email: "  User@Example.COM  " })),
  );

  assert.deepEqual(result, { email: "user@example.com" });
});

test("checks authentication before reading malformed JSON", async () => {
  assert.deepEqual(await errorFrom("{", "wrong-secret"), {
    status: 401,
    body: { error: "Unauthorized" },
  });
});

test("rejects malformed JSON", async () => {
  assert.deepEqual(await errorFrom("{"), {
    status: 400,
    body: { error: "Invalid JSON body" },
  });
});

for (const [name, body] of [
  ["missing", {}],
  ["non-string", { email: 123 }],
  ["empty", { email: "" }],
] as const) {
  test(`rejects the ${name} email value`, async () => {
    assert.deepEqual(await errorFrom(JSON.stringify(body)), {
      status: 400,
      body: { error: "email is required" },
    });
  });
}

test("keeps the whitespace-only email compatibility behavior", async () => {
  const result = await parseInternalEmailRequest(
    request(JSON.stringify({ email: "   " })),
  );

  assert.deepEqual(result, { email: "" });
});
