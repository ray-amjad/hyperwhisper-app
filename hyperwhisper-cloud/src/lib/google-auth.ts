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
// Concurrency: a single in-flight Promise is shared so a cold-cache request
// burst (e.g. machine cold-start after deploy) mints exactly one token
// instead of N. Each region/machine still has its own JWT client + in-flight
// state — that's by design; cross-region coordination is what Redis is for.
//
// Seam: the act of minting a token is injectable (`GoogleTokenMinter`). The
// production minter is the lazy `JWT` client built from
// `GOOGLE_SERVICE_ACCOUNT_JSON`; a test supplies its own so the single-flight
// and TTL behaviour can be exercised without a real RSA key or a call to
// Google. `createGoogleAuth` also gives each instance its own in-flight state,
// so one test cannot leak a pending mint into the next.

import { JWT } from 'google-auth-library';
import { redis } from './redis';

const TOKEN_CACHE_KEY = 'google_oauth_token';
const FALLBACK_TTL_SECONDS = 3000;            // 50 min — used when Google omits expiry_date
const TOKEN_TTL_SAFETY_MARGIN_SECONDS = 600;  // expire 10 min before Google's stated expiry
const MIN_TOKEN_TTL_SECONDS = 60;             // never cache shorter than 60s — pathological cases
const SPEECH_SCOPE = 'https://www.googleapis.com/auth/cloud-platform';

/**
 * The one thing this module cannot do in a test: turn a service-account
 * credential into a live Google access token. `JWT` satisfies this shape, so
 * the production minter is a thin wrapper around the lazy JWT client below.
 */
export interface GoogleTokenMinter {
  authorize(): Promise<{ access_token?: string | null; expiry_date?: number | null }>;
}

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

function computeCacheTtlSeconds(expiryDate: number | null | undefined): number {
  if (typeof expiryDate !== 'number' || !Number.isFinite(expiryDate)) {
    return FALLBACK_TTL_SECONDS;
  }
  const remainingSeconds = Math.floor((expiryDate - Date.now()) / 1000) - TOKEN_TTL_SAFETY_MARGIN_SECONDS;
  if (remainingSeconds < MIN_TOKEN_TTL_SECONDS) {
    // Pathological — Google handed us a token already at/near expiry. Cache
    // briefly so a burst doesn't re-mint per request but expire fast so the
    // next regular request gets a fresh one.
    return MIN_TOKEN_TTL_SECONDS;
  }
  return remainingSeconds;
}

/** The production minter: the lazy, env-backed service-account JWT client. */
const jwtTokenMinter: GoogleTokenMinter = {
  authorize: () => getJwtClient().authorize(),
};

export interface GoogleAuth {
  getGoogleAccessToken(): Promise<string>;
  invalidateGoogleAccessToken(): Promise<void>;
}

/**
 * Build a token accessor over a given minter. The default minter is the
 * env-backed JWT client, so `createGoogleAuth()` reproduces the previous
 * module-level behaviour exactly. Each instance owns its own in-flight state.
 */
export function createGoogleAuth(minter: GoogleTokenMinter = jwtTokenMinter): GoogleAuth {
  let inflight: Promise<string> | null = null;

  async function mintAndCacheToken(): Promise<string> {
    let access_token: string | null | undefined;
    let expiry_date: number | null | undefined;
    try {
      ({ access_token, expiry_date } = await minter.authorize());
    } catch (error) {
      // Singleton failure surface — this is the entire health of the Chirp
      // self-only chain hanging on it, so the log line matters.
      console.error('google-auth.token_mint_failed', {
        message: error instanceof Error ? error.message : String(error),
      });
      // Clear inflight on failure so the next request retries instead of
      // hanging on a permanently-rejected promise.
      inflight = null;
      throw error instanceof Error ? error : new Error(String(error));
    }

    if (!access_token) {
      const err = new Error('Google service account did not return an access_token');
      console.error('google-auth.token_mint_failed', { message: err.message });
      inflight = null;
      throw err;
    }

    const ttlSeconds = computeCacheTtlSeconds(expiry_date);

    try {
      await redis.get().set(TOKEN_CACHE_KEY, access_token, { ex: ttlSeconds });
    } catch (error) {
      // Cache-write failure isn't fatal — the caller already has a usable
      // token. The next request just pays the mint cost again.
      console.warn('google-auth.cache_write_failed', {
        message: error instanceof Error ? error.message : String(error),
        ttlSeconds,
      });
    }

    // Only clear `inflight` AFTER the Redis SET attempt resolves. Clearing
    // earlier would let a concurrent caller arrive between mint-completion
    // and cache-write, see no inflight, miss the cache, and mint a second
    // token. `mintAndCacheToken` owns its own inflight lifecycle now.
    inflight = null;
    return access_token;
  }

  return {
    async getGoogleAccessToken(): Promise<string> {
      try {
        const cached = await redis.get().get<string>(TOKEN_CACHE_KEY);
        if (typeof cached === 'string' && cached.length > 0) {
          return cached;
        }
      } catch (error) {
        console.warn('google-auth.cache_read_failed', {
          message: error instanceof Error ? error.message : String(error),
        });
      }

      if (inflight) {
        return inflight;
      }

      // Assign before awaiting so concurrent callers see the inflight promise.
      // `mintAndCacheToken` is responsible for clearing `inflight` itself —
      // the clear must happen after the Redis SET attempt resolves, not in a
      // wrapping `.finally` on this side (see note inside that function).
      inflight = mintAndCacheToken();

      return inflight;
    },

    async invalidateGoogleAccessToken(): Promise<void> {
      try {
        await redis.get().del(TOKEN_CACHE_KEY);
      } catch (error) {
        console.warn('google-auth.cache_delete_failed', {
          message: error instanceof Error ? error.message : String(error),
        });
      }
      inflight = null;
    },
  };
}

/**
 * The process-wide instance every caller already uses. Same lifetime and same
 * single-flight semantics as the module-level state it replaces.
 */
const defaultGoogleAuth = createGoogleAuth();

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
