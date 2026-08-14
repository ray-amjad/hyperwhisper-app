//
//  LicenseNetworkVerdictReportingTests.swift
//  hyperwhisperTests
//
//  HYPERWHISPER-SP / HYPERWHISPER-FM ("License validation server error"): the
//  macOS client was reporting a NORMAL license verdict to Sentry as a production
//  error. The backend answers a non-entitled license with HTTP 400 +
//  `{"valid":false,"error":"…"}`, and `validateLicense` captured every non-200
//  that wasn't a retryable 5xx/429 — so a lapsed license filed a Sentry error on
//  every launch-time revalidation. A 400 is not a server error.
//
//  The body SHAPE cannot split those two cases: the backend uses the same 400 +
//  `{valid:false,error}` for real infrastructure faults, and an unknown key is
//  reported with the *identical* error string as a Polar outage ("Failed to
//  validate with Polar"). So the backend now sends a machine-readable `reason`
//  (`not_entitled` / `lookup_failed` / `bad_request`) and
//  `LicenseNetworkService.isLicenseVerdictResponse` keys off that. It is a pure
//  static specifically so this classifier can be tested without a live
//  URLSession, a Sentry transport, or the Rust core.
//
//  These tests pin BOTH halves of it:
//    - `reason: "not_entitled"` at 400 is recognized (→ logged, not captured), and
//    - everything else — `lookup_failed`, `bad_request`, an unrecognised reason,
//      a body with no `reason` at all, a captive-portal HTML page, an empty body,
//      a different status code — is NOT, so genuine faults keep reaching Sentry.
//
//  This predicate only gates the Sentry capture. It does not touch the verdict
//  returned to callers: an invalid license stays `.invalid`, with the same
//  user-facing message, whichever way this answers.
//

import Testing
import Foundation
@testable import HyperWhisper

struct LicenseNetworkVerdictReportingTests {

    /// Small helper so each case reads as "this body, this status → verdict?".
    private func isVerdict(_ statusCode: Int, _ json: String) -> Bool {
        LicenseNetworkService.isLicenseVerdictResponse(
            statusCode: statusCode,
            body: Data(json.utf8)
        )
    }

    // MARK: - Ordinary license verdicts (must NOT be captured to Sentry)

    @Test func notEntitledIsAnOrdinaryVerdict() {
        // `License is ${status}` for any status other than "granted" — revoked,
        // expired, disabled. A lapsed subscription hits this on every launch-time
        // revalidation; it is not a server fault.
        #expect(isVerdict(400, #"{"valid":false,"error":"License is revoked","reason":"not_entitled"}"#))
        #expect(isVerdict(400, #"{"valid":false,"error":"License is expired","reason":"not_entitled"}"#))
    }

    @Test func extraFieldsInTheBodyDoNotPreventRecognition() {
        // The decoder must ignore unknown keys — the backend is free to add
        // fields to this response without turning every verdict back into a
        // Sentry error.
        #expect(isVerdict(400, #"{"valid":false,"error":"License is revoked","reason":"not_entitled","code":"not_granted"}"#))
    }

    @Test func reasonAloneIsEnough() {
        // `reason` is the discriminator. The other fields are the core's job, so
        // their absence must not veto a reason the server stated explicitly.
        #expect(isVerdict(400, #"{"reason":"not_entitled"}"#))
    }

    // MARK: - Not a verdict: a reason that isn't `not_entitled`

    @Test func lookupFailedIsNotAVerdict() {
        // The whole point of the field. "Failed to validate with Polar" covers a
        // Polar outage, a rotated token AND a genuinely unknown key — we could
        // not establish the license's state, so this must still reach Sentry.
        #expect(!isVerdict(400, #"{"valid":false,"error":"Failed to validate with Polar","reason":"lookup_failed"}"#))
        #expect(!isVerdict(400, #"{"valid":false,"error":"Failed to get or create user","reason":"lookup_failed"}"#))
    }

    @Test func badRequestIsNotAVerdict() {
        // Unreachable from this client (an empty key is rejected before the
        // request is made, and the body is built by the Rust core), so if it ever
        // does arrive something is wrong and we want to hear about it.
        #expect(!isVerdict(400, #"{"valid":false,"error":"License key is required","reason":"bad_request"}"#))
        #expect(!isVerdict(400, #"{"valid":false,"error":"Invalid request body","reason":"bad_request"}"#))
    }

    @Test func unrecognisedReasonIsNotAVerdict() {
        // Match exactly, never by prefix or case. A misspelled or newly-invented
        // reason is an anomaly, and reporting it is the safe direction to fail.
        #expect(!isVerdict(400, #"{"valid":false,"error":"License is revoked","reason":"not_entitled_yet"}"#))
        #expect(!isVerdict(400, #"{"valid":false,"error":"License is revoked","reason":"notentitled"}"#))
        #expect(!isVerdict(400, #"{"valid":false,"error":"License is revoked","reason":"NOT_ENTITLED"}"#))
        #expect(!isVerdict(400, #"{"valid":false,"error":"License is revoked","reason":""}"#))
    }

    // MARK: - Not a verdict: no reason at all (must still be captured)

    @Test func missingReasonIsNotAVerdict() {
        // An older backend, a proxy, or anything else that doesn't classify. The
        // shape that USED to be trusted is, on its own, no longer enough.
        #expect(!isVerdict(400, #"{"valid":false,"error":"License is revoked"}"#))
        #expect(!isVerdict(400, #"{"valid":false}"#))
        #expect(!isVerdict(400, "{}"))
    }

    @Test func nullReasonIsNotAVerdict() {
        #expect(!isVerdict(400, #"{"valid":false,"error":"License is revoked","reason":null}"#))
    }

    @Test func nonStringReasonIsNotAVerdict() {
        // Decoding throws on a type mismatch, which lands on the safe side.
        #expect(!isVerdict(400, #"{"valid":false,"reason":7}"#))
        #expect(!isVerdict(400, #"{"valid":false,"reason":["not_entitled"]}"#))
    }

    @Test func captivePortalHtmlIsNotAVerdict() {
        // A hotel Wi-Fi / corporate proxy interception page returning 400 must
        // still be captured — that's a real (if environmental) failure, and it
        // does not decode as JSON at all.
        let html = """
        <!DOCTYPE html><html><head><title>Sign in to continue</title></head>\
        <body><h1>Network login required</h1></body></html>
        """
        #expect(!LicenseNetworkService.isLicenseVerdictResponse(statusCode: 400, body: Data(html.utf8)))
    }

    @Test func emptyBodyIsNotAVerdict() {
        #expect(!LicenseNetworkService.isLicenseVerdictResponse(statusCode: 400, body: Data()))
    }

    @Test func nonObjectJsonIsNotAVerdict() {
        #expect(!isVerdict(400, "[]"))
        #expect(!isVerdict(400, #""not_entitled""#))
    }

    // MARK: - Not a verdict: status code (the status must match too)

    @Test func otherClientErrorStatusesAreNotVerdicts() {
        // Same body, different status. Only 400 is the documented
        // ordinary-verdict status; a 401/403/404/422 from this endpoint means
        // something unexpected (auth, routing, schema) and must keep reaching
        // Sentry.
        let verdictBody = #"{"valid":false,"error":"License is revoked","reason":"not_entitled"}"#
        for status in [401, 403, 404, 422] {
            #expect(!isVerdict(status, verdictBody))
        }
    }

    @Test func redirectStatusIsNotAVerdict() {
        // A 301 with a verdict body would mean the endpoint moved — definitely
        // still a capture.
        #expect(!isVerdict(301, #"{"valid":false,"error":"License is revoked","reason":"not_entitled"}"#))
    }

    @Test func successStatusIsNotAVerdict() {
        // Belt and braces: the 200 path never reaches this predicate, but if it
        // ever did, a 200 is not an error verdict.
        #expect(!isVerdict(200, #"{"valid":false,"error":"License is revoked","reason":"not_entitled"}"#))
    }
}
