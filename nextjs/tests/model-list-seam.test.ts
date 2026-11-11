import assert from "node:assert/strict";
import test from "node:test";

import { captureConsole } from "./module-mock";

// Imported through a variable specifier inside each test, the way the other
// tests in this folder do: a static `.ts` import path is a type error under this
// tsconfig, but `node --test --experimental-strip-types` needs the extension.
const MODULE_PATH = "../lib/services/model-list.ts";
const load = () => import(MODULE_PATH);

const ENV = {
  OPENAI_API_KEY: "k",
  ANTHROPIC_API_KEY: "k",
  GEMINI_API_KEY: "k",
  GROQ_API_KEY: "k",
  XAI_API_KEY: "k",
  CEREBRAS_API_KEY: "k",
};

/** One fake upstream. Counts the calls and answers every provider endpoint. */
function fakeUpstream(status = 200) {
  let calls = 0;
  const fetchImpl = async (input: RequestInfo | URL): Promise<Response> => {
    calls += 1;
    const url = String(input);
    if (status !== 200) return new Response("nope", { status });
    let body: unknown = { data: [] };
    if (url.includes("openai.com")) {
      body = {
        data: [
          { id: "gpt-5" },
          { id: "gpt-5-2026-01-01" }, // dated snapshot, dropped
          { id: "whisper-1" }, // on the exclude list, dropped
        ],
      };
    } else if (url.includes("generativelanguage")) {
      body = {
        models: [
          {
            name: "models/gemini-9",
            displayName: "Gemini 9",
            supportedGenerationMethods: ["generateContent"],
          },
        ],
      };
    }
    return new Response(JSON.stringify(body), { status: 200 });
  };
  return { fetchImpl, upstreamCalls: () => calls };
}

test("concurrent cold-cache callers share one upstream fan-out", async () => {
  const { createModelList } = await load();
  const { fetchImpl, upstreamCalls } = fakeUpstream();
  const list = createModelList({ fetch: fetchImpl, env: ENV, now: () => 0 });

  await Promise.all(Array.from({ length: 5 }, () => list.fetchAvailableModels()));

  // 6 providers, one GET each — not 30. The in-flight slot collapsed the rest.
  assert.equal(upstreamCalls(), 6);
});

test("a fan-out where every provider failed is cached for 1 minute, not 1 hour", async () => {
  const { createModelList } = await load();
  const { fetchImpl, upstreamCalls } = fakeUpstream(500);
  let clock = 0;
  const list = createModelList({ fetch: fetchImpl, env: ENV, now: () => clock });

  const quiet = console.error;
  console.error = () => {};
  try {
    const first = await list.fetchAvailableModels();
    assert.ok(Object.values(first.providers).every((p) => !(p as { ok: boolean }).ok));
    assert.equal(upstreamCalls(), 6);

    // Inside the 60s failure window the broken answer is still served.
    clock = 59_000;
    await list.fetchAvailableModels();
    assert.equal(upstreamCalls(), 6);

    // Past it, the fan-out runs again — the hour-long TTL never applied.
    clock = 61_000;
    await list.fetchAvailableModels();
    assert.equal(upstreamCalls(), 12);
  } finally {
    console.error = quiet;
  }
});

test("a successful fan-out is held for the full hour", async () => {
  const { createModelList } = await load();
  const { fetchImpl, upstreamCalls } = fakeUpstream();
  let clock = 0;
  const list = createModelList({ fetch: fetchImpl, env: ENV, now: () => clock });

  await list.fetchAvailableModels();
  assert.equal(upstreamCalls(), 6);

  clock = 59 * 60 * 1000;
  const cached = await list.fetchAvailableModels();
  assert.equal(upstreamCalls(), 6);
  // `fetchedAt` still carries the clock reading of the first fan-out.
  assert.equal(cached.fetchedAt, new Date(0).toISOString());

  clock = 61 * 60 * 1000;
  await list.fetchAvailableModels();
  assert.equal(upstreamCalls(), 12);
});

test("a provider with no key fails that provider only", async () => {
  const { createModelList } = await load();
  const { fetchImpl } = fakeUpstream();
  const list = createModelList({
    fetch: fetchImpl,
    env: { ...ENV, GROQ_API_KEY: undefined },
    now: () => 0,
  });

  const result = await list.fetchAvailableModels();
  assert.deepEqual(result.providers.groq, { ok: false, error: "missing GROQ_API_KEY" });
  assert.equal((result.providers.openai as { ok: boolean }).ok, true);
});

test("the OpenAI filter drops dated snapshots and excluded families", async () => {
  const { createModelList } = await load();
  const { fetchImpl } = fakeUpstream();
  const list = createModelList({ fetch: fetchImpl, env: ENV, now: () => 0 });

  const result = await list.fetchAvailableModels();
  const openai = result.providers.openai as { ok: true; models: Array<{ id: string }> };
  assert.deepEqual(
    openai.models.map((m) => m.id),
    ["gpt-5"]
  );
});

/** Swap console.error for a recorder; the caller restores it in `finally`. */
const captureConsoleError = () => captureConsole(["error"]);

test("a rejected provider fetch logs the provider and the cause, and keeps the result shape", async () => {
  const { createModelList } = await load();
  const { fetchImpl: healthy } = fakeUpstream();
  const fetchImpl = async (input: RequestInfo | URL): Promise<Response> => {
    if (String(input).includes("api.openai.com")) {
      throw new TypeError("fetch failed", {
        cause: new Error("getaddrinfo ENOTFOUND api.openai.com"),
      });
    }
    return healthy(input);
  };
  const list = createModelList({ fetch: fetchImpl, env: ENV, now: () => 0 });

  const captured = captureConsoleError();
  let result;
  try {
    result = await list.fetchAvailableModelsUncached();
  } finally {
    captured.restore();
  }

  assert.deepEqual(result.providers.openai, { ok: false, error: "fetch failed" });
  const line = captured.lines.find((l) => l.includes("openai"));
  assert.ok(line, `no console.error line names openai: ${JSON.stringify(captured.lines)}`);
  assert.ok(line.startsWith("[model-list] "), line);
  assert.ok(line.includes("getaddrinfo ENOTFOUND api.openai.com"), line);
});

test("a provider failure log never carries the API key", async () => {
  const { createModelList } = await load();
  const { fetchImpl: healthy } = fakeUpstream();
  const planted = "gemini-test-value-850";
  // undici quotes the whole URL — Gemini's carries the key — when it cannot parse it.
  const fetchImpl = async (input: RequestInfo | URL): Promise<Response> => {
    const url = String(input);
    if (url.includes("generativelanguage")) {
      throw new TypeError(`Failed to parse URL from ${url}`, {
        cause: new Error(`Invalid URL: ${url}`),
      });
    }
    return healthy(input);
  };
  const list = createModelList({
    fetch: fetchImpl,
    env: { ...ENV, GEMINI_API_KEY: planted },
    now: () => 0,
  });

  const captured = captureConsoleError();
  let result;
  try {
    result = await list.fetchAvailableModelsUncached();
  } finally {
    captured.restore();
  }

  const line = captured.lines.find((l) => l.includes("gemini"));
  assert.ok(line, `no console.error line names gemini: ${JSON.stringify(captured.lines)}`);
  assert.ok(line.includes("[redacted]"), line);
  for (const l of captured.lines) assert.ok(!l.includes(planted), l);
  // The public /models route returns this error to anonymous clients.
  const gemini = result.providers.gemini;
  assert.equal(gemini.ok, false);
  assert.ok(!gemini.error.includes(planted), gemini.error);
  assert.ok(gemini.error.includes("[redacted]"), gemini.error);
});

test("a key with reserved characters is redacted in its URL-encoded form too", async () => {
  const { createModelList } = await load();
  const { fetchImpl: healthy } = fakeUpstream();
  const planted = "gemini/test value 850";
  const encoded = encodeURIComponent(planted);
  assert.notEqual(encoded, planted);
  const fetchImpl = async (input: RequestInfo | URL): Promise<Response> => {
    const url = String(input);
    if (url.includes("generativelanguage")) {
      assert.ok(url.includes(encoded), url);
      throw new TypeError(`Failed to parse URL from ${url}`, {
        cause: new Error(`Invalid URL: ${url}`),
      });
    }
    return healthy(input);
  };
  const list = createModelList({
    fetch: fetchImpl,
    env: { ...ENV, GEMINI_API_KEY: planted },
    now: () => 0,
  });

  const captured = captureConsoleError();
  let result;
  try {
    result = await list.fetchAvailableModelsUncached();
  } finally {
    captured.restore();
  }

  const line = captured.lines.find((l) => l.includes("gemini"));
  assert.ok(line, `no console.error line names gemini: ${JSON.stringify(captured.lines)}`);
  const gemini = result.providers.gemini;
  assert.equal(gemini.ok, false);
  for (const text of [...captured.lines, gemini.error]) {
    assert.ok(!text.includes(planted), text);
    assert.ok(!text.includes(encoded), text);
  }
  assert.ok(gemini.error.includes("[redacted]"), gemini.error);
});

test("an AggregateError cause logs each inner error message", async () => {
  const { createModelList } = await load();
  const { fetchImpl: healthy } = fakeUpstream();
  // undici's dual-stack connect failure: one error per address family.
  const fetchImpl = async (input: RequestInfo | URL): Promise<Response> => {
    if (String(input).includes("api.openai.com")) {
      throw new TypeError("fetch failed", {
        cause: new AggregateError(
          [
            new Error("connect ECONNREFUSED 10.0.0.1:443"),
            new Error("connect ENETUNREACH 2001:db8::1:443"),
          ],
          ""
        ),
      });
    }
    return healthy(input);
  };
  const list = createModelList({ fetch: fetchImpl, env: ENV, now: () => 0 });

  const captured = captureConsoleError();
  let result;
  try {
    result = await list.fetchAvailableModelsUncached();
  } finally {
    captured.restore();
  }

  assert.deepEqual(result.providers.openai, { ok: false, error: "fetch failed" });
  const line = captured.lines.find((l) => l.includes("openai"));
  assert.ok(line, `no console.error line names openai: ${JSON.stringify(captured.lines)}`);
  assert.ok(line.includes("connect ECONNREFUSED 10.0.0.1:443"), line);
  assert.ok(line.includes("connect ENETUNREACH 2001:db8::1:443"), line);
});

// #3029: a 403 log line must tell a bad key from a Worker egress block, so it
// carries the colo and the upstream error code. It never carries the key, the
// body or the query string, and the public `error` stays `HTTP 403`.

/** Answers every provider healthily except the ones `failing` matches. */
function failingUpstream(
  failing: (url: string) => boolean,
  respond: () => Response,
) {
  const { fetchImpl: healthy } = fakeUpstream();
  const seen: string[] = [];
  const fetchImpl = async (input: RequestInfo | URL): Promise<Response> => {
    const url = String(input);
    seen.push(url);
    return failing(url) ? respond() : healthy(input);
  };
  return { fetchImpl, seen };
}

// Distinct, longer keys: redactKey would blank every "k" in a line if the key were "k".
const DIAG_ENV = Object.fromEntries(
  Object.keys(ENV).map((name) => [name, `test-${name.toLowerCase()}-3029`]),
);

async function runUncached(deps: Record<string, unknown>) {
  const { createModelList } = await load();
  const list = createModelList({ env: DIAG_ENV, now: () => 0, ...deps });
  const captured = captureConsoleError();
  try {
    const result = await list.fetchAvailableModelsUncached();
    return { result, lines: captured.lines };
  } finally {
    captured.restore();
  }
}

const linesFor = (lines: string[], needle: string) => lines.filter((l) => l.includes(needle));

test("a 403 with a JSON error code logs the code and the colo, never the key or the body", async () => {
  // Built from parts so a secret scanner does not read the fixture as a key literal.
  const plantedKey = ["csk", "planted", "cerebras", "3029"].join("-");
  const bodyText = "Your request was blocked from this location, see https://example.invalid/?k=1";
  const { fetchImpl } = failingUpstream(
    (url) => url.includes("api.cerebras.ai"),
    () =>
      new Response(
        JSON.stringify({
          error: {
            message: `${bodyText} ${plantedKey}`,
            type: "invalid_request_error",
            code: "unsupported_country_region_territory",
          },
        }),
        { status: 403, headers: { "content-type": "application/json" } },
      ),
  );
  const { result, lines } = await runUncached({
    fetch: fetchImpl,
    env: { ...DIAG_ENV, CEREBRAS_API_KEY: plantedKey },
    colo: () => "NRT",
  });

  // The client-facing value is unchanged.
  assert.deepEqual(result.providers.cerebras, { ok: false, error: "HTTP 403" });

  const [statusLine] = linesFor(lines, "api.cerebras.ai");
  assert.equal(
    statusLine,
    "[model-list] api.cerebras.ai/v1/models -> HTTP 403 colo=NRT code=unsupported_country_region_territory",
  );
  const [failLine] = linesFor(lines, "cerebras fetch failed");
  assert.equal(
    failLine,
    "[model-list] cerebras fetch failed: HTTP 403 colo=NRT code=unsupported_country_region_territory",
  );
  for (const l of lines) {
    assert.ok(!l.includes(plantedKey), l);
    assert.ok(!l.includes("blocked from this location"), l);
    assert.ok(!l.includes("example.invalid"), l);
  }
});

test("a 403 with a non-JSON body logs no code and does not crash", async () => {
  const { fetchImpl } = failingUpstream(
    (url) => url.includes("api.openai.com"),
    () => new Response("<html>403 Forbidden by edge policy</html>", { status: 403 }),
  );
  const { result, lines } = await runUncached({ fetch: fetchImpl, colo: () => "SIN" });

  assert.deepEqual(result.providers.openai, { ok: false, error: "HTTP 403" });
  assert.equal((result.providers.anthropic as { ok: boolean }).ok, true);
  const [statusLine] = linesFor(lines, "api.openai.com");
  assert.equal(statusLine, "[model-list] api.openai.com/v1/models -> HTTP 403 colo=SIN");
  for (const l of lines) {
    assert.ok(!l.includes("code="), l);
    assert.ok(!l.includes("Forbidden by edge policy"), l);
  }
});

test("a 403 from Gemini never logs the key in its URL, and reads Gemini's status code", async () => {
  // Built from parts so a secret scanner does not read the fixture as a key literal.
  const plantedKey = ["AIza", "planted", "gemini", "3029"].join("-");
  const { fetchImpl, seen } = failingUpstream(
    (url) => url.includes("generativelanguage"),
    () =>
      new Response(
        JSON.stringify({
          error: {
            code: 403,
            message: `Method doesn't allow unregistered callers. key=${plantedKey}`,
            status: "PERMISSION_DENIED",
          },
        }),
        { status: 403 },
      ),
  );
  const { result, lines } = await runUncached({
    fetch: fetchImpl,
    env: { ...DIAG_ENV, GEMINI_API_KEY: plantedKey },
    colo: () => "FRA",
  });

  assert.ok(seen.some((u) => u.includes(plantedKey)), "the planted key never reached the URL");
  assert.deepEqual(result.providers.gemini, { ok: false, error: "HTTP 403" });
  const [statusLine] = linesFor(lines, "generativelanguage");
  assert.equal(
    statusLine,
    "[model-list] generativelanguage.googleapis.com/v1beta/models -> HTTP 403 colo=FRA code=PERMISSION_DENIED",
  );
  for (const l of lines) {
    assert.ok(!l.includes(plantedKey), l);
    assert.ok(!l.includes("pageSize"), l);
    assert.ok(!l.includes("unregistered callers"), l);
  }
});

test("an Anthropic-shaped 403 logs its nested error type", async () => {
  const { fetchImpl } = failingUpstream(
    (url) => url.includes("api.anthropic.com"),
    () =>
      new Response(
        JSON.stringify({ type: "error", error: { type: "permission_error", message: "no" } }),
        { status: 403 },
      ),
  );
  const { lines } = await runUncached({ fetch: fetchImpl, colo: () => "LHR" });
  const [statusLine] = linesFor(lines, "api.anthropic.com");
  assert.equal(statusLine, "[model-list] api.anthropic.com/v1/models -> HTTP 403 colo=LHR code=permission_error");
});

test("an error code that is not a short safe token is dropped, not logged", async () => {
  const { fetchImpl } = failingUpstream(
    (url) => url.includes("api.x.ai"),
    () =>
      new Response(JSON.stringify({ error: { code: "bad key: sk-123 /v1?key=x", type: "x".repeat(65) } }), {
        status: 403,
      }),
  );
  const { result, lines } = await runUncached({ fetch: fetchImpl, colo: () => "NRT" });
  assert.deepEqual(result.providers.grok, { ok: false, error: "HTTP 403" });
  const [statusLine] = linesFor(lines, "api.x.ai");
  assert.equal(statusLine, "[model-list] api.x.ai/v1/models -> HTTP 403 colo=NRT");
});

test("with no colo dep (outside a Worker) the log says colo=unknown and nothing breaks", async () => {
  const { fetchImpl } = failingUpstream(
    (url) => url.includes("api.groq.com"),
    () => new Response(JSON.stringify({ error: { code: "invalid_api_key" } }), { status: 403 }),
  );
  // No `colo` dep: the default reads getCloudflareContext(), which throws here.
  const { result, lines } = await runUncached({ fetch: fetchImpl });
  assert.deepEqual(result.providers.groq, { ok: false, error: "HTTP 403" });
  const [statusLine] = linesFor(lines, "api.groq.com");
  assert.equal(statusLine, "[model-list] api.groq.com/openai/v1/models -> HTTP 403 colo=unknown code=invalid_api_key");
});

test("a colo dep that throws or returns junk reads as unknown", async () => {
  for (const colo of [
    () => {
      throw new Error("no context");
    },
    () => "N R T <script>",
  ]) {
    const { fetchImpl } = failingUpstream(
      (url) => url.includes("api.groq.com"),
      () => new Response("nope", { status: 403 }),
    );
    const { result, lines } = await runUncached({ fetch: fetchImpl, colo });
    assert.deepEqual(result.providers.groq, { ok: false, error: "HTTP 403" });
    assert.equal((result.providers.openai as { ok: boolean }).ok, true);
    const [statusLine] = linesFor(lines, "api.groq.com");
    assert.equal(statusLine, "[model-list] api.groq.com/openai/v1/models -> HTTP 403 colo=unknown");
  }
});

test("a network failure line carries the colo too", async () => {
  const { fetchImpl: healthy } = fakeUpstream();
  const fetchImpl = async (input: RequestInfo | URL): Promise<Response> => {
    if (String(input).includes("api.openai.com")) throw new TypeError("fetch failed");
    return healthy(input);
  };
  const { lines } = await runUncached({ fetch: fetchImpl, colo: () => "NRT" });
  const [failLine] = linesFor(lines, "openai fetch failed");
  assert.equal(failLine, "[model-list] openai fetch failed: fetch failed colo=NRT");
});

test("an oversized 403 body is not parsed", async () => {
  const big = JSON.stringify({ error: { code: "invalid_api_key" }, pad: "x".repeat(16 * 1024) });
  const { fetchImpl } = failingUpstream(
    (url) => url.includes("api.cerebras.ai"),
    () => new Response(big, { status: 403 }),
  );
  const { result, lines } = await runUncached({ fetch: fetchImpl, colo: () => "NRT" });
  assert.deepEqual(result.providers.cerebras, { ok: false, error: "HTTP 403" });
  const [statusLine] = linesFor(lines, "api.cerebras.ai");
  assert.equal(statusLine, "[model-list] api.cerebras.ai/v1/models -> HTTP 403 colo=NRT");
});

test("a 403 body that never ends is abandoned and the fan-out still returns", async () => {
  let cancelled = false;
  const stalled = () =>
    new Response(
      new ReadableStream<Uint8Array>({
        start(controller) {
          controller.enqueue(new TextEncoder().encode('{"error":{"code":"inv'));
          // never closes
        },
        cancel() {
          cancelled = true;
        },
      }),
      { status: 403 },
    );
  const { fetchImpl } = failingUpstream((url) => url.includes("api.cerebras.ai"), stalled);
  const started = Date.now();
  const { result, lines } = await runUncached({ fetch: fetchImpl, colo: () => "NRT" });
  assert.ok(Date.now() - started < 10_000, "the stalled body held the fan-out");
  assert.deepEqual(result.providers.cerebras, { ok: false, error: "HTTP 403" });
  assert.equal((result.providers.openai as { ok: boolean }).ok, true);
  const [statusLine] = linesFor(lines, "api.cerebras.ai");
  assert.equal(statusLine, "[model-list] api.cerebras.ai/v1/models -> HTTP 403 colo=NRT");
  assert.ok(cancelled, "the stalled body was not cancelled");
});

test("a non-403 failure does not read the body", async () => {
  let bodyRead = false;
  const { fetchImpl } = failingUpstream(
    (url) => url.includes("api.openai.com"),
    () =>
      new Response(
        new ReadableStream<Uint8Array>({
          pull(controller) {
            bodyRead = true;
            controller.enqueue(new TextEncoder().encode('{"error":{"code":"server_error"}}'));
            controller.close();
          },
        }, { highWaterMark: 0 }), // pull only when someone reads
        { status: 500 },
      ),
  );
  const { result, lines } = await runUncached({ fetch: fetchImpl, colo: () => "NRT" });
  assert.deepEqual(result.providers.openai, { ok: false, error: "HTTP 500" });
  const [statusLine] = linesFor(lines, "api.openai.com");
  assert.equal(statusLine, "[model-list] api.openai.com/v1/models -> HTTP 500 colo=NRT");
  assert.ok(!bodyRead, "a 500 body was read");
});
