/**
 * Next.js instrumentation hook (#2013): server error tracking.
 *
 * `register` starts Sentry on the Node.js server runtime when `SENTRY_DSN` is
 * set (`sentry.server.config.ts`). The app has no Edge routes: `proxy.ts` runs
 * on Node.js in Next.js 16. `onRequestError` reports every error that escapes
 * a route handler, a server component or a server action; with no DSN it does
 * nothing.
 */
import type { Instrumentation } from "next";

export async function register(): Promise<void> {
  if (process.env.NEXT_RUNTIME === "nodejs") {
    await import("./sentry.server.config");
  }
}

export const onRequestError: Instrumentation.onRequestError = async (
  error,
  request,
  context,
) => {
  if (
    process.env.NEXT_RUNTIME !== "nodejs" ||
    !process.env.SENTRY_DSN?.trim()
  ) {
    return;
  }
  const { isErrorTrackingEnabled } = await import("./sentry.server.config");

  if (!isErrorTrackingEnabled()) return;
  const Sentry = await import("@sentry/nextjs");

  Sentry.captureRequestError(error, request, context);
};
