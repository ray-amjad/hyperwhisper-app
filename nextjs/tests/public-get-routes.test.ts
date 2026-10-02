/**
 * Two small public GET routes with no auth in front of them.
 *
 * `GET /api/geo/nearest-region` tells the latency matrix and the model picker
 * which region column is closest to the visitor. It reads Vercel's edge
 * coordinate headers and a client-supplied list of region codes. The traps are
 * in the parsing: a blank header must not become 0 (a real coordinate off West
 * Africa), a hostile `regions` list must be filtered and capped, and the answer
 * must stay private to the visitor.
 *
 * `GET /api/internal/models` fans out to 6 paid provider APIs. It must go
 * through the cached model list, and it must let the edge cache hold the
 * answer, or every anonymous hit costs 6 upstream calls.
 *
 * Both routes are imported for real. The only boundary replaced is
 * `globalThis.fetch` (the provider APIs) and `process.env` (the provider keys).
 */
import assert from "node:assert/strict";
import { after, before, describe, test } from "node:test";

import { NextRequest } from "next/server";

import { GET as getNearestRegion } from "../app/api/geo/nearest-region/route";
import { GET as getInternalModels } from "../app/api/internal/models/route";

const ORIGIN = "https://preview-42.example.test";

// Berlin. Frankfurt is the closest of the regions used below.
const BERLIN = { lat: "52.52", lon: "13.40" };

function nearest(
  regions: string | null,
  headers: Record<string, string> = {
    "x-vercel-ip-latitude": BERLIN.lat,
    "x-vercel-ip-longitude": BERLIN.lon,
  },
): Promise<{ status: number; cache: string | null; body: unknown }> {
  const query = regions === null ? "" : `?regions=${encodeURIComponent(regions)}`;
  const response = getNearestRegion(
    new NextRequest(`${ORIGIN}/api/geo/nearest-region${query}`, { headers }),
  );
  return response.json().then((body) => ({
    status: response.status,
    cache: response.headers.get("cache-control"),
    body,
  }));
}

const NO_ANSWER = { region: null, city: null };

describe("GET /api/geo/nearest-region", () => {
  test("picks the closest candidate and names its city", async () => {
    const result = await nearest("iad,fra,syd");

    assert.equal(result.status, 200);
    assert.deepEqual(result.body, { region: "fra", city: "Frankfurt" });
  });

  test("keeps every answer private to the visitor, for 5 minutes", async () => {
    assert.equal((await nearest("iad,fra")).cache, "private, max-age=300");
    assert.equal((await nearest(null)).cache, "private, max-age=300");
  });

  test("answers null, not Null Island, when a coordinate header is blank", async () => {
    // Number("") is 0. A blank latitude read as 0 puts Berlin's longitude on
    // the equator, and the route would then highlight a real column.
    for (const blank of ["", "   "]) {
      const latBlank = await nearest("iad,fra,syd", {
        "x-vercel-ip-latitude": blank,
        "x-vercel-ip-longitude": BERLIN.lon,
      });
      assert.deepEqual(latBlank.body, NO_ANSWER, `latitude ${JSON.stringify(blank)}`);

      const lonBlank = await nearest("iad,fra,syd", {
        "x-vercel-ip-latitude": BERLIN.lat,
        "x-vercel-ip-longitude": blank,
      });
      assert.deepEqual(lonBlank.body, NO_ANSWER, `longitude ${JSON.stringify(blank)}`);
    }
  });

  test("answers null when a coordinate header is absent (local dev)", async () => {
    assert.deepEqual(
      (await nearest("iad,fra", { "x-vercel-ip-longitude": BERLIN.lon })).body,
      NO_ANSWER,
    );
    assert.deepEqual(
      (await nearest("iad,fra", { "x-vercel-ip-latitude": BERLIN.lat })).body,
      NO_ANSWER,
    );
    assert.deepEqual((await nearest("iad,fra", {})).body, NO_ANSWER);
  });

  test("answers null when a coordinate header is not a finite number", async () => {
    for (const bad of ["abc", "Infinity", "-Infinity", "NaN"]) {
      const result = await nearest("iad,fra", {
        "x-vercel-ip-latitude": bad,
        "x-vercel-ip-longitude": BERLIN.lon,
      });
      assert.deepEqual(result.body, NO_ANSWER, bad);
    }
  });

  test("answers null when no usable region is given", async () => {
    assert.deepEqual((await nearest(null)).body, NO_ANSWER);
    assert.deepEqual((await nearest("")).body, NO_ANSWER);
    assert.deepEqual((await nearest("fra1,../x,ab,f-a")).body, NO_ANSWER);
  });

  test("normalises case and whitespace in the region list", async () => {
    assert.deepEqual((await nearest(" IAD , Fra ")).body, {
      region: "fra",
      city: "Frankfurt",
    });
  });

  test("drops a malformed code instead of matching a prefix of it", async () => {
    // `fra1` is not `fra`; with it dropped, Ashburn is the only candidate.
    assert.deepEqual((await nearest("fra1,iad")).body, {
      region: "iad",
      city: "Ashburn",
    });
  });

  test("a malformed code does not use up one of the 60 slots", async () => {
    // Filter first, cap second. A client (or a hostile link) that pads the
    // list with junk must not push the real regions past the cap.
    const junk = Array.from({ length: 60 }, (_, i) => `fra${i}`);

    assert.deepEqual((await nearest([...junk, "fra"].join(","))).body, {
      region: "fra",
      city: "Frankfurt",
    });
  });

  test("answers null when no candidate is a known region", async () => {
    assert.deepEqual((await nearest("nowhere,local")).body, NO_ANSWER);
  });

  test("reads at most 60 candidates", async () => {
    const filler = Array.from({ length: 59 }, (_, i) => `zz${String.fromCharCode(97 + (i % 26))}${"q".repeat(1 + Math.floor(i / 26))}`);

    // The 60th code is read.
    assert.deepEqual((await nearest([...filler, "fra"].join(","))).body, {
      region: "fra",
      city: "Frankfurt",
    });
    // The 61st is not.
    assert.deepEqual(
      (await nearest([...filler, "zzzz", "fra"].join(","))).body,
      NO_ANSWER,
    );
  });
});

describe("GET /api/internal/models", () => {
  const KEYS = [
    "OPENAI_API_KEY",
    "ANTHROPIC_API_KEY",
    "GEMINI_API_KEY",
    "GROQ_API_KEY",
    "XAI_API_KEY",
    "CEREBRAS_API_KEY",
  ] as const;
  const savedEnv: Record<string, string | undefined> = {};
  const realFetch = globalThis.fetch;
  const upstream: string[] = [];

  before(() => {
    for (const key of KEYS) {
      savedEnv[key] = process.env[key];
      process.env[key] = "test-key";
    }
    // GROQ has no key: its provider must report the gap, not call out.
    delete process.env.GROQ_API_KEY;

    globalThis.fetch = (async (input: RequestInfo | URL) => {
      const url = String(input);
      upstream.push(new URL(url).host);
      if (url.includes("api.openai.com")) {
        return new Response(JSON.stringify({ data: [{ id: "gpt-5" }] }), { status: 200 });
      }
      if (url.includes("api.cerebras.ai")) {
        return new Response("down", { status: 503 });
      }
      if (url.includes("generativelanguage")) {
        return new Response(JSON.stringify({ models: [] }), { status: 200 });
      }
      return new Response(JSON.stringify({ data: [] }), { status: 200 });
    }) as typeof fetch;
  });

  after(() => {
    globalThis.fetch = realFetch;
    for (const key of KEYS) {
      if (savedEnv[key] === undefined) delete process.env[key];
      else process.env[key] = savedEnv[key];
    }
  });

  test("returns every provider's result and lets the edge cache hold it", async () => {
    const response = await getInternalModels();

    assert.equal(response.status, 200);
    assert.equal(
      response.headers.get("cache-control"),
      "public, s-maxage=3600, stale-while-revalidate=86400",
    );

    const body = (await response.json()) as {
      fetchedAt: string;
      providers: Record<string, { ok: boolean; models?: unknown; error?: string }>;
    };
    assert.ok(!Number.isNaN(Date.parse(body.fetchedAt)), body.fetchedAt);
    assert.deepEqual(Object.keys(body.providers).sort(), [
      "anthropic",
      "cerebras",
      "gemini",
      "grok",
      "groq",
      "openai",
    ]);
    assert.deepEqual(body.providers.openai, {
      ok: true,
      models: [{ id: "gpt-5", display_name: "gpt-5" }],
    });
    assert.deepEqual(body.providers.groq, { ok: false, error: "missing GROQ_API_KEY" });
    assert.equal(body.providers.cerebras.ok, false);
    assert.equal(upstream.includes("api.groq.com"), false);
    assert.equal(upstream.length, 5);
  });

  test("a second request is served from the cache, with no upstream call", async () => {
    const before = upstream.length;
    const first = await (await getInternalModels()).json();
    const second = await (await getInternalModels()).json();

    assert.equal(upstream.length, before, "the route fanned out to the providers again");
    assert.deepEqual(second, first);
  });
});
