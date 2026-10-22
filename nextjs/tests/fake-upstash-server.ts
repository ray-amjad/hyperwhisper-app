/**
 * A local HTTP server that speaks the Upstash REST protocol and runs each
 * command on `ioredis-mock`, which executes Lua in an embedded VM. Point the
 * REAL `@upstash/redis` client at `listen(upstashRestServer())` and the REAL
 * `@upstash/ratelimit` Lua scripts count exactly as production Redis would.
 *
 * Shared by tests/rate-limit.test.ts and tests/auth-rate-limit.test.ts. The
 * store is module state: one per test file (node --test runs each file in its
 * own process).
 */
import { createHash } from "node:crypto";
import http from "node:http";
import type { AddressInfo } from "node:net";
import { createRequire } from "node:module";

import type Redis from "ioredis";

type Command = (string | number)[];

// Loaded through `require` so `tsc` never resolves the module's types:
// `@types/ioredis-mock` is only a transitive dependency, which npm hoists
// and pnpm (the Vercel build) does not, so a static import fails the
// production build with TS7016. ioredis-mock implements the ioredis API.
const RedisMock = createRequire(import.meta.url)("ioredis-mock") as new () => Redis;

export const store = new RedisMock();
const scripts = new Map<string, string>();
/** The Authorization header of every request the server received. */
export const siteAuthHeaders: (string | undefined)[] = [];

/** Upstash answers in base64 when the client asks for it, as this one does. */
function encode(value: unknown): unknown {
  if (typeof value === "string") return Buffer.from(value).toString("base64");
  if (Array.isArray(value)) return value.map(encode);
  return value;
}

/**
 * One Redis command, as a Redis 7 server would run it. EVAL caches the
 * script under its SHA-1 so a later EVALSHA finds it, and an unknown SHA
 * answers NOSCRIPT — the path the ratelimit client takes on its first call.
 * Redis strips a `#!lua` shebang line before it compiles the body; the
 * embedded VM does not, so it is stripped here.
 */
async function runCommand(command: Command): Promise<unknown> {
  const [rawName, ...args] = command.map(String);
  const name = rawName.toLowerCase();
  if (name === "eval" || name === "evalsha") {
    let sha = args[0];
    if (name === "eval") {
      sha = createHash("sha1").update(args[0]).digest("hex");
      scripts.set(sha, args[0]);
    }
    const source = scripts.get(sha);
    if (!source) throw new Error("NOSCRIPT No matching script.");
    const [numKeys, ...keysAndArgs] = args.slice(1);
    return store.eval(source.replace(/^#![^\n]*\n/, ""), numKeys, ...keysAndArgs);
  }
  const fn = (store as unknown as Record<string, (...a: string[]) => Promise<unknown>>)[name];
  if (typeof fn !== "function") throw new Error(`ERR unknown command '${rawName}'`);
  return fn.apply(store, args);
}

export function upstashRestServer(): http.Server {
  return http.createServer(async (req, res) => {
    let body = "";
    for await (const chunk of req) body += chunk;
    siteAuthHeaders.push(req.headers.authorization);
    const parsed = JSON.parse(body) as Command | Command[];
    res.setHeader("content-type", "application/json");
    if (req.url === "/pipeline" || req.url === "/multi-exec") {
      const replies = [];
      for (const command of parsed as Command[]) {
        try {
          replies.push({ result: encode(await runCommand(command)) });
        } catch (err) {
          replies.push({ error: (err as Error).message });
        }
      }
      res.end(JSON.stringify(replies));
      return;
    }
    try {
      res.end(JSON.stringify({ result: encode(await runCommand(parsed as Command)) }));
    } catch (err) {
      res.statusCode = 400;
      res.end(JSON.stringify({ error: (err as Error).message }));
    }
  });
}

export async function listen(server: http.Server): Promise<string> {
  await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
  return `http://127.0.0.1:${(server.address() as AddressInfo).port}`;
}
