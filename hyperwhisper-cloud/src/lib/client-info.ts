// CLIENT IDENTITY — WHICH APP AND WHICH BUILD IS CALLING
//
// Every native client stamps two headers on its HyperWhisper Cloud requests:
//
//   X-HyperWhisper-Platform: macos | windows | ios
//   X-HyperWhisper-Version:  2.41.0
//
// They land on the structured log lines (`transcribe.request_start`,
// `transcribe.request_done`, `post_process.request_start`), so the Axiom
// dataset answers "does this failure only happen on Windows 1.8.1?" without a
// support round-trip.
//
// Older builds send neither header. They do send a User-Agent, so we fall back
// to it: macOS ships `HyperWhisper/2.41.0`, Windows ships
// `HyperWhisper-Windows/1.8.2`. That keeps the field populated for the installed
// base until it updates.
//
// Values are attacker-controlled — anyone can POST any header — so they are
// logging labels only. Never gate auth, billing, or routing on them.
//
// One deliberate exception: lib/latency-eligibility.ts reads them to decide
// whether a client is new enough to have shipped the anonymous-speed-data
// opt-out switch. That is safe only because forging the value in either
// direction gains the forger nothing — see the note there before adding a
// second exception.

import type { Context } from 'hono';

export const CLIENT_PLATFORM_HEADER = 'X-HyperWhisper-Platform';
export const CLIENT_VERSION_HEADER = 'X-HyperWhisper-Version';

/** Value used when the client sends nothing we can read. */
export const UNKNOWN_CLIENT = 'unknown';

export interface ClientInfo {
  clientPlatform: string;
  clientVersion: string;
}

// A log field, not a credential: cap the length and the alphabet so a hostile
// caller cannot inject newlines, quotes, or a megabyte of text into the JSON
// log line.
const MAX_VALUE_LENGTH = 32;
const SAFE_VALUE = /^[a-zA-Z0-9._-]+$/;

function sanitize(value: string | undefined): string | undefined {
  if (!value) return undefined;
  const trimmed = value.trim();
  if (!trimmed || trimmed.length > MAX_VALUE_LENGTH || !SAFE_VALUE.test(trimmed)) {
    return undefined;
  }
  return trimmed;
}

// `HyperWhisper/2.41.0` → macos, `HyperWhisper-Windows/1.8.2` → windows.
// Anything else (curl, a browser, an integration) stays unknown.
//
// The version part is deliberately not required to start with a digit: a client
// that cannot read its own version stamps a word there (Windows sends
// `HyperWhisper-Windows/unknown`), and the platform half of that is still worth
// keeping. The value is a log label, so a non-numeric version costs nothing.
const LEGACY_USER_AGENT = /^HyperWhisper(-(?<platform>[A-Za-z]+))?\/(?<version>[A-Za-z0-9._-]+)/;

function fromUserAgent(userAgent: string | undefined): ClientInfo | undefined {
  const match = userAgent?.trim().match(LEGACY_USER_AGENT);
  if (!match?.groups) return undefined;

  // macOS never named itself in the User-Agent; a bare `HyperWhisper/x` is it.
  const platform = match.groups.platform?.toLowerCase() ?? 'macos';
  return {
    clientPlatform: sanitize(platform) ?? UNKNOWN_CLIENT,
    clientVersion: sanitize(match.groups.version) ?? UNKNOWN_CLIENT,
  };
}

/**
 * Resolve platform + app version from raw header values. Pure, so the header
 * plumbing and the parsing are testable apart.
 */
export function parseClientInfo(
  platformHeader: string | undefined,
  versionHeader: string | undefined,
  userAgent: string | undefined,
): ClientInfo {
  const platform = sanitize(platformHeader)?.toLowerCase();
  const version = sanitize(versionHeader);
  if (platform || version) {
    // One header present and the other missing is still worth recording —
    // report what arrived rather than dropping both.
    const legacy = fromUserAgent(userAgent);
    return {
      clientPlatform: platform ?? legacy?.clientPlatform ?? UNKNOWN_CLIENT,
      clientVersion: version ?? legacy?.clientVersion ?? UNKNOWN_CLIENT,
    };
  }

  return fromUserAgent(userAgent) ?? {
    clientPlatform: UNKNOWN_CLIENT,
    clientVersion: UNKNOWN_CLIENT,
  };
}

/** Read the client identity off a Hono request. */
export function readClientInfo(c: Context): ClientInfo {
  return parseClientInfo(
    c.req.header(CLIENT_PLATFORM_HEADER),
    c.req.header(CLIENT_VERSION_HEADER),
    c.req.header('User-Agent'),
  );
}
