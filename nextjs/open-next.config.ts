import { defineCloudflareConfig } from "@opennextjs/cloudflare";
import r2IncrementalCache from "@opennextjs/cloudflare/overrides/incremental-cache/r2-incremental-cache";
import doQueue from "@opennextjs/cloudflare/overrides/queue/do-queue";
import doShardedTagCache from "@opennextjs/cloudflare/overrides/tag-cache/do-sharded-tag-cache";

/**
 * OpenNext on Cloudflare Workers (DEPLOY-CLOUDFLARE.md).
 *
 * ISR needs all three: the blog, latency and choosing-a-model pages are
 * `force-static` with `revalidate`, and the add-blog-post webhook calls
 * `revalidatePath`, which needs a tag cache. Without a writable incremental
 * cache the pages would stay frozen at their build-time render, which has no
 * database (Workers Builds holds no DB credentials).
 *
 * No `withFilter(softTagFilter)` on the tag cache: it drops the `_N_T_` soft
 * tags that revalidatePath relies on.
 */
export default defineCloudflareConfig({
  incrementalCache: r2IncrementalCache,
  queue: doQueue,
  tagCache: doShardedTagCache({ baseShardSize: 12 }),
});
