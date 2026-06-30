/**
 * Account Key credits API (canonical path).
 *
 * "Account Key" is the new name for what older native apps call a "license key".
 * The wire contract is identical to /api/license/credits — this route re-exports
 * those handlers so new app releases can target /api/account/* while installed
 * macOS/Windows/iOS builds keep hitting /api/license/* unchanged.
 *
 * The JSON field rename (license_key -> account_key) is intentionally deferred
 * to the DB/identifier rename (Part B3); both paths currently speak license_key.
 */
export { GET, POST } from "../../license/credits/route";
