//
//  HyperWhisperCloudEntitlementTests.swift
//  hyperwhisperTests
//
//  Locks the HyperWhisper Cloud fail-fast pre-check (HYPERWHISPER-T2).
//

import Foundation
import Testing
@testable import HyperWhisper

struct HyperWhisperCloudEntitlementTests {

    // MARK: - The guard itself

    @Test func unlicensedRequestIsRefusedBeforeAnyNetworkWork() throws {
        do {
            try HyperWhisperCloudEntitlement.requireLicense(
                isLicensed: false,
                provider: "HyperWhisper Cloud"
            )
            Issue.record("Expected an unlicensed request to be refused")
        } catch let error as TranscriptionError {
            guard case .cloudAccountRequired(let provider) = error else {
                Issue.record("Expected .cloudAccountRequired, got \(error)")
                return
            }
            #expect(provider == "HyperWhisper Cloud")
        }
    }

    @Test func licensedRequestIsAllowedThrough() throws {
        // Fail-closed only: a `true` here means "worth sending", not "entitled".
        // The server remains the sole authority and still validates the key.
        try HyperWhisperCloudEntitlement.requireLicense(
            isLicensed: true,
            provider: "HyperWhisper Cloud"
        )
    }

    @Test func providerNameIsCarriedThroughForRoutedProviders() throws {
        // Routed providers (AzureMAI / GoogleChirp) pass their own display name.
        do {
            try HyperWhisperCloudEntitlement.requireLicense(
                isLicensed: false,
                provider: "Azure AI Speech"
            )
            Issue.record("Expected an unlicensed request to be refused")
        } catch let error as TranscriptionError {
            guard case .cloudAccountRequired(let provider) = error else {
                Issue.record("Expected .cloudAccountRequired, got \(error)")
                return
            }
            #expect(provider == "Azure AI Speech")
        }
    }

    // MARK: - How the resulting error behaves

    @Test func cloudAccountRequiredIsTerminalAndActionable() {
        let error = TranscriptionError.cloudAccountRequired(provider: "HyperWhisper Cloud")

        // Retrying cannot help — the account key is missing, not flaky.
        #expect(error.isRetryable == false)
        // The user has a concrete remedy, so surface it with a settings CTA.
        #expect(error.showSettingsButton)
        #expect(error.shouldSurfaceInline)
    }

    @Test func cloudAccountRequiredHasALocalizedMessageAndNoRawKeyLeak() {
        let error = TranscriptionError.cloudAccountRequired(provider: "HyperWhisper Cloud")
        let message = error.errorDescription ?? ""

        // `String.localized` has no Base fallback: a missing key renders the raw
        // key to the user. Catch that here rather than in a screenshot.
        #expect(message.isEmpty == false)
        #expect(message != "transcription.error.cloudAccountRequired")
        // The string is deliberately format-specifier-free.
        #expect(message.contains("%") == false)
    }

    // MARK: - Sentry / classification contract

    @Test func newCaseKeepsADistinctSentryFingerprintFromTheReal401() {
        // The Sentry report-vs-suppress decision itself is locked in
        // `TranscriptionErrorClassificationTests` (which mirrors the switch
        // rather than instantiating the @MainActor pipeline). What matters here
        // is that the classification pair we chose does not collide with the
        // "auth"/"unauthorized" pair — that IS the live HYPERWHISPER-T2
        // fingerprint, and it must stay distinguishable from a client-side
        // refusal that never reached the server.
        let refusal = TranscriptionPipeline.sentryFingerprintForTranscriptionFailure(
            classification: TranscriptionPipeline.TranscriptionErrorClassification(
                category: "license",
                kind: "cloud_account_required",
                retryable: false,
                httpStatus: nil
            ),
            stage: "transcribe"
        )
        let real401 = TranscriptionPipeline.sentryFingerprintForTranscriptionFailure(
            classification: TranscriptionPipeline.TranscriptionErrorClassification(
                category: "auth",
                kind: "unauthorized",
                retryable: false,
                httpStatus: nil
            ),
            stage: "transcribe"
        )

        #expect(refusal != real401)
        #expect(refusal.contains("license"))
        #expect(refusal.contains("cloud_account_required"))
    }
}
