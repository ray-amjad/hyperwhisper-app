import { Redis } from "@upstash/redis";

// The site's own Upstash database. The transcription service has a separate
// one behind UPSTASH_REDIS_CLOUD_* (`hyperwhisper-cloud/src/lib/redis.ts`).
// Both go through the same @upstash/redis client against the same REST
// protocol, so a swapped value fails silently — as a cache answering the
// other service's keys, not as a connection error. The SITE / CLOUD segment
// is the only thing separating them; keep it accurate.
const redis = new Redis({
  url: process.env.UPSTASH_REDIS_SITE_URL!,
  token: process.env.UPSTASH_REDIS_SITE_TOKEN!,
});

export default redis;
