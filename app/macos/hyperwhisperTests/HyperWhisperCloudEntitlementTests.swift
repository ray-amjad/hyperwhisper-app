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

    /// The literal the *toast* path matches on to decide whether the inline
    /// error gets an "Open Settings" button.
    ///
    /// Both matchers substring-match English markers against the already
    /// localized message, so the marker is duplicated in three places and this
    /// constant is the fourth. It must stay byte-identical to:
    /// - the `"account key"` entry in `AppState.showError`'s
    ///   `settingsActionableMarkers`, and
    /// - the `"account key"` clause in
    ///   `RecordingDialog.showTranscriptionErrorAlert`.
    ///
    /// Both are `private` locals, so a test cannot read them — pinning the
    /// literal here is the closest available check, and it is the one that
    /// catches the real regression: `RecordingTranscriptionFlow+ErrorHandling`
    /// hands `AppState.showError` only `error.localizedDescription`, so the
    /// button is re-derived from the *message*, not from
    /// `TranscriptionError.showSettingsButton`. A reworded Base string silently
    /// drops the button; asserting `showSettingsButton` alone would not notice.
    private static let toastSettingsMarker = "account key"

    private static let cloudAccountRequiredKey = "transcription.error.cloudAccountRequired"

    /// Reads a key's **English** value straight out of `Base.lproj`, so the
    /// marker assertion below does not depend on the test runner's locale.
    ///
    /// English is the string that has to match: both matchers compare English
    /// substrings, so the other 39 locales never match any marker. That gap is
    /// pre-existing and deliberately out of scope here — pinning Base keeps this
    /// test honest about what is actually guaranteed.
    /// Falls back to `Bundle.main` (i.e. the runtime lookup) if the built app
    /// lays its `.lproj` resources out differently than expected, so a bundle
    /// layout surprise degrades this to the runtime assertion instead of
    /// hard-failing.
    private static func baseLocalizedValue(forKey key: String) -> String? {
        let sentinel = "\u{0}__missing__"
        let bundle: Bundle = ["Base", "en"]
            .compactMap { Bundle.main.path(forResource: $0, ofType: "lproj") }
            .compactMap { Bundle(path: $0) }
            .first ?? .main
        let value = bundle.localizedString(forKey: key, value: sentinel, table: nil)
        return value == sentinel ? nil : value
    }

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

    // MARK: - The toast path actually keeps its Settings button

    @Test func englishMessageMatchesTheMarkerTheToastPathLooksFor() throws {
        // Locale-independent: reword the Base string past the marker and this
        // fails, which is exactly the regression that shipped on this branch.
        // nil here means Base.lproj is missing the key entirely.
        let base = try #require(Self.baseLocalizedValue(forKey: Self.cloudAccountRequiredKey))
        #expect(base.localizedCaseInsensitiveContains(Self.toastSettingsMarker))
    }

    @Test func localizedMessageMatchesTheMarkerTheToastPathLooksFor() {
        let error = TranscriptionError.cloudAccountRequired(provider: "HyperWhisper Cloud")
        let message = error.errorDescription ?? ""

        // This is the exact predicate both matchers run, on the string the app
        // actually produces. (English runner — see `baseLocalizedValue`.)
        #expect(message.localizedCaseInsensitiveContains(Self.toastSettingsMarker))
    }

    @Test func settingsButtonSurvivesTheErrorHandlingHandoff() {
        // `RecordingTranscriptionFlow+ErrorHandling` throws away the typed error
        // and passes only the message, so re-derive the button the way
        // `AppState.showError` does and check it still comes out true. The
        // `.unauthorized` this case replaced matched "unauthorized"/"api key";
        // "requires an account key" matched nothing, and the button vanished.
        let error = TranscriptionError.cloudAccountRequired(provider: "HyperWhisper Cloud")
        let handedToShowError = (error as Error).localizedDescription

        // Erasing to `Error` must not lose the LocalizedError text — that is the
        // only string the toast ever sees.
        #expect(handedToShowError == (error.errorDescription ?? ""))
        #expect(handedToShowError.localizedCaseInsensitiveContains(Self.toastSettingsMarker))

        // The typed property and the message-derived answer must agree.
        #expect(error.showSettingsButton)
    }

    @Test func settingsButtonSurvivesTheRecordingDialogPrefixStrip() {
        // The dialog reads `appState.lastTranscription`, which the error handler
        // sets to "Error: <message>", then strips the prefix before matching.
        let error = TranscriptionError.cloudAccountRequired(provider: "HyperWhisper Cloud")
        let lastTranscription = "Error: \(error.localizedDescription)"
        let cleanError = lastTranscription.replacingOccurrences(of: "Error: ", with: "")

        #expect(cleanError.localizedCaseInsensitiveContains(Self.toastSettingsMarker))
    }

    // MARK: - Streaming path is gated too

    @Test func streamingCloudSessionIsRefusedWhenUnlicensed() {
        // `RecordingTranscriptionFlow+Streaming` applies the same guard before
        // the WebSocket session starts, passing the picker's display name. The
        // ws endpoint validates an account/licence key only, so an unlicensed
        // session was a guaranteed 401 reported as "invalid API key".
        let provider = StreamingTranscriptionProvider.hyperwhisperCloud.displayName
        #expect(provider == "HyperWhisper Cloud")

        do {
            try HyperWhisperCloudEntitlement.requireLicense(isLicensed: false, provider: provider)
            Issue.record("Expected an unlicensed streaming session to be refused")
        } catch let error as TranscriptionError {
            guard case .cloudAccountRequired(let named) = error else {
                Issue.record("Expected .cloudAccountRequired, got \(error)")
                return
            }
            #expect(named == provider)

            // The streaming path surfaces the refusal via
            // `cancelRecordingWithError(error.localizedDescription)`, which routes
            // into `AppState.showError` — so the message has to carry the marker
            // there too, or the streaming refusal loses its button as well.
            #expect(error.localizedDescription.localizedCaseInsensitiveContains(Self.toastSettingsMarker))
        } catch {
            Issue.record("Expected a TranscriptionError, got \(error)")
        }
    }

    @Test func streamingCloudSessionIsAllowedWhenLicensed() throws {
        // Fail-closed only — a licensed session must not be refused client-side.
        try HyperWhisperCloudEntitlement.requireLicense(
            isLicensed: true,
            provider: StreamingTranscriptionProvider.hyperwhisperCloud.displayName
        )
    }

    // MARK: - Local HTTP API does not leak a generic failure

    @Test func localAPIMapsCloudAccountRequiredToAnExplicitCode() {
        let (code, message, hint) = LocalAPIResponder.mapTranscriptionError(
            TranscriptionError.cloudAccountRequired(provider: "HyperWhisper Cloud")
        )

        // Without an explicit arm this fell into `default:` and returned the
        // generic TRANSCRIPTION_FAILED — the leak the function exists to prevent.
        #expect(code != .transcriptionFailed)
        #expect(code == .missingAPIKey)
        // Sibling arms return hardcoded English, not a localized string.
        #expect(message.localizedCaseInsensitiveContains(Self.toastSettingsMarker))
        #expect(hint != nil)
    }
}
