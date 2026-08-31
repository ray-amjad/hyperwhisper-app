// GOOGLE OAUTH HELPER
// Mints a short-lived Google Cloud access token from a service-account JSON
// credential and caches it in Upstash Redis so the per-request cost is only
// paid once per warm-up window. Used by the Google Chirp 3 STT provider AND
// the GCS scratch bucket (lib/gcs-storage.ts) — both share one token.
//
// Cache key: `google_oauth_token`
// TTL: derived from Google's returned `expiry_date` (capped at 1h), expiring
//      10 minutes early so stragglers don't race the boundary. If Google
//      doesn't return an expiry, we fall back to a conservative 50 min TTL.
//
// This file holds only the two production edges — the env-backed JWT client
// and the Redis cache. The accessor itself, including the single-flight
// behaviour, lives in lib/google-auth-core.ts, which is where it is tested.

import { JWT } from 'google-auth-library';
import { createGoogleAuth, type GoogleTokenCache, type GoogleTokenMinter } from './google-auth-core';
import { redis } from './redis';

const TOKEN_CACHE_KEY = 'google_oauth_token';
const SPEECH_SCOPE = 'https://www.googleapis.com/auth/cloud-platform';

export type { GoogleAuth, GoogleTokenCache, GoogleTokenMinter } from './google-auth-core';

let _jwtClient: JWT | null = null;

function getJwtClient(): JWT {
  if (_jwtClient) {
    return _jwtClient;
  }

  const raw = process.env.GOOGLE_SERVICE_ACCOUNT_JSON;
  if (!raw) {
    throw new Error('GOOGLE_SERVICE_ACCOUNT_JSON not configured');
  }

  let credentials: { client_email?: string; private_key?: string };
  try {
    credentials = JSON.parse(raw);
  } catch (error) {
    throw new Error(`GOOGLE_SERVICE_ACCOUNT_JSON is not valid JSON: ${error instanceof Error ? error.message : String(error)}`);
  }

  if (!credentials.client_email || !credentials.private_key) {
    throw new Error('GOOGLE_SERVICE_ACCOUNT_JSON is missing client_email or private_key');
  }

  _jwtClient = new JWT({
    email: credentials.client_email,
    key: credentials.private_key,
    scopes: [SPEECH_SCOPE],
  });

  return _jwtClient;
}

/**
 * The production minter: the lazy, env-backed service-account JWT client.
 *
 * `async` is load-bearing, not style. `getJwtClient()` throws SYNCHRONOUSLY on
 * every credential-config fault (missing, malformed, or incomplete
 * GOOGLE_SERVICE_ACCOUNT_JSON), and `GoogleTokenMinter.authorize` is declared
 * to return a Promise. Without `async` that sync throw ran the core's
 * `inflight = null` clear BEFORE `inflight = mintAndCacheToken()` assigned to
 * it, so the rejected promise stayed published and every later caller on this
 * machine got the same stale rejection back — for the machine's whole life,
 * even after Infisical synced a correct secret. `async` turns it into a
 * rejection, which is what the core's retry path is written against.
 */
const jwtTokenMinter: GoogleTokenMinter = {
  authorize: async () => getJwtClient().authorize(),
};

/** The production cache: one Upstash key, shared by every machine in a region. */
const redisTokenCache: GoogleTokenCache = {
  read: () => redis.get().get<string>(TOKEN_CACHE_KEY),
  write: async (token, ttlSeconds) => {
    await redis.get().set(TOKEN_CACHE_KEY, token, { ex: ttlSeconds });
  },
  clear: async () => {
    await redis.get().del(TOKEN_CACHE_KEY);
  },
};

/**
 * The process-wide instance every caller already uses. Same lifetime and same
 * single-flight semantics as the module-level state it replaces.
 */
const defaultGoogleAuth = createGoogleAuth(jwtTokenMinter, redisTokenCache);

/**
 * Get a Google Cloud OAuth access token, using the Upstash Redis cache when
 * possible. Concurrent cold-cache callers share a single in-flight mint so we
 * don't fan out N parallel `authorize()` calls under burst.
 */
export function getGoogleAccessToken(): Promise<string> {
  return defaultGoogleAuth.getGoogleAccessToken();
}

/**
 * Force-invalidate the cached Google access token. Used when an in-flight
 * request hits a 401 mid-poll because the cached token expired faster than
 * its declared TTL — the next `getGoogleAccessToken` call after this will
 * re-mint from the JWT client instead of returning a stale Redis hit.
 */
export function invalidateGoogleAccessToken(): Promise<void> {
  return defaultGoogleAuth.invalidateGoogleAccessToken();
}
