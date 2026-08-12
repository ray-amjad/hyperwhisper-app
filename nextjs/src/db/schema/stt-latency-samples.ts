import { boolean, index, integer, pgTable, text, timestamp, uuid } from "drizzle-orm/pg-core";

/**
 * Append-only, anonymous timing log. One row = one provider attempt made by the
 * edge transcription service (hyperwhisper-cloud), not one user request: a
 * request that falls back from ElevenLabs to Deepgram writes two rows.
 *
 * Deliberately absent: user id, license key, request id, IP, transcript,
 * character count, and the clip's exact length. Leaving out the request id is
 * what makes these rows anonymous rather than pseudonymous — nothing joins two
 * rows back into one session. `created_at` is written coarsened to the hour for
 * the same reason: one transcription's chain arrives in a single multi-row
 * INSERT, and an exact transaction timestamp would be a request identifier in
 * all but name. `audio_seconds` is left null on the same grounds — every
 * attempt on one transcription measures the same audio, so a whole-second
 * length beside the region and the hour is another way to group a chain back
 * together. Only the three-value `duration_bucket` survives, which is all the
 * page compares on. The cost is that a single request cannot be reconstructed
 * from this table; Axiom still holds that (short-retention, internal-only).
 *
 * Written by POST /api/internal/latency (x-internal-secret). Read only by the
 * public /latency page, which aggregates a trailing 30-day window. Rows are
 * pruned at 1 year by /api/internal/latency/prune.
 */
export const sttLatencySamples = pgTable(
  "stt_latency_samples",
  {
    id: uuid("id").defaultRandom().primaryKey(),
    // Backend provider id as the edge service knows it (deepgram, groq, grok,
    // elevenlabs, azure-mai, …) — NOT the catalog id (grokStt). The page maps
    // to display names through cloud-stt-catalog.json's `sttProvider` field.
    provider: text("provider").notNull(),
    // Which model the row is about, and it is NOT the same question on both
    // sides of `ok`: a successful attempt stores the model that actually RAN
    // (AssemblyAI's sync path runs universal-3-5-pro whatever was requested, so
    // this matches the X-STT-Model the caller got back), while a failed attempt
    // stores the model it was ATTEMPTED with, since no model ever ran. Null when
    // the provider takes no model id.
    //
    // Nothing reads this column yet — src/content/latency.ts groups by provider
    // and region only, and the page labels rows by vendor alone for exactly that
    // reason (lib/latency/providers.ts). It is written so cutting the aggregate
    // by model later needs a new query rather than a year of new data.
    model: text("model"),
    // process.env.FLY_REGION of the machine that ran the attempt. Always a real
    // three-letter Fly region: a machine off Fly does not report at all, and the
    // ingest rejects anything else.
    flyRegion: text("fly_region").notNull(),
    // Always null: the reported clip length picks the bucket below and is then
    // discarded, because a per-row length is a join key that would reassemble
    // one transcription's chain (see the note above). Kept nullable rather than
    // dropped so a future, deliberate use does not need a migration.
    audioSeconds: integer("audio_seconds"),
    // 'short' <10s | 'medium' 10-30s | 'long' >30s, from the edge service's
    // estimate of the clip length. Denormalized at write time so the page's
    // aggregate never has to bucket at read time.
    durationBucket: text("duration_bucket").notNull(),
    // ONE attempt end to end, on the edge route's own clock: everything that
    // attempt spent — handing the audio to the provider, creating a job, and
    // every poll waiting on it — and nothing before it, so no client upload, no
    // auth, no credit check, and no earlier attempt in the fallback chain.
    //
    // Deliberately NOT ProviderUnavailableError.elapsedMs. For the async
    // providers (AssemblyAI, Soniox, Google Chirp) that field times only the
    // single fetch that failed, so a 90-second wait arrives as the 8 seconds of
    // its last poll. If this comment ever reads "the provider call alone" again,
    // someone has "fixed" the route back to that bug — hyperwhisper-cloud's
    // transcribe.test.ts pins the difference.
    latencyMs: integer("latency_ms").notNull(),
    ok: boolean("ok").notNull(),
    // timeout | rate_limit | upstream_5xx | bad_response | network_error |
    // input_rejected | unknown. Null when ok.
    failureKind: text("failure_kind"),
    // 1-based position in the fallback chain.
    attempt: integer("attempt").notNull(),
    // Written by the ingest route, truncated to the hour, so the rows of one
    // transcription cannot be told apart from every other row in that hour and
    // region. The default is a backstop for a direct write; nothing but the
    // ingest route writes here.
    createdAt: timestamp("created_at", { withTimezone: true })
      .notNull()
      .defaultNow(),
  },
  (table) => [
    // Serves the page's only query: `duration_bucket = $1 AND created_at >=
    // now() - 30 days`, grouped by provider and region.
    //
    // The EQUALITY column leads, then the range column. A btree can only bound
    // its scan on the leading column, so leading with created_at made every one
    // of the three per-revalidation calls walk the whole 30-day range and throw
    // away the ~2/3 of it belonging to the other two buckets. This way each call
    // descends straight to its bucket and scans only that bucket's window.
    index("stt_latency_samples_agg_idx").on(
      table.durationBucket,
      table.createdAt,
      table.provider,
      table.flyRegion,
    ),
    // Retention delete scans on age alone — and with duration_bucket now leading
    // the index above, this is the only one that can serve it. Not redundant.
    index("stt_latency_samples_created_at_idx").on(table.createdAt),
  ],
);

export type SttLatencySampleRow = typeof sttLatencySamples.$inferSelect;
