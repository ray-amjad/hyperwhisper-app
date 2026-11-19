// THE REQUEST GATE
// The one place every key-accepting route (/transcribe, /post-process, /usage,
// the live-streaming upgrades) runs IP block → auth → per-key rate limit, in
// that order.
//
// It is two calls, not one, because a route checks its own request shape
// between the IP block and auth (/post-process parses the body the key is in,
// /transcribe rejects a bad provider before any auth work). `openRequestGate`
// runs the IP block and hands back `admitKey`, so auth and the rate limit can
// only run after it, and always in the order the gate fixes. The route still
// chooses its bucket(s) and does its own credit floor or hold afterwards.

import type { Context } from 'hono';
import { isIPBlocked } from '../lib/redis';
import { getClientIP } from '../lib/request-id';
import { errorResponse } from '../lib/responses';
import { validateAuth, type AuthContext, type AuthDiagnostics } from './auth';
import { keyRateLimitResponse, type RateLimitBucket } from './rate-limit';

export type GateRejectionReason = 'ip_blocked' | 'auth_failed' | 'rate_limited';

export type GateRejection =
  | { ok: false; reason: 'ip_blocked' | 'rate_limited'; response: Response }
  | {
    ok: false;
    reason: 'auth_failed';
    response: Response;
    authDiagnostics: AuthDiagnostics;
    /** The status `validateAuth` answered, even where `response` is the plain-text 401. */
    authStatus: number;
  };

export interface RequestGateOptions {
  /** The endpoint the rate-limit refusal log names (`/transcribe`, `c.req.path`). */
  endpoint: string;
  /** The per-key buckets, checked as `keyRateLimitResponse` does. Default `['key']`. */
  buckets?: readonly RateLimitBucket[];
  /**
   * `validate` (default): `validateAuth`, then the per-key rate limit on the
   * accepted key. `route`: the rate limit only, and the route does its own key
   * lookup after it. /usage needs that order (#2010): its force refresh calls
   * the site's credits and validate routes, whose per-IP budgets every machine
   * in a Fly region shares, so the key's bucket must run BEFORE any lookup. The
   * key is hashed, so an unknown key getting a bucket costs nothing.
   */
  auth?: 'validate' | 'route';
  /**
   * The WebSocket upgrades answer the IP block and an auth failure as plain
   * text (`Access denied` / `Unauthorized`), as their clients read them; every
   * other route answers with the JSON error body.
   */
  plainText?: boolean;
}

export type KeyAdmission =
  | { ok: true; auth: AuthContext; authDiagnostics: AuthDiagnostics }
  | GateRejection;

export type RouteKeyAdmission = { ok: true } | GateRejection;

export interface OpenRequestGate {
  ok: true;
  clientIP: string;
  /** Auth then rate limit (or the rate limit alone, with `auth: 'route'`). */
  admitKey(licenseKey: string | undefined): Promise<KeyAdmission>;
}

export interface OpenRouteAuthGate {
  ok: true;
  clientIP: string;
  admitKey(licenseKey: string): Promise<RouteKeyAdmission>;
}

function blockedResponse(c: Context, plainText: boolean): Response {
  return plainText
    ? c.text('Access denied', 403)
    : errorResponse(403, 'Access denied', 'Your IP has been temporarily blocked due to abuse');
}

export async function openRequestGate(
  c: Context,
  options: RequestGateOptions & { auth: 'route' },
): Promise<OpenRouteAuthGate | GateRejection>;
export async function openRequestGate(
  c: Context,
  options: RequestGateOptions & { auth?: 'validate' },
): Promise<OpenRequestGate | GateRejection>;
export async function openRequestGate(
  c: Context,
  options: RequestGateOptions,
): Promise<OpenRequestGate | OpenRouteAuthGate | GateRejection> {
  const plainText = options.plainText ?? false;
  const clientIP = getClientIP(c);
  c.set('clientIP', clientIP);
  if (await isIPBlocked(clientIP)) {
    return { ok: false, reason: 'ip_blocked', response: blockedResponse(c, plainText) };
  }

  const rateLimit = async (licenseKey: string): Promise<GateRejection | null> => {
    const rateLimited = await keyRateLimitResponse(options.endpoint, licenseKey, options.buckets);
    return rateLimited ? { ok: false, reason: 'rate_limited', response: rateLimited } : null;
  };

  if (options.auth === 'route') {
    return {
      ok: true,
      clientIP,
      admitKey: async (licenseKey: string): Promise<RouteKeyAdmission> => (await rateLimit(licenseKey)) ?? { ok: true },
    };
  }

  return {
    ok: true,
    clientIP,
    admitKey: async (licenseKey: string | undefined): Promise<KeyAdmission> => {
      const authResult = await validateAuth({ licenseKey });
      if (!authResult.ok) {
        return {
          ok: false,
          reason: 'auth_failed',
          response: plainText ? c.text('Unauthorized', 401) : authResult.response,
          authDiagnostics: authResult.diagnostics,
          authStatus: authResult.response.status,
        };
      }
      c.set('auth', authResult.value);
      const limited = await rateLimit(authResult.value.licenseKey);
      if (limited) return limited;
      return { ok: true, auth: authResult.value, authDiagnostics: authResult.diagnostics };
    },
  };
}
