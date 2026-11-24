// UPSTASH REDIS CLIENT FACTORY
//
// The env-reading, client-building edge that `./redis` used to inline. It
// lives in its own module because a dozen suites `mock.module('./redis')`,
// which replaces that module process-wide in bun, so no test could ever run
// the real getter. Nothing mocks this module, and both dependencies arrive as
// defaulted parameters, so `redis-client.test.ts` drives the real code.

import { Redis } from '@upstash/redis';

/**
 * The Upstash env is missing. Callers that fail open (rate limits, credit
 * holds) check for this type to log "not configured" once instead of on every
 * request.
 */
export class RedisNotConfiguredError extends Error {
  constructor() {
    super('UPSTASH_REDIS_CLOUD_URL and UPSTASH_REDIS_CLOUD_TOKEN are required');
    // `name` stays 'Error' on purpose: log lines print `${name}: ${message}`,
    // and they must read exactly as they did before this type existed.
    // Callers detect it with `instanceof`, never by name.
  }
}

export type RedisClientFactory = (opts: { url: string; token: string }) => Redis;

// The transcription service's own Upstash database. The Next.js site has a
// separate one behind UPSTASH_REDIS_SITE_* (`nextjs/lib/clients/redis.ts`).
// Both go through the same @upstash/redis client against the same REST
// protocol, so a swapped value fails silently — as a cache answering the
// other service's keys, not as a connection error. The SITE / CLOUD segment
// is the only thing separating them; keep it accurate.
export function createRedisGetter(
  env: Record<string, string | undefined> = process.env,
  makeClient: RedisClientFactory = (opts) => new Redis(opts)
): () => Redis {
  // Lazy: nothing is read or built until the first call, so a process (or a
  // test) that never touches Redis needs no Upstash env at all.
  let client: Redis | null = null;
  return () => {
    if (!client) {
      const url = env.UPSTASH_REDIS_CLOUD_URL;
      const token = env.UPSTASH_REDIS_CLOUD_TOKEN;

      if (!url || !token) {
        throw new RedisNotConfiguredError();
      }

      client = makeClient({ url, token });
    }
    return client;
  };
}
