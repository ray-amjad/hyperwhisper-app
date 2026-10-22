/**
 * Server error tracking (#2013).
 *
 * Sends errors that escape a route handler, a server component or a server
 * action (Next.js `onRequestError`, wired in `instrumentation.ts`) to Sentry,
 * so they are grouped and alerted on instead of living only in Vercel's
 * short-lived function logs. Inert unless `SENTRY_DSN` is set: with no DSN
 * nothing is started and `onRequestError` does nothing.
 *
 * What leaves the server is an ALLOW-LIST, built fresh in `scrubEvent`: the
 * exception's type, its message (through `scrubMessage`), its stack frames as
 * code locations only, the route TEMPLATE (`/api/license/validate`, never the
 * request URL), the method, the runtime, release and environment. The request
 * (URL, query, headers, cookies, IP, body), the user, breadcrumbs, extra data,
 * the host name and every other context are dropped. Server-only: the browser
 * bundle gets no Sentry SDK from this.
 */
import type { ErrorEvent, Exception, StackFrame } from "@sentry/nextjs";

import * as Sentry from "@sentry/nextjs";

import { redactAccountKeyFromUrl } from "@/src/lib/account-key-redaction";

/** Tags an event may carry. Anything else set on a scope is dropped. */
export const ALLOWED_TAGS = ["route", "routeType", "method"] as const;

/** The most of one message that leaves the server. */
const MAX_MESSAGE_LENGTH = 300;
/** Stack frames kept per exception: code locations, not data. */
const MAX_FRAMES = 50;

const EMAIL = /[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}/gi;
// An HTTP auth header value echoed into a message.
const BEARER = /\b(Bearer|Basic)\s+[A-Za-z0-9._~+/=-]+/gi;
// Stripe secret, restricted and webhook-signing keys.
const STRIPE_SECRET =
  /\b(sk|rk)_(live|test)_[A-Za-z0-9]+|\bwhsec_[A-Za-z0-9]+/g;
// `scheme://user:password@host`: keep the scheme and host, cut the userinfo.
const URL_CREDENTIALS = /([a-z][a-z0-9+.-]*:\/\/)[^@\s"'/?#\]]*@/gi;
// drizzle's `Failed query: <sql>\nparams: <bound values>`: the values are a
// customer's email or licence key (lib/shared/db-error.ts). Cut to the end.
const QUERY_PARAMS = /\bparams:[\s\S]*$/;
// pg quotes the offending value: `invalid input syntax for type uuid: "…"`,
// `Key (email)=(…) already exists`. Not applied to drizzle's own
// `Failed query:` text, whose double quotes are column names and whose values
// are `$n` placeholders until `params:`.
const DOUBLE_QUOTED = /"[^"]*"/g;
const FAILED_QUERY = /^Failed query: /;
const KEY_VALUE = /(Key \([^)]*\)=\()[^)]*\)/g;

/**
 * An exception message as it may leave the server: bound query values,
 * quoted values, Account Keys, email addresses, auth headers, Stripe keys and
 * URL credentials cut, then capped.
 */
export function scrubMessage(message: string): string {
  try {
    const withoutParams = message.replace(QUERY_PARAMS, "params: [redacted]");
    const withoutValues = FAILED_QUERY.test(withoutParams)
      ? withoutParams
      : withoutParams.replace(DOUBLE_QUOTED, '"[redacted]"');
    const line = redactAccountKeyFromUrl(
      withoutValues
        .replace(KEY_VALUE, "$1[redacted])")
        .replace(URL_CREDENTIALS, "$1[redacted]@")
        .replace(BEARER, "$1 [redacted]")
        .replace(STRIPE_SECRET, "[redacted]"),
    ).replace(EMAIL, "[redacted-email]");

    return line.length > MAX_MESSAGE_LENGTH
      ? `${line.slice(0, MAX_MESSAGE_LENGTH)}[truncated]`
      : line;
  } catch {
    return "[unreadable message]";
  }
}

function scrubFrame(frame: StackFrame): StackFrame {
  return {
    filename: frame.filename,
    module: frame.module,
    function: frame.function,
    lineno: frame.lineno,
    colno: frame.colno,
    in_app: frame.in_app,
  };
}

function scrubException(exception: Exception): Exception {
  const frames = exception.stacktrace?.frames;

  return {
    type: exception.type,
    value:
      typeof exception.value === "string"
        ? scrubMessage(exception.value)
        : undefined,
    mechanism: exception.mechanism
      ? { type: exception.mechanism.type, handled: exception.mechanism.handled }
      : undefined,
    stacktrace: frames
      ? { frames: frames.slice(-MAX_FRAMES).map(scrubFrame) }
      : undefined,
  };
}

function stringField(
  record: Record<string, unknown> | undefined,
  name: string,
): string | undefined {
  const value = record?.[name];

  return typeof value === "string" ? value : undefined;
}

/**
 * The `beforeSend` hook: a new event holding only the allow-listed fields.
 * Exported for the tests.
 */
export function scrubEvent(event: ErrorEvent): ErrorEvent {
  const tags: Record<string, string> = {};

  for (const name of ALLOWED_TAGS) {
    const value = event.tags?.[name];

    if (
      typeof value === "string" ||
      typeof value === "number" ||
      typeof value === "boolean"
    ) {
      tags[name] = String(value);
    }
  }
  // `captureRequestError` puts the route on the `nextjs` context: the route
  // template (`router_path`), never the request path.
  const nextjs = event.contexts?.nextjs as Record<string, unknown> | undefined;
  const route = stringField(nextjs, "router_path");
  const routeType = stringField(nextjs, "route_type");
  // …and names the transaction `<METHOD> <route template>`.
  const method = event.transaction?.match(/^([A-Z]+) /)?.[1];

  if (route) tags.route = route;
  if (routeType) tags.routeType = routeType;
  if (method) tags.method = method;

  const scrubbed: ErrorEvent = {
    type: undefined,
    event_id: event.event_id,
    timestamp: event.timestamp,
    level: event.level,
    platform: event.platform,
    release: event.release,
    environment: event.environment,
    sdk: event.sdk,
    fingerprint: event.fingerprint,
    transaction: route && method ? `${method} ${route}` : undefined,
    tags,
    contexts: event.contexts?.runtime
      ? { runtime: event.contexts.runtime }
      : undefined,
  };

  if (event.exception?.values) {
    scrubbed.exception = { values: event.exception.values.map(scrubException) };
  }
  if (typeof event.message === "string") {
    scrubbed.message = scrubMessage(event.message);
  }

  return scrubbed;
}

let initialized = false;

/** Whether Sentry was started in this process. */
export function isErrorTrackingEnabled(): boolean {
  return initialized;
}

/**
 * Starts Sentry when `SENTRY_DSN` is set. Returns whether it did. `transport`
 * is for the tests only.
 */
export function initServerErrorTracking(
  options: {
    transport?: (
      options: Parameters<typeof Sentry.createTransport>[0],
    ) => ReturnType<typeof Sentry.createTransport>;
  } = {},
): boolean {
  const dsn = process.env.SENTRY_DSN?.trim();

  if (!dsn || initialized) return initialized;

  Sentry.init({
    dsn,
    environment:
      process.env.SENTRY_ENVIRONMENT || process.env.VERCEL_ENV || "development",
    release: process.env.VERCEL_GIT_COMMIT_SHA || undefined,
    // Errors only: no performance tracing, no OpenTelemetry setup, no
    // breadcrumbs, no request data, no process-wide handlers.
    enableOpenTelemetrySetup: false,
    maxBreadcrumbs: 0,
    defaultIntegrations: false,
    integrations: [
      Sentry.functionToStringIntegration(),
      Sentry.linkedErrorsIntegration(),
      // No dedupe: it drops an error identical to the one before it, so a
      // route failing on every request would show as one event and never trip
      // a rate alert. The SDK already skips an error object captured twice.
    ],
    beforeSend: scrubEvent,
    ...(options.transport ? { transport: options.transport } : {}),
  });
  initialized = true;

  return true;
}

initServerErrorTracking();
