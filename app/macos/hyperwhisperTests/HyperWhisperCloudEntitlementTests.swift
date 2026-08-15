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

    /// The literal both toast paths match on to decide whether the inline error
    /// gets an "Open Settings" button.
    ///
    /// This constant is a *name for the thing under test*, not the source of
    /// truth: `bothProductionMatchersCarryTheAccountKeyMarker` below reads the
    /// real marker lists out of `AppState.showError` and
    /// `RecordingDialog.showTranscriptionErrorAlert` and asserts this literal is
    /// in both, so deleting the marker from either one fails a test rather than
    /// silently leaving a stale copy here passing.
    ///
    /// Why the button is derived from the *message* at all:
    /// `RecordingTranscriptionFlow+ErrorHandling` hands `AppState.showError`
    /// only `error.localizedDescription`, so the typed answer
    /// (`TranscriptionError.showSettingsButton`, which
    /// `AppState.showInlineError(_ error:)` would have used) never reaches the
    /// toast. A reworded Base string therefore drops the button, and asserting
    /// `showSettingsButton` alone would not notice.
    ///
    /// Known and out of scope: because both matchers compare English literals
    /// against an already-localized message, the button only ever appears in
    /// English — the other 39 translations match no marker (German renders
    /// "erfordert einen Kontoschlüssel"). That is parity with the
    /// `.unauthorized` case this error replaced, not a new regression; see the
    /// note on `settingsActionableMarkers` in `AppState.showError`.
    private static let toastSettingsMarker = "account key"

    private static let cloudAccountRequiredKey = "transcription.error.cloudAccountRequired"

    /// Reads a key's **English** value straight out of `Base.lproj`, so marker
    /// assertions do not depend on the test runner's locale.
    ///
    /// English is the string that has to match: both matchers compare English
    /// substrings, so the other 39 locales never match any marker. Pinning Base
    /// keeps these tests honest about what is actually guaranteed, and keeps
    /// them green on a non-English dev machine, where asserting against
    /// `localizedDescription` would fail for a reason unrelated to the code.
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

    // MARK: - Reading the production rules out of the production source
    //
    // The three things these tests need to pin — the classification arm for
    // `.cloudAccountRequired`, `AppState.showError`'s marker list, and
    // `RecordingDialog`'s marker list plus its error-prefix strip — are all
    // unreachable from a test as values:
    //
    // - `TranscriptionPipeline.classify(_:)` is an instance method on a
    //   `@MainActor` class whose `init()` wires coordinators, a retry
    //   controller and a keepalive ticker; constructing one in a unit test is
    //   not on the table, and nothing exposes the classification statically.
    // - Both marker lists are `private` locals declared inside a function body.
    //
    // Copying those values into the test is what let earlier versions of these
    // tests pass while the production arm/marker they were named after was
    // changed out from under them. So instead of copying, read the repo source
    // — the same `#filePath`-relative trick `CloudSttTierParityTests` and
    // `AppTypeClassifierTests` already use to assert against repo data rather
    // than a bundled copy. Scraping Swift text is cruder than decoding JSON,
    // but it is what makes these assertions actually fail when the production
    // line changes. If a rename moves a function or file out from under an
    // anchor below, these tests fail loudly with the anchor named, which is the
    // intended failure mode.

    private enum ProductionSourceError: Error, CustomStringConvertible {
        case unreadable(String)
        case anchorNotFound(String)

        var description: String {
            switch self {
            case .unreadable(let path):
                return "Could not read production source at \(path)"
            case .anchorNotFound(let anchor):
                return """
                Could not locate '\(anchor)' in the production source. It was \
                probably renamed or moved — update the anchor in \
                HyperWhisperCloudEntitlementTests rather than deleting the check.
                """
            }
        }
    }

    private struct ClassificationArm: Equatable {
        let category: String
        let kind: String
    }

    private struct RecordingDialogErrorAlertRules {
        /// The literal `showTranscriptionErrorAlert` strips off the front of
        /// `appState.lastTranscription` before matching.
        let errorPrefixStrip: String
        /// Every marker the dialog's own `showSettings` expression tests for.
        let settingsMarkers: [String]
    }

    /// Repo root, derived from this file's own compile-time path.
    private static var repoRoot: URL {
        URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()  // hyperwhisperTests
            .deletingLastPathComponent()  // macos
            .deletingLastPathComponent()  // app
            .deletingLastPathComponent()  // <repo root>
    }

    private static func productionSourceLines(_ repoRelativePath: String) throws -> [String] {
        let url = repoRoot.appendingPathComponent(repoRelativePath)
        guard let data = try? Data(contentsOf: url) else {
            throw ProductionSourceError.unreadable(repoRelativePath)
        }
        return String(decoding: data, as: UTF8.self).components(separatedBy: .newlines)
    }

    /// First double-quoted literal in `text`.
    private static func firstQuotedLiteral(in text: Substring) -> String? {
        guard let open = text.firstIndex(of: "\"") else { return nil }
        let afterOpen = text.index(after: open)
        guard let close = text[afterOpen...].firstIndex(of: "\"") else { return nil }
        return String(text[afterOpen..<close])
    }

    /// First double-quoted literal that follows `label` on `line`.
    private static func quotedLiteral(after label: String, in line: String) -> String? {
        guard let labelRange = line.range(of: label) else { return nil }
        return firstQuotedLiteral(in: line[labelRange.upperBound...])
    }

    private static func isComment(_ trimmedLine: String) -> Bool {
        trimmedLine.hasPrefix("//")
    }

    /// Every `(category, kind)` pair the production
    /// `TranscriptionPipeline.classify(_ error: TranscriptionError)` switch
    /// returns, keyed by enum case name.
    private static func transcriptionErrorClassificationArms() throws -> [String: ClassificationArm] {
        let lines = try productionSourceLines(
            "app/macos/hyperwhisper/Managers/Transcription/Pipeline/TranscriptionPipeline+ErrorClassification.swift"
        )
        // Bounded to the TranscriptionError switch so the HyperWhisperCloudError
        // overload below it cannot contribute arms under colliding case names.
        guard let start = lines.firstIndex(where: {
            $0.contains("func classify(_ error: TranscriptionError)")
        }) else {
            throw ProductionSourceError.anchorNotFound("func classify(_ error: TranscriptionError)")
        }
        guard let end = lines[start...].firstIndex(where: {
            $0.contains("func classify(_ error: HyperWhisperCloudError)")
        }) else {
            throw ProductionSourceError.anchorNotFound("func classify(_ error: HyperWhisperCloudError)")
        }

        var arms: [String: ClassificationArm] = [:]
        var pendingCase: String?
        for line in lines[start..<end] {
            let trimmed = line.trimmingCharacters(in: .whitespaces)
            guard !isComment(trimmed) else { continue }

            if trimmed.hasPrefix("case .") {
                // "case .serverError(let statusCode, _):" -> "serverError"
                pendingCase = String(
                    trimmed.dropFirst("case .".count).prefix { $0.isLetter || $0.isNumber }
                )
            }
            if let name = pendingCase,
               let category = quotedLiteral(after: "category: ", in: trimmed),
               let kind = quotedLiteral(after: "kind: ", in: trimmed) {
                arms[name] = ClassificationArm(category: category, kind: kind)
                pendingCase = nil
            }
        }
        return arms
    }

    /// The real `settingsActionableMarkers` array from `AppState.showError`.
    private static func appStateSettingsActionableMarkers() throws -> [String] {
        let lines = try productionSourceLines("app/macos/hyperwhisper/Models/AppState.swift")
        guard let start = lines.firstIndex(where: {
            $0.contains("let settingsActionableMarkers = [")
        }) else {
            throw ProductionSourceError.anchorNotFound("AppState settingsActionableMarkers")
        }
        guard let end = lines[start...].firstIndex(where: {
            $0.contains("settingsActionableMarkers.contains")
        }) else {
            throw ProductionSourceError.anchorNotFound("AppState settingsActionableMarkers use site")
        }

        let markers = lines[start...end].compactMap { line -> String? in
            let trimmed = line.trimmingCharacters(in: .whitespaces)
            guard !isComment(trimmed) else { return nil }
            return firstQuotedLiteral(in: trimmed[...])
        }
        guard !markers.isEmpty else {
            throw ProductionSourceError.anchorNotFound("AppState settingsActionableMarkers entries")
        }
        return markers
    }

    /// The real strip literal + marker list from
    /// `RecordingDialog.showTranscriptionErrorAlert`.
    private static func recordingDialogErrorAlertRules() throws -> RecordingDialogErrorAlertRules {
        let lines = try productionSourceLines("app/macos/hyperwhisper/Views/RecordingDialog.swift")
        guard let start = lines.firstIndex(where: {
            $0.contains("private func showTranscriptionErrorAlert(errorText: String)")
        }) else {
            throw ProductionSourceError.anchorNotFound("RecordingDialog.showTranscriptionErrorAlert")
        }
        guard let end = lines[start...].firstIndex(where: {
            $0.contains("appState.showInlineError(message: cleanError")
        }) else {
            throw ProductionSourceError.anchorNotFound("RecordingDialog showInlineError hand-off")
        }

        var strip: String?
        var markers: [String] = []
        for line in lines[start...end] {
            let trimmed = line.trimmingCharacters(in: .whitespaces)
            guard !isComment(trimmed) else { continue }

            if strip == nil,
               let value = quotedLiteral(after: "replacingOccurrences(of: ", in: trimmed) {
                strip = value
            }
            if let marker = quotedLiteral(after: "localizedCaseInsensitiveContains(", in: trimmed) {
                markers.append(marker)
            }
        }

        guard let strip else {
            throw ProductionSourceError.anchorNotFound("RecordingDialog error-prefix strip")
        }
        guard !markers.isEmpty else {
            throw ProductionSourceError.anchorNotFound("RecordingDialog settings markers")
        }
        return RecordingDialogErrorAlertRules(errorPrefixStrip: strip, settingsMarkers: markers)
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
        #expect(message != Self.cloudAccountRequiredKey)
        // The string is deliberately format-specifier-free.
        #expect(message.contains("%") == false)
    }

    // MARK: - Sentry / classification contract

    @Test func newCaseKeepsADistinctSentryFingerprintFromTheReal401() throws {
        // The Sentry report-vs-suppress decision itself is locked in
        // `TranscriptionErrorClassificationTests`. What matters here is that the
        // classification pair production actually returns for
        // `.cloudAccountRequired` does not collide with the "auth"/"unauthorized"
        // pair — that IS the live HYPERWHISPER-T2 fingerprint, and it must stay
        // distinguishable from a client-side refusal that never reached the
        // server.
        //
        // Both pairs are read out of the production switch rather than restated
        // here, because restating them is exactly what let the previous version
        // of this test stay green while the arm was flipped back to
        // auth/unauthorized — the regression this test is named for.
        let arms = try Self.transcriptionErrorClassificationArms()
        let refusal = try #require(
            arms["cloudAccountRequired"],
            "classify(_:) has no .cloudAccountRequired arm returning a classification"
        )
        let real401 = try #require(
            arms["unauthorized"],
            "classify(_:) has no .unauthorized arm returning a classification"
        )

        // Flipping the new arm back onto the 401's category/kind fails here…
        #expect(refusal != real401)
        #expect(refusal.category == "license")
        #expect(refusal.kind == "cloud_account_required")
        // …and so does quietly moving the real 401 onto the refusal's pair.
        #expect(real401.category == "auth")
        #expect(real401.kind == "unauthorized")

        // `sentryFingerprintForTranscriptionFailure` is `nonisolated static`, so
        // it is the one piece of the pipeline a test can call directly. Feed it
        // the real pairs and confirm the fingerprints Sentry groups on differ.
        let refusalFingerprint = TranscriptionPipeline.sentryFingerprintForTranscriptionFailure(
            classification: TranscriptionPipeline.TranscriptionErrorClassification(
                category: refusal.category,
                kind: refusal.kind,
                retryable: TranscriptionError.cloudAccountRequired(provider: "HyperWhisper Cloud").isRetryable,
                httpStatus: nil
            ),
            stage: "transcribe"
        )
        let real401Fingerprint = TranscriptionPipeline.sentryFingerprintForTranscriptionFailure(
            classification: TranscriptionPipeline.TranscriptionErrorClassification(
                category: real401.category,
                kind: real401.kind,
                retryable: TranscriptionError.unauthorized(provider: "HyperWhisper Cloud").isRetryable,
                httpStatus: nil
            ),
            stage: "transcribe"
        )

        #expect(refusalFingerprint != real401Fingerprint)
        #expect(refusalFingerprint.contains("license"))
        #expect(refusalFingerprint.contains("cloud_account_required"))
    }

    // MARK: - The toast path actually keeps its Settings button

    @Test func bothProductionMatchersCarryTheAccountKeyMarker() throws {
        // The two matchers are independent English-literal lists in two files,
        // and the dialog's runs second and overwrites the other's verdict, so
        // losing the marker from either one loses the button. Read both.
        let appStateMarkers = try Self.appStateSettingsActionableMarkers()
        let dialogRules = try Self.recordingDialogErrorAlertRules()

        #expect(
            appStateMarkers.contains(Self.toastSettingsMarker),
            "AppState.showError's settingsActionableMarkers no longer contains the account-key marker"
        )
        #expect(
            dialogRules.settingsMarkers.contains(Self.toastSettingsMarker),
            "RecordingDialog.showTranscriptionErrorAlert no longer matches the account-key marker"
        )

        // The dialog's list is deliberately a strict subset of AppState's; it is
        // not a mirror. Pinned so a future reader cannot assume the two lists
        // are in sync (see the comment in RecordingDialog).
        #expect(dialogRules.settingsMarkers.count < appStateMarkers.count)
    }

    @Test func englishMessageMatchesTheMarkerTheToastPathLooksFor() throws {
        // Locale-independent: reword the Base string past the marker and this
        // fails, which is exactly the regression that shipped on this branch.
        // nil here means Base.lproj is missing the key entirely.
        let base = try #require(Self.baseLocalizedValue(forKey: Self.cloudAccountRequiredKey))
        #expect(base.localizedCaseInsensitiveContains(Self.toastSettingsMarker))

        // And against the real list, so a deleted marker fails too, not just a
        // reworded string.
        let appStateMarkers = try Self.appStateSettingsActionableMarkers()
        #expect(appStateMarkers.contains(where: { base.localizedCaseInsensitiveContains($0) }))
    }

    @Test func localizedMessageMatchesTheMarkerTheToastPathLooksFor() throws {
        let error = TranscriptionError.cloudAccountRequired(provider: "HyperWhisper Cloud")
        let message = error.errorDescription ?? ""
        let english = try #require(Self.baseLocalizedValue(forKey: Self.cloudAccountRequiredKey))
        let markers = try Self.appStateSettingsActionableMarkers()

        // This is the exact predicate `AppState.showError` runs, on the English
        // string it is capable of matching.
        #expect(markers.contains(where: { english.localizedCaseInsensitiveContains($0) }))

        // Whatever locale the runner is in, the app must still produce a real
        // localized string, and it is that string the matchers receive.
        #expect(message.isEmpty == false)
        #expect(message != Self.cloudAccountRequiredKey)

        // On an English runtime the two are the same string, so the marker has
        // to survive end to end. On any other locale it deliberately will not —
        // that is the 39-translation gap documented on `toastSettingsMarker`,
        // and failing here for it would be failing for the runner's locale
        // rather than for the code.
        if message == english {
            #expect(markers.contains(where: { message.localizedCaseInsensitiveContains($0) }))
        }
    }

    @Test func settingsButtonSurvivesTheErrorHandlingHandoff() throws {
        // `RecordingTranscriptionFlow+ErrorHandling` throws away the typed error
        // and passes only the message, so re-derive the button the way
        // `AppState.showError` does — with AppState's own marker list — and
        // check it still comes out true. The `.unauthorized` this case replaced
        // matched "unauthorized"/"api key"; "requires an account key" matched
        // nothing, and the button vanished.
        let error = TranscriptionError.cloudAccountRequired(provider: "HyperWhisper Cloud")
        let handedToShowError = (error as Error).localizedDescription

        // Erasing to `Error` must not lose the LocalizedError text — that is the
        // only string the toast ever sees. Locale-independent: both sides are
        // resolved in the same locale.
        #expect(handedToShowError == (error.errorDescription ?? ""))

        // The marker half is asserted against Base English, because that is the
        // only locale either matcher can match in.
        let english = try #require(Self.baseLocalizedValue(forKey: Self.cloudAccountRequiredKey))
        let markers = try Self.appStateSettingsActionableMarkers()
        #expect(markers.contains(where: { english.localizedCaseInsensitiveContains($0) }))

        // The typed property and the message-derived answer must agree.
        #expect(error.showSettingsButton)
    }

    @Test func settingsButtonSurvivesTheRecordingDialogPrefixStrip() throws {
        // The dialog reads `appState.lastTranscription`, which the error handler
        // sets to "Error: <message>", then strips the prefix before matching.
        // Both the strip literal and the marker list come out of
        // RecordingDialog.swift itself: deleting the marker there, or changing
        // the strip so it stops matching the handler's prefix, fails this test.
        let rules = try Self.recordingDialogErrorAlertRules()
        let english = try #require(Self.baseLocalizedValue(forKey: Self.cloudAccountRequiredKey))

        let lastTranscription = "Error: \(english)"
        let cleanError = lastTranscription.replacingOccurrences(
            of: rules.errorPrefixStrip,
            with: ""
        )
        #expect(
            cleanError == english,
            "the dialog's strip literal no longer removes the handler's error prefix cleanly"
        )
        #expect(
            rules.settingsMarkers.contains(where: { cleanError.localizedCaseInsensitiveContains($0) }),
            "no marker in RecordingDialog's own list matches the cloud-account-required message"
        )
    }

    // MARK: - Streaming path is gated too

    @Test func streamingCloudSessionIsRefusedWhenUnlicensed() throws {
        // `RecordingTranscriptionFlow+Streaming` applies the same guard before
        // the WebSocket session starts, passing the picker's display name. The
        // ws endpoint validates an account/licence key only, so an unlicensed
        // session was a guaranteed 401 reported as "invalid API key".
        let provider = StreamingTranscriptionProvider.hyperwhisperCloud.displayName
        #expect(provider == "HyperWhisper Cloud")

        // Resolved up front so the marker assertion below stays locale- and
        // copy-independent.
        let english = try #require(Self.baseLocalizedValue(forKey: Self.cloudAccountRequiredKey))
        let markers = try Self.appStateSettingsActionableMarkers()

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
            // into `AppState.showError` — so the message has to carry a marker
            // there too, or the streaming refusal loses its button as well.
            #expect(error.errorDescription == error.localizedDescription)
            #expect(markers.contains(where: { english.localizedCaseInsensitiveContains($0) }))
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
        // Locale-independent by construction: sibling arms return hardcoded
        // English, not a localized string.
        #expect(message.localizedCaseInsensitiveContains(Self.toastSettingsMarker))
        #expect(hint != nil)
    }
}
