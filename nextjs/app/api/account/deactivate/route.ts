/**
 * Account Key deactivation API (canonical path).
 *
 * "Account Key" is the new name for what older native apps call a "license key".
 * The wire contract is identical to /api/license/deactivate — this route
 * re-exports those handlers so new app releases can target /api/account/* while
 * installed macOS/Windows/iOS builds keep hitting /api/license/* unchanged.
 */
export { POST } from "../../license/deactivate/route";
