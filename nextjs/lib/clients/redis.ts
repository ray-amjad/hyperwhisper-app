import { Redis } from "@upstash/redis";

// This is the Next.js site's Upstash pair, and the `_REST_` infix in the two
// variable names is the only thing that separates it from the Fly
// transcription service's pair. That service reads UPSTASH_REDIS_URL /
// UPSTASH_REDIS_TOKEN in `hyperwhisper-cloud/src/lib/redis.ts` — no infix.
// Both go through the same @upstash/redis client over the same REST
// protocol, so a cross-wired value fails silently as a cache that answers
// the wrong service's keys rather than as a connection error. Keep the two
// name pairs distinct, and check which service you are in before you touch
// either one.
const redis = new Redis({
  url: process.env.UPSTASH_REDIS_REST_URL!,
  token: process.env.UPSTASH_REDIS_REST_TOKEN!,
});

export default redis;
