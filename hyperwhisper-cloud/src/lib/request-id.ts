import type { Context } from 'hono';
import { getConnInfo } from 'hono/bun';

export function generateRequestId(): string {
  return crypto.randomUUID();
}

// Fly.io stamps `Fly-Request-Id` on every request its edge proxy forwards, so
// it's a handy correlation id for logs. It is NOT a trust signal: any caller
// that reaches the Machine off-edge (6PN, private IPv6, colocated process)
// controls its own request headers and can forge this value, so it must never
// gate IP trust on its own.
export function getFlyRequestId(c: Context): string | undefined {
  return c.req.header('Fly-Request-Id')
    || c.req.header('Fly-Request-ID')
    || c.req.header('fly-request-id')
    || undefined;
}

// True when the request reached this Machine directly over the private 6PN
// network (another org Machine, the Machine's own private IPv6, or a colocated
// process) instead of through Fly's public edge proxy. All Fly private IPv6
// addresses live under `fdaa::/16`, and 6PN connections bypass the proxy
// entirely — so a 6PN peer address is proof the request did NOT transit the
// edge. Unlike `Fly-Request-Id`/`Fly-Client-IP`, the TCP peer address is
// reported by the kernel and cannot be forged by the caller.
function isOffEdgePeer(c: Context): boolean {
  let peer: string | undefined;
  try {
    peer = getConnInfo(c).remote.address;
  } catch {
    // No connection info (e.g. unit tests via `app.request`, or non-Bun
    // runtimes). Fall back to header-based resolution rather than failing open
    // on a thrown error.
    return false;
  }
  if (!peer) {
    return false;
  }
  return peer.toLowerCase().startsWith('fdaa:');
}

// Resolve the caller's IP for rate-limiting / IP-blocking.
//
// `Fly-Client-IP` and `X-Forwarded-For` are only trustworthy when the request
// came through Fly's public edge proxy, which strips any client-supplied
// `Fly-Client-IP` before forwarding. A caller that reaches the Machine off-edge
// (6PN, private IPv6, colocated process) controls every request header — it can
// forge both `Fly-Request-Id` and `Fly-Client-IP` — so header presence proves
// nothing. We instead key trust on the unforgeable TCP peer address: if the
// request arrived directly over 6PN we refuse to honor the headers and fall
// back to `'unknown'`, keeping per-IP daily quota and `ip_blocked` enforceable.
export function getClientIP(c: Context): string {
  if (isOffEdgePeer(c)) {
    return 'unknown';
  }

  return c.req.header('Fly-Client-IP')
    || c.req.header('X-Forwarded-For')?.split(',')[0]?.trim()
    || 'unknown';
}
