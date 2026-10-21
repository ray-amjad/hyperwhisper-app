// SERVER ERROR TRACKING (#2013)
//
// Sends unhandled errors to Sentry, so they are grouped and alerted on rather
// than lost in the log stream. Inert unless `SENTRY_DSN` is set: with no DSN
// `initErrorTracking` does nothing, registers no process listener, and every
// `captureServerError` call is a no-op.
//
// What leaves the machine is an ALLOW-LIST, built fresh in `scrubEvent`: the
// exception's type, its message (redacted, below), its stack frames as code
// locations only, the tags named in `ALLOWED_TAGS`, the runtime, release and
// environment. Everything else the SDK attaches — the request (URL, headers,
// IP, body), the user, breadcrumbs (console lines), extra data, the host name
// and every other context — is dropped. A transcript, a licence key or an
// email can only reach an event through the message, and the message goes
// through `scrubMessage` before it leaves.

import * as Sentry from '@sentry/bun';
import type { ErrorEvent, Exception, StackFrame } from '@sentry/bun';
import { toRedactedLogLine } from './log-redaction';

/** Tags an event may carry. Anything else set on a scope is dropped. */
export const ALLOWED_TAGS = ['region', 'machineId', 'errorId', 'route', 'method'] as const;

/** Stack frames kept per exception: code locations, not data. */
const MAX_FRAMES = 50;

// `HW-` and four groups of four, as the Account Key is issued, whatever the
// case. No `\b` in front: in `key=HW-…` or `%3DHW-…` the character before can
// be a word character.
const ACCOUNT_KEY = /HW-[A-Z0-9]{4}(?:-[A-Z0-9]{4}){3}(?![A-Z0-9])/gi;
const EMAIL = /[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}/gi;
// An HTTP auth header value echoed into a message.
const BEARER = /\b(Bearer|Basic)\s+[A-Za-z0-9._~+/=-]+/gi;
// A double-quoted value, such as a provider's text echoed into a message
// (`provider said "<transcript>"`): not serialized, so the log redaction keeps it.
const DOUBLE_QUOTED = /"[^"]*"/g;

/**
 * An exception message as it may leave the machine: Account Keys, email
 * addresses and double-quoted values replaced first, then the log redaction every
 * `console.error` here already uses (secrets, credentials in URLs, serialized
 * payloads such as a provider body that could quote a transcript, and a
 * 200-character cap).
 */
export function scrubMessage(message: string): string {
  const keyed = message
    .replace(ACCOUNT_KEY, '<redacted-license-key>')
    .replace(EMAIL, '<redacted-email>')
    .replace(BEARER, '$1 <redacted>')
    .replace(DOUBLE_QUOTED, '"<redacted>"');
  return toRedactedLogLine(keyed);
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
    value: typeof exception.value === 'string' ? scrubMessage(exception.value) : undefined,
    mechanism: exception.mechanism
      ? { type: exception.mechanism.type, handled: exception.mechanism.handled }
      : undefined,
    stacktrace: frames ? { frames: frames.slice(-MAX_FRAMES).map(scrubFrame) } : undefined,
  };
}

/**
 * The `beforeSend` hook: returns a new event holding only the allow-listed
 * fields. Exported for the tests.
 */
export function scrubEvent(event: ErrorEvent): ErrorEvent {
  const tags: Record<string, string> = {};
  for (const name of ALLOWED_TAGS) {
    const value = event.tags?.[name];
    if (typeof value === 'string' || typeof value === 'number' || typeof value === 'boolean') {
      tags[name] = String(value);
    }
  }
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
    tags,
    contexts: event.contexts?.runtime ? { runtime: event.contexts.runtime } : undefined,
  };
  if (event.exception?.values) {
    scrubbed.exception = { values: event.exception.values.map(scrubException) };
  }
  if (typeof event.message === 'string') {
    scrubbed.message = scrubMessage(event.message);
  }
  return scrubbed;
}

let initialized = false;

/**
 * Starts Sentry when `SENTRY_DSN` is set. Returns whether it did. `transport`
 * is for the tests only.
 */
export function initErrorTracking(
  options: { transport?: NonNullable<Parameters<typeof Sentry.init>[0]>['transport'] } = {},
): boolean {
  const dsn = process.env.SENTRY_DSN?.trim();
  if (!dsn || initialized) return initialized;

  Sentry.init({
    dsn,
    environment: process.env.SENTRY_ENVIRONMENT || process.env.FLY_APP_NAME || 'development',
    release: process.env.SENTRY_RELEASE || process.env.FLY_IMAGE_REF || undefined,
    // Errors only: no performance tracing, no OpenTelemetry instrumentation of
    // the HTTP stack, no breadcrumbs.
    enableOpenTelemetrySetup: false,
    maxBreadcrumbs: 0,
    defaultIntegrations: false,
    integrations: [
      Sentry.functionToStringIntegration(),
      Sentry.linkedErrorsIntegration(),
      Sentry.dedupeIntegration(),
      Sentry.onUncaughtExceptionIntegration(),
      // `strict` keeps Bun's own behaviour: an unhandled rejection still ends
      // the process (after the event is sent), as it does with no DSN.
      Sentry.onUnhandledRejectionIntegration({ mode: 'strict' }),
    ],
    initialScope: {
      tags: {
        region: process.env.FLY_REGION || 'local',
        machineId: process.env.FLY_MACHINE_ID || 'local',
      },
    },
    beforeSend: scrubEvent,
    ...(options.transport ? { transport: options.transport } : {}),
  });
  initialized = true;
  return true;
}

/**
 * Reports an error the Hono `onError` handler caught. `errorId` is the id the
 * client was given, so a support quote finds the event. A no-op when Sentry was
 * not started.
 */
export function captureServerError(
  err: unknown,
  context: { errorId: string; route?: string; method?: string },
): void {
  if (!initialized) return;
  try {
    Sentry.captureException(err, {
      tags: {
        errorId: context.errorId,
        ...(context.route ? { route: context.route } : {}),
        ...(context.method ? { method: context.method } : {}),
      },
    });
  } catch {
    // Reporting must never turn a 500 into a crash.
  }
}

/** Waits for queued events to send, up to `timeoutMs`. For shutdown. */
export async function flushErrorTracking(timeoutMs: number): Promise<void> {
  if (!initialized) return;
  try {
    await Sentry.flush(timeoutMs);
  } catch {
    // Best effort.
  }
}
