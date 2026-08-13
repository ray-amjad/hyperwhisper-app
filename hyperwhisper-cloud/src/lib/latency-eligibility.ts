// ANONYMOUS SPEED DATA — WHO IS ALLOWED TO BE MEASURED
//
// The public /latency page is built from anonymous per-attempt timings, and
// sharing them is on by default. That default is only defensible if the user
// could have turned it off, and the switch — "Share anonymous speed data" in
// Settings → General — did not exist before macOS 2.43.0 and Windows 1.10.0.
// Every build older than those was measured with no way to decline.
//
// So the default is not applied retroactively: a client that predates its
// platform's switch is not recorded at all. It starts being recorded when the
// user updates to a build that can say no — at which point the default is a
// real choice, because there is a visible setting behind it.
//
// This is a privacy floor, not a security control. The platform and version
// come from client-supplied headers (see client-info.ts, which warns against
// gating auth, billing or routing on them) and anyone can forge them. That is
// acceptable here and nowhere else, because of which way the forgery points:
// claiming a NEWER version opts you into anonymous statistics you were free to
// opt into anyway, and claiming an older one opts you out — which the
// X-Latency-Opt-Out header already grants unconditionally. Neither direction
// buys an attacker anything, and both are self-inflicted.

import { UNKNOWN_CLIENT } from './client-info';

/**
 * First release of each client that ships the opt-out switch. A platform that
 * is not listed is never recorded, which is what makes the list the whole
 * policy: a new client (iOS) contributes nothing until it is added here, and it
 * should only be added once its own settings screen has the switch.
 */
export const MIN_OPT_OUT_VERSION: Readonly<Record<string, string>> = {
  macos: '2.43.0',
  windows: '1.10.0',
};

/** `2.43.0`, `2.43` and `3` parse; anything else does not. */
const NUMERIC_VERSION = /^\d+(\.\d+){0,2}$/;

function parts(version: string): [number, number, number] | null {
  if (!NUMERIC_VERSION.test(version)) return null;
  const [major = 0, minor = 0, patch = 0] = version.split('.').map(Number);
  return [major, minor, patch];
}

/** True when `version` is `minimum` or newer. */
function isAtLeast(version: string, minimum: string): boolean {
  const actual = parts(version);
  const floor = parts(minimum);
  if (!actual || !floor) return false;

  for (let i = 0; i < 3; i++) {
    if (actual[i] !== floor[i]) return actual[i] > floor[i];
  }
  return true;
}

/**
 * True when this client is new enough that its user has a visible way to turn
 * anonymous speed data off.
 *
 * False for everything else, deliberately — an unknown platform, an unreadable
 * version, and a pre-release tag like `2.43.0-beta` all fail closed. That also
 * excludes direct API callers, who send no client headers: the documented
 * `X-Latency-Opt-Out: 1` is a real opt-out, but it is one a caller has to know
 * about, and "you could have read the docs" is a weaker consent than a switch
 * in front of them. Fewer rows is the cheap side of this trade; the page needs
 * volume, not every last caller.
 */
export function clientOffersLatencyOptOut(
  clientPlatform: string,
  clientVersion: string,
): boolean {
  if (clientPlatform === UNKNOWN_CLIENT || clientVersion === UNKNOWN_CLIENT) return false;

  const minimum = MIN_OPT_OUT_VERSION[clientPlatform];
  if (!minimum) return false;

  return isAtLeast(clientVersion, minimum);
}
