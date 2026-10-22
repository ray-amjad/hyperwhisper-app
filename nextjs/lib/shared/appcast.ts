/**
 * How long a server-side read of the site's own Sparkle appcast may take.
 *
 * Fail fast instead of holding a serverless function open for the full
 * platform max duration if the appcast origin is slow or hangs. Every server
 * fetch of `appcast*.xml` uses this, so the copies cannot drift (#2069).
 */
export const APPCAST_FETCH_TIMEOUT_MS = 3000;
