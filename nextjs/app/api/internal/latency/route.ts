import { NextRequest, NextResponse } from "next/server";

import { timingSafeEqualSecret } from "@/lib/security/timing-safe-secret";
import { latencyIngestRateLimiter } from "@/lib/rate-limit";
import { bucketForSeconds } from "@/lib/latency/types";
import { db } from "@/src/db";
import { sttLatencySamples } from "@/src/db/schema/stt-latency-samples";
import { MAX_SAMPLES_PER_REQUEST, coarseCreatedAt, validateBatch } from "./validation";

export const dynamic = "force-dynamic";

/** A 20-sample batch is a few KB. Anything larger is not a real report. */
const MAX_BODY_BYTES = 32 * 1024;

/**
 * Internal write: stores anonymous per-provider timing rows from the edge
 * transcription service. One request carries the whole fallback chain of one
 * transcription (1-3 samples), because the edge sends the batch once, after the
 * response is already flushed to the user.
 *
 * Same x-internal-secret gate (timing-safe, 401 on bad secret) as the other
 * internal routes. Rows carry no user id, key, request id, or IP — see
 * src/db/schema/stt-latency-samples.ts for what is deliberately absent.
 */
export async function POST(request: NextRequest) {
  const secret = request.headers.get("x-internal-secret");
  if (!timingSafeEqualSecret(secret, process.env.HYPERWHISPER_INTERNAL_SECRET)) {
    return NextResponse.json({ error: "Unauthorized" }, { status: 401 });
  }

  const declaredLength = Number(request.headers.get("content-length") ?? 0);
  if (declaredLength > MAX_BODY_BYTES) {
    return NextResponse.json({ error: "Payload too large" }, { status: 413 });
  }

  let body: unknown;
  try {
    const text = await request.text();
    if (text.length > MAX_BODY_BYTES) {
      return NextResponse.json({ error: "Payload too large" }, { status: 413 });
    }
    body = JSON.parse(text);
  } catch {
    return NextResponse.json({ error: "Invalid JSON" }, { status: 400 });
  }

  const result = validateBatch(body);
  if (!result.ok) {
    return NextResponse.json({ error: result.error }, { status: 400 });
  }

  // A dropped sample is a real signal, not routine noise: it usually means the
  // edge service has learned a provider or failure kind this route has not. The
  // reporter logs the same event on its side of the wire; without both, adding a
  // provider silently deletes it from the public page.
  if (result.skipped.length > 0) {
    console.warn("latency ingest skipped samples:", {
      skipped: result.skipped.length,
      received: result.skipped.length + result.samples.length,
      // Array.from, not a spread: this tsconfig targets es5, where spreading a
      // Set is a type error (TS2802) rather than a runtime one.
      reasons: Array.from(new Set(result.skipped.map((entry) => entry.reason))),
    });
  }

  // Keyed by region, not by IP: every machine in one Fly region shares an exit
  // address, so a per-IP limit would throttle the busiest region against itself.
  // This is a runaway-loop backstop, nothing more — the ceiling is far above
  // real traffic.
  const region = result.samples[0]?.flyRegion ?? "unknown";
  const { success } = await latencyIngestRateLimiter.limit(region);
  if (!success) {
    return NextResponse.json({ error: "Rate limit exceeded" }, { status: 429 });
  }

  if (result.samples.length === 0) {
    return NextResponse.json({ inserted: 0, skipped: result.skipped });
  }

  // Hour granularity, not `defaultNow()`: an exact transaction timestamp shared
  // by every row of one multi-row INSERT is a request identifier in all but
  // name. See coarseCreatedAt().
  const createdAt = coarseCreatedAt();

  try {
    await db.insert(sttLatencySamples).values(
      result.samples.map((sample) => ({
        provider: sample.provider,
        model: sample.model,
        flyRegion: sample.flyRegion,
        audioSeconds: sample.audioSeconds,
        // Bucketed here rather than on the page: the boundaries and the labels
        // that describe them live in one place (lib/latency/types.ts), and the
        // aggregate never has to bucket at read time.
        // audioSeconds is never null at this point — validateSample rejects a
        // sample without a clip length, because it could not be compared.
        durationBucket: bucketForSeconds(sample.audioSeconds ?? 0),
        latencyMs: sample.latencyMs,
        ok: sample.ok,
        failureKind: sample.failureKind,
        attempt: sample.attempt,
        createdAt,
      })),
    );
  } catch (error) {
    // A database failure is transient and worth a retry, so it must not look
    // like a rejected payload.
    console.error("Error in latency ingest:", error);
    return NextResponse.json({ error: "Internal server error" }, { status: 500 });
  }

  return NextResponse.json({
    inserted: result.samples.length,
    skipped: result.skipped,
    max: MAX_SAMPLES_PER_REQUEST,
  });
}