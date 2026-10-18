import { loadPostHog } from "@/src/lib/posthog-client";

/**
 * Reports a failure that a client component caught and turned into a message
 * (#739), so it leaves a record in PostHog error tracking instead of nowhere.
 *
 * `operation` is a stable name for the flow (`"sign_in.magic_link"`). Keep
 * `context` free of personal data: no email, no Account Key, no link.
 *
 * It never throws and its promise never rejects, so a caller can `void` it
 * inside its own `catch`. PostHog loads lazily (#918) and can be absent or
 * blocked: then only the console sees the error.
 */
export function reportClientError(
  operation: string,
  error: unknown,
  context?: Record<string, string | number>,
): Promise<void> {
  try {
    console.error(operation, error);
  } catch {
    // A console that throws must not turn one error into two.
  }

  return report(operation, error, context);
}

async function report(
  operation: string,
  error: unknown,
  context: Record<string, string | number> | undefined,
): Promise<void> {
  try {
    const posthog = await loadPostHog();

    posthog?.captureException(error, { ...context, operation });
  } catch {
    // An ad-blocker can stub `captureException` with a thrower: a failed
    // report must never surface as a second error.
  }
}
