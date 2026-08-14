//
//  LicenseNetworkVerdictReportingTests.swift
//  hyperwhisperTests
//
//  HYPERWHISPER-SP / HYPERWHISPER-FM ("License validation server error"): the
//  macOS client was reporting a NORMAL license verdict to Sentry as a production
//  error. The backend answers "key not found" and "License is revoked/expired/
//  disabled" with HTTP 400 + `{"valid":false,"error":"…"}` (see `checkLicenseKey`
//  in nextjs/src/lib/license-validation.ts), and `validateLicense` captured every
//  non-200 that wasn't a retryable 5xx/429 — so a lapsed license, or a key
//  mistyped on Activate, filed a Sentry error on every launch-time revalidation.
//  A 400 is not a server error.
//
//  `LicenseNetworkService.isLicenseVerdictResponse` is the predicate that splits
//  the two cases, and it exists as a pure static specifically so this classifier
//  can be tested without a live URLSession, a Sentry transport, or the Rust core.
//  These tests pin BOTH halves of it:
//    - the ordinary-verdict shape is recognized (→ logged, not captured), and
//    - everything else — a different status code, a captive-portal HTML page, an
//      empty body, `valid: true`, a missing `valid`, a blank message — is NOT,
//      so genuine server faults keep reaching Sentry.
//
//  This predicate only gates the Sentry capture. It does not touch the verdict
//  returned to callers: an invalid license stays `.invalid`, with the same
//  user-facing message, whichever way this answers.
//
//  All license keys below are obviously-synthetic placeholders.
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

    @Test func keyNotFoundIsAnOrdinaryVerdict() {
        // The exact body the backend sends when a key isn't in the database and
        // can't be imported from Polar — i.e. a typo on the Activate screen.
        #expect(isVerdict(400, #"{"valid":false,"error":"License key not found"}"#))
    }

    @Test func revokedLicenseIsAnOrdinaryVerdict() {
        // `License is ${license.status}` for any status other than "granted" —
        // revoked, expired, disabled. A lapsed subscription hits this on every
        // launch-time revalidation; it is not a server fault.
        #expect(isVerdict(400, #"{"valid":false,"error":"License is revoked"}"#))
        #expect(isVerdict(400, #"{"valid":false,"error":"License is expired"}"#))
    }

    @Test func extraFieldsInTheBodyDoNotPreventRecognition() {
        // The decoder must ignore unknown keys — the backend is free to add
        // fields to this response without turning every verdict back into a
        // Sentry error.
        #expect(isVerdict(400, #"{"valid":false,"error":"License key not found","code":"not_found"}"#))
    }

    // MARK: - Not a verdict: body shape (must still be captured)

    @Test func emptyErrorMessageIsNotAVerdict() {
        // A 400 with no explanation isn't a recognizable verdict — something is
        // wrong and we still want to hear about it.
        #expect(!isVerdict(400, #"{"valid":false,"error":""}"#))
    }

    @Test func whitespaceOnlyErrorMessageIsNotAVerdict() {
        #expect(!isVerdict(400, #"{"valid":false,"error":"   "}"#))
        #expect(!isVerdict(400, #"{"valid":false,"error":"\n\t "}"#))
    }

    @Test func validTrueIsNotAVerdict() {
        // A 400 that claims the license IS valid is self-contradictory — a real
        // anomaly worth capturing.
        #expect(!isVerdict(400, #"{"valid":true}"#))
        #expect(!isVerdict(400, #"{"valid":true,"error":"License key not found"}"#))
    }

    @Test func missingValidFieldIsNotAVerdict() {
        // `valid` decodes to nil rather than throwing, and nil is not false — an
        // error-only body is not the shape this endpoint documents.
        #expect(!isVerdict(400, #"{"error":"something"}"#))
    }

    @Test func missingErrorFieldIsNotAVerdict() {
        #expect(!isVerdict(400, #"{"valid":false}"#))
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
        #expect(!isVerdict(400, #""License key not found""#))
    }

    // MARK: - Not a verdict: status code (the status must match too)

    @Test func otherClientErrorStatusesAreNotVerdicts() {
        // Same body shape, different status. Only 400 is the documented
        // ordinary-verdict status; a 401/403/404/422 from this endpoint means
        // something unexpected (auth, routing, schema) and must keep reaching
        // Sentry.
        let verdictShapedBody = #"{"valid":false,"error":"License key not found"}"#
        for status in [401, 403, 404, 422] {
            #expect(!isVerdict(status, verdictShapedBody))
        }
    }

    @Test func redirectStatusIsNotAVerdict() {
        // A 301 with a verdict-shaped body would mean the endpoint moved —
        // definitely still a capture.
        #expect(!isVerdict(301, #"{"valid":false,"error":"License is revoked"}"#))
    }

    @Test func successStatusIsNotAVerdict() {
        // Belt and braces: the 200 path never reaches this predicate, but if it
        // ever did, a 200 is not an error verdict.
        #expect(!isVerdict(200, #"{"valid":false,"error":"License key not found"}"#))
    }
}
