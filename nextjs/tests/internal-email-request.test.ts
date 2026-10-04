import assert from "node:assert/strict";
import test from "node:test";
import { inspect } from "node:util";
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

test("logs a malformed body with the pathname and a reason, never the email", async () => {
  // A bare email is not JSON, and V8 quotes the whole input in its SyntaxError
  // message. Prove that here, so the no-leak check below can never go vacuous.
  const body = "leak@example.com";
  assert.throws(() => JSON.parse(body), (err: Error) => err.message.includes(body));

  const warnings: unknown[][] = [];
  const realWarn = console.warn;
  console.warn = (...args: unknown[]) => {
    warnings.push(args);
  };
  try {
    assert.deepEqual(await errorFrom(body), {
      status: 400,
      body: { error: "Invalid JSON body" },
    });
  } finally {
    console.warn = realWarn;
  }

  assert.equal(warnings.length, 1);
  assert.deepEqual(warnings[0][1], {
    path: "/api/internal/test",
    errorName: "SyntaxError",
    contentType: "application/json",
    contentLength: null,
  });
  // inspect, not JSON.stringify: a logged Error object stringifies to "{}".
  const logged = warnings
    .map((args) => args.map((arg) => inspect(arg, { depth: null })).join(" "))
    .join("\n");
  assert.ok(!logged.includes(body), logged);
  assert.ok(!logged.includes(INTERNAL_SECRET), logged);
});

async function captureWarnings<T>(run: () => Promise<T>) {
  const warnings: unknown[][] = [];
  const realWarn = console.warn;
  console.warn = (...args: unknown[]) => {
    warnings.push(args);
  };
  try {
    return { result: await run(), warnings };
  } finally {
    console.warn = realWarn;
  }
}

// A missing or mistyped email is the same wire-format disagreement as an
// unparseable body (#1226): one warning with the pathname and the type only.
for (const [name, body, emailType] of [
  ["missing", "{}", "undefined"],
  ["mis-keyed", JSON.stringify({ Email: "leak@example.com" }), "undefined"],
  ["non-string", JSON.stringify({ email: 123 }), "number"],
  ["empty", JSON.stringify({ email: "" }), "string"],
  ["null", JSON.stringify({ email: null }), "object"],
  ["string-body", JSON.stringify("leak@example.com"), "no-body-object"],
  ["null-body", "null", "no-body-object"],
] as const) {
  test(`rejects the ${name} email value and logs it without the body`, async () => {
    const { result, warnings } = await captureWarnings(() => errorFrom(body));

    assert.deepEqual(result, {
      status: 400,
      body: { error: "email is required" },
    });
    assert.equal(warnings.length, 1);
    assert.deepEqual(warnings[0], [
      "internal email request: email missing",
      { path: "/api/internal/test", emailType },
    ]);
    // inspect, not JSON.stringify, matching the malformed-body case above.
    const logged = warnings
      .map((args) => args.map((arg) => inspect(arg, { depth: null })).join(" "))
      .join("\n");
    assert.ok(!logged.includes("leak@example.com"), logged);
    assert.ok(!logged.includes(INTERNAL_SECRET), logged);
  });
}

test("does not log a valid email request", async () => {
  const { result, warnings } = await captureWarnings(() =>
    parseInternalEmailRequest(
      request(JSON.stringify({ email: "user@example.com" })),
    ),
  );

  assert.deepEqual(result, { email: "user@example.com" });
  assert.equal(warnings.length, 0);
});

test("keeps the whitespace-only email compatibility behavior", async () => {
  const result = await parseInternalEmailRequest(
    request(JSON.stringify({ email: "   " })),
  );

  assert.deepEqual(result, { email: "" });
});
