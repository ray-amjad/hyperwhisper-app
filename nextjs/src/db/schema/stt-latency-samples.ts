import { boolean, index, integer, pgTable, text, timestamp, uuid } from "drizzle-orm/pg-core";

/**
 * Append-only, anonymous timing log. One row = one provider attempt made by the
 * edge transcription service (hyperwhisper-cloud), not one user request: a
 * request that falls back from ElevenLabs to Deepgram writes two rows.
 *
 * Deliberately absent: user id, license key, request id, IP, transcript, and
 * character count. Leaving out the request id is what makes these rows
 * anonymous rather than pseudonymous — nothing joins two rows back into one
 * session. `created_at` is written coarsened to the hour for the same reason:
 * one transcription's chain arrives in a single multi-row INSERT, and an exact
 * transaction timestamp would be a request identifier in all but name. The cost
 * is that a single request cannot be reconstructed from this table; Axiom still
 * holds that (short-retention, internal-only).
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
    // The model actually attempted. Null when the provider takes no model id.
    model: text("model"),
    // process.env.FLY_REGION of the machine that ran the attempt. Always a real
    // three-letter Fly region: a machine off Fly does not report at all, and the
    // ingest rejects anything else.
    flyRegion: text("fly_region").notNull(),
    // Rounded clip length. Measured for successes, estimated from byte size for
    // failures (a failed attempt returns no duration) — see latency-report.ts.
    audioSeconds: integer("audio_seconds"),
    // 'short' <10s | 'medium' 10-30s | 'long' >30s. Denormalized at write time
    // so the page's aggregate never has to bucket at read time.
    durationBucket: text("duration_bucket").notNull(),
    // The provider call alone. Excludes upload, auth, credit checks, and every
    // earlier attempt in the fallback chain.
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
    // Serves the page's only query: a 30-day window filtered to one duration
    // bucket, grouped by provider and region. Column order matches that shape.
    index("stt_latency_samples_agg_idx").on(
      table.createdAt,
      table.durationBucket,
      table.provider,
      table.flyRegion,
    ),
    // Retention delete scans on age alone.
    index("stt_latency_samples_created_at_idx").on(table.createdAt),
  ],
);

export type SttLatencySampleRow = typeof sttLatencySamples.$inferSelect;
