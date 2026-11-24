/**
 * #2636: the Mintlify docs are external rewrites on the site's own origin, and
 * OpenNext forwards every request header to Mintlify and copies every response
 * header back. worker.ts strips the site's credentials on those paths
 * (lib/cloudflare/docs-proxy-headers.ts) and leaves every other path alone.
 *
 * The worker tests run the real worker.ts `fetch` with the OpenNext handler
 * (`.open-next/worker.js`, made only by a Cloudflare build) swapped for a stub
 * upstream that records the request it got. The pairing tests read the real
 * next.config.mjs rewrites through Next's own loader and the regex Next writes
 * into routes-manifest.json, which OpenNext matches against.
 */
import assert from "node:assert/strict";
import * as nodeModule from "node:module";
import test from "node:test";

import type { NextConfig } from "next";

import loadCustomRoutes from "next/dist/lib/load-custom-routes";
import { buildCustomRoute as buildManifestRoute } from "next/dist/lib/build-custom-route";

import {
  isDocsProxyPath,
  withoutDocsCookies,
  withoutDocsCredentials,
} from "../lib/cloudflare/docs-proxy-headers";

const OPEN_NEXT_STUB = new URL(
  "./fixtures/open-next-worker-stub.mjs",
  import.meta.url,
).href;

// `module.registerHooks` (Node 22.15+) is newer than this repo's @types/node.
type ResolveResult = { url: string; shortCircuit?: boolean };
const { registerHooks } = nodeModule as unknown as {
  registerHooks(hooks: {
    resolve(
      specifier: string,
      context: unknown,
      nextResolve: (specifier: string, context: unknown) => ResolveResult,
    ): ResolveResult;
  }): void;
};

// `mock.module` cannot stand in for a file that does not exist, so resolve the
// generated handler to a stub instead.
registerHooks({
  resolve(specifier, context, nextResolve) {
    if (specifier === "./.open-next/worker.js") {
      return { url: OPEN_NEXT_STUB, shortCircuit: true };
    }
    return nextResolve(specifier, context);
  },
});

type Upstream = (request: Request) => Response | Promise<Response>;
type Worker = {
  fetch(request: Request, env: object, ctx: object): Promise<Response>;
};

const ORIGIN = "https://www.hyperwhisper.com";
// Built at run time: a credential-looking literal in a key-named const would
// trip gitleaks on the PR.
const SESSION = ["session", "token", "fixture"].join("-");
const SESSION_COOKIE = `__Secure-better-auth.session_token=${SESSION}; theme=dark`;
const BEARER = `Bearer ${["bearer", "fixture"].join("-")}`;
const BASIC = `Basic ${Buffer.from("user:pass").toString("base64")}`;

let worker: Worker | undefined;

/** Runs `request` through worker.ts with `upstream` as the OpenNext handler. */
async function runWorker(request: Request, upstream: Upstream) {
  let seen: Request | undefined;

  (globalThis as { __openNextStub?: unknown }).__openNextStub = async (
    req: Request,
  ) => {
    seen = req;
    return upstream(req);
  };
  // A non-literal specifier keeps tsc out of worker.ts (excluded in
  // tsconfig: it imports the generated handler).
  const specifier = "../worker.ts";
  worker ??= ((await import(specifier)) as { default: Worker }).default;

  const response = await worker.fetch(request, {}, { waitUntil() {} });

  assert.ok(seen, "the OpenNext handler was called");
  return { seen, response };
}

function credentialed(path: string, init: RequestInit = {}) {
  return new Request(`${ORIGIN}${path}`, {
    ...init,
    headers: {
      cookie: SESSION_COOKIE,
      authorization: BEARER,
      "proxy-authorization": BASIC,
      accept: "text/html",
      "accept-encoding": "gzip, br",
      "user-agent": "docs-proxy-test",
      "x-forwarded-for": "203.0.113.9",
      ...(init.headers as Record<string, string> | undefined),
    },
  });
}

function upstreamSetting(
  cookies: string[],
  extra: Record<string, string> = {},
) {
  return () => {
    const headers = new Headers({
      "content-type": "text/html; charset=utf-8",
      "x-mintlify-client-version": "0.0.1",
      "cache-control": "public, max-age=60",
      ...extra,
    });
    for (const cookie of cookies) headers.append("set-cookie", cookie);
    return new Response("<html>docs</html>", { status: 200, headers });
  };
}

// --- The path filter --------------------------------------------------------

const DOCS_PATHS = [
  "/docs",
  "/docs/",
  "/docs/models",
  "/docs/api-reference/local-api/overview",
  "/docs/llms.txt",
  "/docs.rsc",
  "/_mintlify/static/chunk.js",
  "/_mintlify/api/search",
  "/_mintlify",
  "/mintlify-assets/_next/static/x.css",
  "/api/request",
  "/api/request/",
];

const SITE_PATHS = [
  "/",
  "/en",
  "/en/docs",
  "/de/docs/models",
  "/docsx",
  "/docs-old",
  "/docs.rscx",
  "/xdocs",
  "/_mintlifyx/a",
  "/mintlify-assetsx/a",
  "/api/requests",
  "/api/request/x",
  "/api/requestx",
  "/api/customer/profile",
  "/api/auth/get-session",
  "/api/auth/sign-in/email",
  "/api/license/validate",
  "/api/mcp/admin",
  "/api/webhooks/stripe",
  "/_next/static/chunks/main.js",
  "/account",
];

test("docs proxy paths are matched", () => {
  for (const path of DOCS_PATHS)
    assert.equal(isDocsProxyPath(path), true, path);
});

test("site paths, /docsx and the account API are not matched", () => {
  for (const path of SITE_PATHS) {
    assert.equal(isDocsProxyPath(path), false, path);
  }
});

// --- Pairing with next.config.mjs's rewrites -------------------------------

const loadRewrites = async () => {
  // The config imports the env validator unless this is set.
  process.env.SKIP_ENV_VALIDATION = "1";
  // A non-literal specifier keeps tsc out of next.config.mjs (top-level await).
  const specifier = "../next.config.mjs";
  const config = ((await import(specifier)) as { default: NextConfig }).default;
  const { rewrites } = await loadCustomRoutes(config);
  return [
    ...rewrites.beforeFiles,
    ...rewrites.afterFiles,
    ...rewrites.fallback,
  ];
};

/** Concrete paths a rewrite source can match, from its own text. */
function samplesFor(source: string): string[] {
  const fills = source.endsWith("+")
    ? ["x", "a/b", "a.b/c.js"]
    : ["", "x", "a/b", "a.b/c.js"];
  const bases = /:\w+[*+]$/.test(source)
    ? fills.map((fill) => source.replace(/\/:\w+[*+]$/, fill ? `/${fill}` : ""))
    : [source];

  return bases.flatMap((path) => [path, `${path}/`, path.toUpperCase()]);
}

test("every external rewrite in next.config.mjs is covered by the filter", async () => {
  const rewrites = await loadRewrites();
  const external = rewrites.filter(({ destination }) =>
    /^https?:\/\//.test(destination),
  );

  assert.ok(external.length >= 6, "the Mintlify rewrites are loaded");
  const corpus = [
    ...external.flatMap(({ source }) => samplesFor(source)),
    ...DOCS_PATHS,
    ...SITE_PATHS,
  ];

  for (const rewrite of external) {
    const { regex } = buildManifestRoute("rewrite", rewrite);
    // OpenNext compiles the manifest regex with no flags; Next itself matches
    // sources case-insensitively. Cover both.
    const matchers = [new RegExp(regex), new RegExp(regex, "i")];
    const matched = corpus.filter((path) => matchers.some((m) => m.test(path)));

    assert.ok(matched.length > 0, `${rewrite.source} matched a sample`);
    for (const path of matched) {
      assert.equal(
        isDocsProxyPath(path),
        true,
        `${path} goes to ${rewrite.destination} but keeps its credentials`,
      );
    }
  }
});

test("every path the filter matches is one a rewrite sends to Mintlify", async () => {
  const rewrites = await loadRewrites();
  const regexes = rewrites
    .filter(({ destination }) => /^https?:\/\//.test(destination))
    .map((rewrite) => new RegExp(buildManifestRoute("rewrite", rewrite).regex));

  for (const path of DOCS_PATHS) {
    assert.ok(
      regexes.some((regex) => regex.test(path)),
      `${path} is filtered but no rewrite proxies it`,
    );
  }
});

// --- The worker's fetch handler --------------------------------------------

for (const path of [
  "/docs",
  "/docs/models",
  "/_mintlify/static/x.js",
  "/mintlify-assets/a.png",
  "/docs.rsc",
]) {
  test(`GET ${path}: Mintlify gets no Cookie or Authorization`, async () => {
    const { seen } = await runWorker(credentialed(path), upstreamSetting([]));

    assert.equal(seen.headers.get("cookie"), null);
    assert.equal(seen.headers.get("authorization"), null);
    assert.equal(seen.headers.get("proxy-authorization"), null);
    // Everything else still goes through.
    assert.equal(seen.headers.get("accept"), "text/html");
    assert.equal(seen.headers.get("user-agent"), "docs-proxy-test");
    assert.equal(seen.headers.get("x-forwarded-for"), "203.0.113.9");
    assert.equal(seen.url, `${ORIGIN}${path}`);
    assert.equal(seen.method, "GET");
  });
}

test("POST /api/request (the playground proxy) keeps its method and body", async () => {
  const body = JSON.stringify({
    method: "GET",
    url: "http://127.0.0.1:39201/v1/health",
    header: { Authorization: "Bearer typed-into-the-playground" },
  });
  const { seen } = await runWorker(
    credentialed("/api/request", {
      method: "POST",
      body,
      headers: { "content-type": "application/json" },
    }),
    () =>
      new Response("{}", { headers: { "content-type": "application/json" } }),
  );

  assert.equal(seen.method, "POST");
  assert.equal(seen.headers.get("cookie"), null);
  assert.equal(seen.headers.get("authorization"), null);
  assert.equal(seen.headers.get("content-type"), "application/json");
  // The key a reader types into the playground travels in the JSON body.
  assert.equal(await seen.text(), body);
});

test("a docs Set-Cookie (all of them) never reaches the browser", async () => {
  const { response } = await runWorker(
    credentialed("/docs"),
    upstreamSetting(
      [
        "__Host-mintlify=1; Path=/; Secure",
        "__Secure-better-auth.session_token=; Path=/; Max-Age=0",
      ],
      {
        "clear-site-data": '"cookies", "storage"',
        "service-worker-allowed": "/",
      },
    ),
  );

  assert.equal(response.headers.get("set-cookie"), null);
  assert.deepEqual(response.headers.getSetCookie(), []);
  assert.equal(response.headers.get("clear-site-data"), null);
  assert.equal(response.headers.get("service-worker-allowed"), null);
  assert.equal(response.status, 200);
  assert.equal(response.headers.get("x-mintlify-client-version"), "0.0.1");
  assert.equal(response.headers.get("cache-control"), "public, max-age=60");
  assert.equal(await response.text(), "<html>docs</html>");
});

test("a docs response still loses Content-Encoding for a client that cannot decode it", async () => {
  const { response } = await runWorker(
    new Request(`${ORIGIN}/docs/llms.txt`, {
      headers: { cookie: SESSION_COOKIE },
    }),
    upstreamSetting(["a=1; Path=/"], { "content-encoding": "gzip" }),
  );

  assert.equal(response.headers.get("set-cookie"), null);
  assert.equal(response.headers.get("content-encoding"), null);
});

test("/api/customer/profile keeps its Cookie and Authorization, as the same Request", async () => {
  const request = credentialed("/api/customer/profile");
  const { seen } = await runWorker(request, upstreamSetting([]));

  assert.equal(seen, request);
  assert.equal(seen.headers.get("cookie"), SESSION_COOKIE);
  assert.equal(seen.headers.get("authorization"), BEARER);
  assert.equal(seen.headers.get("proxy-authorization"), BASIC);
});

for (const path of [
  "/docsx",
  "/en/docs",
  "/api/requests",
  "/api/auth/get-session",
]) {
  test(`${path} is not a docs path: its Cookie goes through`, async () => {
    const { seen } = await runWorker(credentialed(path), upstreamSetting([]));

    assert.equal(seen.headers.get("cookie"), SESSION_COOKIE);
    assert.equal(seen.headers.get("authorization"), BEARER);
  });
}

test("a Set-Cookie from /api/auth/* reaches the browser, as the same Response", async () => {
  const upstream = upstreamSetting(
    [
      `__Secure-better-auth.session_token=${SESSION}; Path=/; HttpOnly; Secure`,
      "__Secure-better-auth.session_data=x; Path=/",
    ],
    { "clear-site-data": '"cookies"' },
  );
  let sent: Response | undefined;
  const { response } = await runWorker(
    credentialed("/api/auth/sign-in/email", { method: "POST", body: "{}" }),
    () => (sent = upstream()),
  );

  assert.equal(response, sent);
  assert.equal(response.headers.getSetCookie().length, 2);
  assert.equal(response.headers.get("clear-site-data"), '"cookies"');
});

test("a site page's Set-Cookie and headers are untouched", async () => {
  const { response } = await runWorker(
    credentialed("/en/account"),
    upstreamSetting(["NEXT_LOCALE=en; Path=/"]),
  );

  assert.deepEqual(response.headers.getSetCookie(), ["NEXT_LOCALE=en; Path=/"]);
});

// --- The helpers' no-op paths ------------------------------------------------

test("a docs request with no credentials is passed on as the same object", () => {
  const request = new Request(`${ORIGIN}/docs`, { headers: { accept: "*/*" } });
  assert.equal(withoutDocsCredentials(request), request);
});

test("a docs response with nothing to strip is passed on as the same object", () => {
  const request = new Request(`${ORIGIN}/docs`);
  const response = new Response("ok", {
    headers: { "content-type": "text/plain" },
  });
  assert.equal(withoutDocsCookies(request, response), response);
});

test("a docs 304 with a Set-Cookie keeps its status and loses the cookie", () => {
  const request = new Request(`${ORIGIN}/docs`);
  const response = new Response(null, {
    status: 304,
    headers: { "set-cookie": "a=1", etag: '"v1"' },
  });
  const out = withoutDocsCookies(request, response);
  assert.equal(out.status, 304);
  assert.equal(out.headers.get("set-cookie"), null);
  assert.equal(out.headers.get("etag"), '"v1"');
});
