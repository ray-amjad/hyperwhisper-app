import { NextResponse } from "next/server";

/**
 * The slice of `@upstash/ratelimit`'s `Ratelimit` a route gate needs. Typed
 * structurally so a route test can hand in a stub without building a client.
 */
export interface RequestLimiter {
  limit(identifier: string): Promise<{
    success: boolean;
    /** Unix ms at which the window frees a slot. */
    reset?: number;
    /** `"timeout"` when @upstash/ratelimit gave up waiting and let the call through. */
    reason?: string;
  }>;
}

/** Used when the limiter does not say when the window frees: every site window is 1 minute. */
const DEFAULT_RETRY_AFTER_SECONDS = 60;

export function retryAfterSeconds(
  reset: number | undefined,
  now: number = Date.now(),
): number {
  if (typeof reset !== "number" || !Number.isFinite(reset)) {
    return DEFAULT_RETRY_AFTER_SECONDS;
  }

  return Math.max(1, Math.ceil((reset - now) / 1000));
}

/**
 * Count one request against `limiter` and return the 429 to send, or `null` to
 * carry on.
 *
 * FAILS OPEN (#2010): if Upstash throws (unreachable, misconfigured), the
 * request is served and a warning is logged. @upstash/ratelimit already
 * resolves `{ success: true, reason: "timeout" }` on a slow Upstash; that is
 * logged too, so a disabled limiter is visible. The log line carries the
 * error's class name only — an Upstash error message can quote the command,
 * and with it the caller's IP or key.
 */
export async function rateLimitGate(
  limiter: RequestLimiter,
  identifier: string,
  label: string,
): Promise<NextResponse | null> {
  let verdict: Awaited<ReturnType<RequestLimiter["limit"]>>;

  try {
    verdict = await limiter.limit(identifier);
  } catch (error) {
    const name = error instanceof Error ? error.name : typeof error;

    console.warn(`Rate limit unavailable for ${label}, failing open: ${name}`);

    return null;
  }

  if (verdict.reason === "timeout") {
    console.warn(`Rate limit timed out for ${label}, failing open`);
  }
  if (verdict.success) return null;

  const retryAfter = retryAfterSeconds(verdict.reset);

  return NextResponse.json(
    { error: "Too many requests. Please try again later." },
    { status: 429, headers: { "Retry-After": String(retryAfter) } },
  );
}
