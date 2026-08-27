import assert from "node:assert/strict";
import test from "node:test";

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
