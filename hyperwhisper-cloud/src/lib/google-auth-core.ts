// GOOGLE OAUTH CORE
// The token accessor itself, with both of its I/O edges injected: the act of
// minting a token (`GoogleTokenMinter`) and the act of caching one
// (`GoogleTokenCache`). lib/google-auth.ts supplies the production pair — the
// service-account JWT client and the Upstash Redis cache.
//
// Concurrency: a single in-flight Promise is shared so a cold-cache request
// burst (e.g. machine cold-start after deploy) mints exactly one token
// instead of N. Each region/machine still has its own minter + in-flight
// state — that's by design; cross-region coordination is what the cache is for.
//
// This file is separate from lib/google-auth.ts on purpose. bun's
// `mock.module` is process-wide, and providers/google-chirp.test.ts replaces
// `../lib/google-auth` for the whole run. A test that imported the factory
// from there would get that suite's stub instead of this code. Nothing mocks
// this module, so its test always exercises the real thing.

const FALLBACK_TTL_SECONDS = 3000;            // 50 min — used when the minter omits expiry_date
const TOKEN_TTL_SAFETY_MARGIN_SECONDS = 600;  // expire 10 min before Google's stated expiry
const MIN_TOKEN_TTL_SECONDS = 60;             // never cache shorter than 60s — pathological cases

/**
 * The one thing this module cannot do in a test: turn a service-account
 * credential into a live Google access token. `JWT` satisfies this shape, so
 * the production minter is a thin wrapper around the lazy JWT client in
 * lib/google-auth.ts.
 */
export interface GoogleTokenMinter {
  authorize(): Promise<{ access_token?: string | null; expiry_date?: number | null }>;
}

/**
 * The shared cache the minted token lands in. Every method may reject; a
 * cache failure is logged and swallowed, never fatal — the caller either
 * already has a usable token or pays the mint cost again.
 */
export interface GoogleTokenCache {
  read(): Promise<string | null>;
  write(token: string, ttlSeconds: number): Promise<void>;
  clear(): Promise<void>;
}

export interface GoogleAuth {
  getGoogleAccessToken(): Promise<string>;
  invalidateGoogleAccessToken(): Promise<void>;
}

export function computeCacheTtlSeconds(expiryDate: number | null | undefined): number {
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

/**
 * Build a token accessor over a given minter and cache. Each instance owns
 * its own in-flight state, so one test cannot leak a pending mint into the
 * next.
 */
export function createGoogleAuth(minter: GoogleTokenMinter, cache: GoogleTokenCache): GoogleAuth {
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
      await cache.write(access_token, ttlSeconds);
    } catch (error) {
      // Cache-write failure isn't fatal — the caller already has a usable
      // token. The next request just pays the mint cost again.
      console.warn('google-auth.cache_write_failed', {
        message: error instanceof Error ? error.message : String(error),
        ttlSeconds,
      });
    }

    // Only clear `inflight` AFTER the cache write attempt resolves. Clearing
    // earlier would let a concurrent caller arrive between mint-completion
    // and cache-write, see no inflight, miss the cache, and mint a second
    // token. `mintAndCacheToken` owns its own inflight lifecycle now.
    inflight = null;
    return access_token;
  }

  return {
    async getGoogleAccessToken(): Promise<string> {
      try {
        const cached = await cache.read();
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
      // the clear must happen after the cache write attempt resolves, not in
      // a wrapping `.finally` on this side (see note inside that function).
      inflight = mintAndCacheToken();

      return inflight;
    },

    async invalidateGoogleAccessToken(): Promise<void> {
      try {
        await cache.clear();
      } catch (error) {
        console.warn('google-auth.cache_delete_failed', {
          message: error instanceof Error ? error.message : String(error),
        });
      }
      inflight = null;
    },
  };
}
