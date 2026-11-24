// @ts-check

/**
 * The site's scheduled jobs: cron expression -> internal route.
 *
 * wrangler.jsonc `triggers.crons` lists the same expressions, and worker.ts's
 * `scheduled()` handler looks the route up here and calls it through the Next
 * handler with `Authorization: Bearer $CRON_SECRET`
 * (lib/security/internal-cron-auth.ts accepts that). Times are UTC.
 * tests/cron-jobs.test.ts keeps this map and wrangler.jsonc in step.
 *
 * @type {Readonly<Record<string, string>>}
 */
export const CRON_JOBS = Object.freeze({
  "17 4 * * *": "/api/internal/latency/prune",
  "37 4 * * *": "/api/internal/sessions/prune",
});
