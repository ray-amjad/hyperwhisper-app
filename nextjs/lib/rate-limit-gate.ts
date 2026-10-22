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

type LimitVerdict = Awaited<ReturnType<RequestLimiter["limit"]>>;

export type RateLimitOutcome =
  | { limited: false }
  | { limited: true; retryAfterSeconds: number };

/**
 * Run one limiter check and say whether it refused, with the seconds until it
 * frees a slot. The one fail-open policy for every site limiter, Better Auth's
 * included (#2010).
 *
 * FAILS OPEN: if the check throws (Upstash unreachable, misconfigured), the
 * request is allowed and a warning is logged. @upstash/ratelimit already
 * resolves `{ success: true, reason: "timeout" }` on a slow Upstash; that is
 * logged too, so a disabled limiter is visible. The log line carries the
 * error's class name only — an Upstash error message can quote the command,
 * and with it the caller's IP or key.
 */
export async function countFailOpen(
  check: () => Promise<LimitVerdict>,
  label: string,
): Promise<RateLimitOutcome> {
  let verdict: LimitVerdict;

  try {
    verdict = await check();
  } catch (error) {
    const name = error instanceof Error ? error.name : typeof error;

    console.warn(`Rate limit unavailable for ${label}, failing open: ${name}`);

    return { limited: false };
  }

  if (verdict.reason === "timeout") {
    console.warn(`Rate limit timed out for ${label}, failing open`);
  }
  if (verdict.success) return { limited: false };

  return { limited: true, retryAfterSeconds: retryAfterSeconds(verdict.reset) };
}

/**
 * Count one request against `limiter` and return the 429 to send, or `null` to
 * carry on. Fails open (`countFailOpen`).
 */
export async function rateLimitGate(
  limiter: RequestLimiter,
  identifier: string,
  label: string,
): Promise<NextResponse | null> {
  const outcome = await countFailOpen(() => limiter.limit(identifier), label);

  if (!outcome.limited) return null;

  return NextResponse.json(
    { error: "Too many requests. Please try again later." },
    {
      status: 429,
      headers: { "Retry-After": String(outcome.retryAfterSeconds) },
    },
  );
}
