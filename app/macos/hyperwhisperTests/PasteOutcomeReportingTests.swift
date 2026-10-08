//
//  PasteOutcomeReportingTests.swift
//  hyperwhisperTests
//
//  Guards the two properties the auto-paste diagnostics depend on:
//  the outcome slugs are stable (Sentry queries and dashboards key off them),
//  and only genuine failures raise an event.
//

import AppKit
import ApplicationServices
import Foundation
import Testing
@testable import HyperWhisper

// Serialized: the executePasteAsync tests below share AccessibilityHelper.shared,
// its test seams, TextDeliveryGate and the general pasteboard.
@Suite(.serialized)
@MainActor
struct PasteOutcomeReportingTests {

    /// The raw values become the Sentry message, the `paste_outcome` tag and the
    /// `paste_outcome` extra. A rename would silently orphan every saved query,
    /// so pin them here.
    @Test func outcomeSlugsAreStable() {
        #expect(AccessibilityHelper.PasteOutcome.success.rawValue == "success")
        #expect(AccessibilityHelper.PasteOutcome.noAccessibilityPermission.rawValue == "no_accessibility_permission")
        #expect(AccessibilityHelper.PasteOutcome.targetLost.rawValue == "target_lost")
        #expect(AccessibilityHelper.PasteOutcome.targetUnknown.rawValue == "target_unknown")
        #expect(AccessibilityHelper.PasteOutcome.secureField.rawValue == "secure_field")
        #expect(AccessibilityHelper.PasteOutcome.noFocusedField.rawValue == "no_focused_field")
        #expect(AccessibilityHelper.PasteOutcome.cancelled.rawValue == "cancelled")
        #expect(AccessibilityHelper.PasteOutcome.suppressed.rawValue == "suppressed")
        #expect(AccessibilityHelper.PasteOutcome.commandFailed.rawValue == "command_failed")
    }

    /// Resolution slugs are Sentry fields used by saved queries.
    @Test func targetResolutionSlugsAreStable() {
        #expect(AccessibilityHelper.TargetResolutionOutcome.notAttempted.rawValue == "not_attempted")
        #expect(AccessibilityHelper.TargetResolutionOutcome.processNotFound.rawValue == "process_not_found")
        #expect(AccessibilityHelper.TargetResolutionOutcome.expectedBundleMissing.rawValue == "expected_bundle_missing")
        #expect(AccessibilityHelper.TargetResolutionOutcome.resolvedBundleMissing.rawValue == "resolved_bundle_missing")
        #expect(AccessibilityHelper.TargetResolutionOutcome.bundleMismatch.rawValue == "bundle_mismatch")
        #expect(AccessibilityHelper.TargetResolutionOutcome.matched.rawValue == "matched")
    }

    /// The classifier distinguishes all states without a live application.
    @Test func targetResolutionClassificationMatrix() {
        typealias Resolution = AccessibilityHelper.TargetResolutionOutcome
        let cases: [(Bool, Bool, String?, String?, Resolution)] = [
            (false, false, nil, nil, .notAttempted),
            (true, false, "com.example.expected", nil, .processNotFound),
            (true, true, nil, "com.example.actual", .expectedBundleMissing),
            (true, true, "com.example.expected", nil, .resolvedBundleMissing),
            (true, true, "com.example.expected", "com.example.actual", .bundleMismatch),
            (true, true, "com.example.expected", "com.example.expected", .matched),
        ]

        for (capturedPIDExists, resolvedApplicationExists, expected, resolved, outcome) in cases {
            #expect(Resolution.classify(
                capturedPIDExists: capturedPIDExists,
                resolvedApplicationExists: resolvedApplicationExists,
                expectedBundleID: expected,
                resolvedBundleID: resolved
            ) == outcome)
        }
    }

    /// Missing record-start identity keeps the legacy PID-existence acceptance.
    @Test func targetResolutionAcceptanceIsUnchanged() {
        typealias Resolution = AccessibilityHelper.TargetResolutionOutcome
        #expect(Resolution.expectedBundleMissing.allowsPasteTarget)
        #expect(Resolution.matched.allowsPasteTarget)
        #expect(Resolution.notAttempted.allowsPasteTarget == false)
        #expect(Resolution.processNotFound.allowsPasteTarget == false)
        #expect(Resolution.resolvedBundleMissing.allowsPasteTarget == false)
        #expect(Resolution.bundleMismatch.allowsPasteTarget == false)
    }

    /// A PID-reuse report keeps expected and actual identities separate.
    @Test func mismatchRetainsResolvedBundleWithoutReplacingExpectedBundle() {
        let expected = "com.example.expected"
        let resolved = "com.example.actual"
        var attempt = AccessibilityHelper.PasteAttempt(targetBundleID: expected)
        let outcome = AccessibilityHelper.TargetResolutionOutcome.classify(
            capturedPIDExists: true,
            resolvedApplicationExists: true,
            expectedBundleID: expected,
            resolvedBundleID: resolved
        )
        attempt.recordTargetResolution(outcome, resolvedBundleID: resolved)

        #expect(attempt.targetResolution == .bundleMismatch)
        #expect(attempt.targetBundleID == expected)
        #expect(attempt.resolvedTargetBundleID == resolved)
    }

    /// A deliberate refusal must never reach Sentry. A secure field, a focus the
    /// user moved away, a superseded paste and the onboarding gate are all
    /// normal, and all four are frequent enough to flood the issue stream.
    @Test func deliberateRefusalsAreNotReported() {
        #expect(AccessibilityHelper.PasteOutcome.success.isReportable == false)
        #expect(AccessibilityHelper.PasteOutcome.secureField.isReportable == false)
        #expect(AccessibilityHelper.PasteOutcome.noFocusedField.isReportable == false)
        #expect(AccessibilityHelper.PasteOutcome.cancelled.isReportable == false)
        #expect(AccessibilityHelper.PasteOutcome.suppressed.isReportable == false)
    }

    /// A transcript that did not arrive because of a defect or a broken setup
    /// must be reported — that is the whole point of the change.
    @Test func realFailuresAreReported() {
        #expect(AccessibilityHelper.PasteOutcome.commandFailed.isReportable)
        #expect(AccessibilityHelper.PasteOutcome.targetLost.isReportable)
        #expect(AccessibilityHelper.PasteOutcome.targetUnknown.isReportable)
        #expect(AccessibilityHelper.PasteOutcome.noAccessibilityPermission.isReportable)
    }

    /// Only a failed keystroke is a defect in the app. The other two reportable
    /// outcomes describe the user's environment, so they stay at warning level.
    @Test func onlyAFailedKeystrokeIsADefect() {
        #expect(AccessibilityHelper.PasteOutcome.commandFailed.isDefect)
        #expect(AccessibilityHelper.PasteOutcome.targetLost.isDefect == false)
        #expect(AccessibilityHelper.PasteOutcome.targetUnknown.isDefect == false)
        #expect(AccessibilityHelper.PasteOutcome.noAccessibilityPermission.isDefect == false)
    }

    /// #1034: only a secure field and the onboarding gate withhold the text on
    /// purpose, so only they schedule a restore on an exit that pasted nothing.
    /// The property's switch is exhaustive, so a new outcome cannot compile
    /// without a decision; this pins the decision for every current case.
    @Test func onlySecureFieldAndSuppressedWithholdTextOnPurpose() {
        #expect(AccessibilityHelper.PasteOutcome.secureField.withholdsTextOnPurpose)
        #expect(AccessibilityHelper.PasteOutcome.suppressed.withholdsTextOnPurpose)
        #expect(AccessibilityHelper.PasteOutcome.success.withholdsTextOnPurpose == false)
        #expect(AccessibilityHelper.PasteOutcome.noAccessibilityPermission.withholdsTextOnPurpose == false)
        #expect(AccessibilityHelper.PasteOutcome.targetLost.withholdsTextOnPurpose == false)
        #expect(AccessibilityHelper.PasteOutcome.targetUnknown.withholdsTextOnPurpose == false)
        #expect(AccessibilityHelper.PasteOutcome.noFocusedField.withholdsTextOnPurpose == false)
        #expect(AccessibilityHelper.PasteOutcome.cancelled.withholdsTextOnPurpose == false)
        #expect(AccessibilityHelper.PasteOutcome.commandFailed.withholdsTextOnPurpose == false)
    }

    /// PRIVACY: the attempt metadata records how long the text was, never what
    /// it said.
    @Test func attemptRecordsLengthNotText() {
        let attempt = AccessibilityHelper.PasteAttempt(
            targetBundleID: "com.apple.Safari",
            characterCount: "hello world".count
        )
        #expect(attempt.characterCount == 11)
        #expect(attempt.targetBundleID == "com.apple.Safari")
        #expect(attempt.hadCapturedTarget == false)
        #expect(attempt.targetResolution == .notAttempted)
        #expect(attempt.resolvedTargetBundleID == nil)
        #expect(attempt.usedFocusRetry == false)

        // No field may carry the transcript. A new field named for the content
        // fails here before it can reach a log line or a Sentry event.
        let forbidden = ["text", "transcript", "content", "prompt", "message"]
        for field in Mirror(reflecting: attempt).children.compactMap(\.label) {
            let lowered = field.lowercased()
            #expect(forbidden.contains { lowered.contains($0) } == false,
                    "PasteAttempt.\(field) may carry transcript content")
        }
    }

    /// #783: when the captured paste target is gone, `executePasteAsync` refuses
    /// to paste and leaves the transcript on the clipboard for a manual Cmd+V.
    /// Nothing was pasted, so it must not arm a clipboard restoration: with the
    /// default settings that timer overwrites the transcript 10 s later.
    ///
    /// Drives the real refuse branch. The permission seam gets past the
    /// Accessibility guard (CI never grants it). The target is this process
    /// under a bundle ID it does not have, so `resolveCapturedTarget` rejects it
    /// (bundle mismatch, or process not found) and `capturedTargetLost` is true.
    /// No app is activated and no keystroke is sent.
    ///
    /// Settings are READ from the live `SettingsManager`, never written: they
    /// are `@AppStorage`, and a write from this app-hosted bundle can abort the
    /// run (see StreamingSettingsBindingTests). The #1034 tests below touch the
    /// same pasteboard and `AccessibilityHelper` paste state, so the suite is
    /// `.serialized`.
    ///
    /// Two shared traits (`.restoreClipboardIsOn`, `.sentryIsOff`, defined at
    /// the bottom of this file) SKIP (never fail) the test on a Mac where it
    /// cannot run honestly. Both read the live state on the main actor, because
    /// `SettingsManager` is `@MainActor`. CI has an empty DSN and default
    /// settings, so it runs there.
    /// - Restore off: the #783 code arms nothing either, so the run could not
    ///   tell the fix from the defect. The setting is read, never written.
    /// - Sentry live: the refusal is a reportable `target_lost`, and a test must
    ///   never send it. `SentryService.shutdown()` would stop that, but it also
    ///   purges the on-disk Sentry queue this dev Mac shares with the installed
    ///   app, and nothing restarts the SDK, so the test leaves Sentry alone.
    ///
    /// Debug only: the permission seam exists only in a Debug build, which is
    /// the configuration the scheme and CI test with.
    #if DEBUG
    @Test(.restoreClipboardIsOn, .sentryIsOff)
    func refusedPasteKeepsTranscriptOnClipboardWithoutArmingRestore() async throws {
        let helper = AccessibilityHelper.shared
        let transcript = "refused transcript #783"

        // No AX requirement: the refusal returns before the focus check, so the
        // run reads no focus and sends no keystroke on any Mac.
        try await withSavedPasteState(deliverySuppressed: false,
                                      requireAccessibilityUntrusted: false) {
            let result = await helper.executePasteAsync(
                transcript,
                previousAppPID: ProcessInfo.processInfo.processIdentifier,
                previousAppBundleID: "com.example.hyperwhisper-tests.not-this-process",
                settings: SettingsManager.shared
            )

            let refused: Bool
            if case .noFocusedField = result { refused = true } else { refused = false }
            #expect(refused, "expected the target-lost refusal (.noFocusedField), got \(result)")
            // The #783 defect armed the restoration here, before it returned.
            #expect(helper.activeRestorationWorkItem == nil)
            #expect(NSPasteboard.general.string(forType: .string) == transcript)
            // #1061: the next recording keeps the older clipboard.
            #expect(helper.keptClipboardSnapshotChangeCount == NSPasteboard.general.changeCount)
        }
    }
    #endif

    // MARK: - #1034: exits that paste nothing keep the transcript

    /// #1034: when no paste target is focused, nothing is pasted, the dialog
    /// stays open, and the transcript is left on the clipboard for a manual
    /// Cmd+V. The exit must not arm a clipboard restoration, which would
    /// overwrite the transcript 10 s later.
    ///
    /// The target is this process under its own bundle ID, so it is accepted and
    /// the #783 refusal (which also returns `.noFocusedField`) is not taken; the
    /// probe count proves the run reached the focus check. The focus seam then
    /// reports no field. The only app activated is this test host, and no
    /// keystroke is sent on any Mac, so the traits are those of the #783 test.
    ///
    /// On a Mac that grants Accessibility, `isSecureFieldFocused()` reads the
    /// real system-wide focused element before the seam is asked. Only a
    /// password field focused in the frontmost app changes the exit; the test
    /// then fails with `.secureField`, it never passes vacuously. The cancelled
    /// and suppressed tests below share this.
    #if DEBUG
    @Test(.restoreClipboardIsOn, .sentryIsOff)
    func noFocusedFieldKeepsTranscriptOnClipboardWithoutArmingRestore() async throws {
        let helper = AccessibilityHelper.shared
        let probe = CanPasteProbe()
        let transcript = "no focused field transcript #1034"

        try await withSavedPasteState(deliverySuppressed: false,
                                      requireAccessibilityUntrusted: false) {
            helper.canPasteOverrideForTesting = {
                probe.calls += 1
                return false
            }

            let result = await pasteIntoThisProcess(transcript)

            let noFocusedField: Bool
            if case .noFocusedField = result { noFocusedField = true } else { noFocusedField = false }
            #expect(noFocusedField, "expected .noFocusedField, got \(result)")
            // The #783 refusal returns before the focus check and never calls the seam.
            #expect(probe.calls > 0, "the run never reached the focus check")
            // The #1034 defect armed the restoration here, before it returned.
            #expect(helper.activeRestorationWorkItem == nil)
            #expect(NSPasteboard.general.string(forType: .string) == transcript)
            #expect(helper.keptClipboardSnapshotChangeCount == NSPasteboard.general.changeCount)
        }
    }

    /// #1034: a paste cancelled just before the keystroke (a newer paste
    /// superseded it) pasted nothing, so it must not arm a restoration either.
    /// The focus seam cancels the in-flight paste task and reports a field, so
    /// the run reaches the `Task.isCancelled` check right before the paste. That
    /// check returns before `sendPasteCommand()`, so no keystroke is sent on any
    /// Mac and the test needs no Accessibility skip.
    @Test(.restoreClipboardIsOn, .sentryIsOff)
    func cancelledBeforePasteKeepsTranscriptOnClipboardWithoutArmingRestore() async throws {
        let helper = AccessibilityHelper.shared
        let probe = CanPasteProbe()
        let transcript = "cancelled transcript #1034"

        try await withSavedPasteState(deliverySuppressed: false,
                                      requireAccessibilityUntrusted: false) {
            helper.canPasteOverrideForTesting = {
                probe.calls += 1
                helper.currentPasteTask?.cancel()
                return true
            }

            let result = await pasteIntoThisProcess(transcript)

            var failure: Error?
            if case .failed(let error) = result { failure = error }
            #expect((failure as? CancellationError) != nil,
                    "expected .failed(CancellationError), got \(result)")
            #expect(probe.calls > 0, "the run never reached the focus check")
            #expect(helper.activeRestorationWorkItem == nil)
            #expect(NSPasteboard.general.string(forType: .string) == transcript)
            // The newer paste that cancelled this one owns the clipboard (#1061).
            #expect(helper.keptClipboardSnapshotChangeCount == nil)
        }
    }

    /// #1034: when `sendPasteCommand()` fails, nothing was pasted, so the exit
    /// must not arm a restoration. The focus seam reports a field and this Mac
    /// does not grant Accessibility, so `sendPasteCommand()` returns false at
    /// its permission check and the exit classifies `.noAccessibilityPermission`.
    /// A real `.commandFailed` needs a CGEvent that cannot be built, which a test
    /// cannot arrange; this drives the same exit and the same decision.
    ///
    /// The one test here that keeps `.accessibilityIsNotTrusted`: it reaches
    /// `sendPasteCommand()` with the gate open and the focus seam true, so on a
    /// Mac that grants Accessibility it would post a real Cmd+V.
    @Test(.restoreClipboardIsOn, .sentryIsOff, .accessibilityIsNotTrusted)
    func sendPasteWithoutPermissionKeepsTranscriptOnClipboardWithoutArmingRestore() async throws {
        let helper = AccessibilityHelper.shared
        let probe = CanPasteProbe()
        let transcript = "send failed transcript #1034"

        try await withSavedPasteState(deliverySuppressed: false,
                                      requireAccessibilityUntrusted: true) {
            helper.canPasteOverrideForTesting = {
                probe.calls += 1
                return true
            }

            let result = await pasteIntoThisProcess(transcript)

            var failure: Error?
            if case .failed(let error) = result { failure = error }
            // The send-failed exit returns this NSError; a cancellation does not.
            #expect(failure.map { ($0 as NSError).domain } == "AccessibilityHelper",
                    "expected the send-failed exit, got \(result)")
            #expect(probe.calls > 0, "the run never reached the focus check")
            // Not the onboarding gate: that classification keeps its restore.
            #expect(TextDeliveryGate.isSuppressed == false)
            #expect(helper.activeRestorationWorkItem == nil)
            #expect(NSPasteboard.general.string(forType: .string) == transcript)
            #expect(helper.keptClipboardSnapshotChangeCount == NSPasteboard.general.changeCount)
        }
    }

    /// #1034 keeps one restore on the send-failed exit: the onboarding gate
    /// (`TextDeliveryGate`) withholds the text on purpose, like a secure field
    /// (#783), so the exit classifies `.suppressed` and still arms it. Pins that
    /// the fix narrowed the restore rather than removing it. The gate guard is
    /// the first check in `sendPasteCommand()`, so it refuses before the
    /// permission check and no keystroke is sent on any Mac; the test needs no
    /// Accessibility skip.
    @Test(.restoreClipboardIsOn, .sentryIsOff)
    func suppressedSendPasteStillArmsRestore() async throws {
        let helper = AccessibilityHelper.shared
        let probe = CanPasteProbe()
        let transcript = "suppressed transcript #1034"

        try await withSavedPasteState(deliverySuppressed: true,
                                      requireAccessibilityUntrusted: false) {
            helper.canPasteOverrideForTesting = {
                probe.calls += 1
                return true
            }

            let result = await pasteIntoThisProcess(transcript)

            var failure: Error?
            if case .failed(let error) = result { failure = error }
            #expect(failure.map { ($0 as NSError).domain } == "AccessibilityHelper",
                    "expected the send-failed exit, got \(result)")
            #expect(probe.calls > 0, "the run never reached the focus check")
            #expect(helper.activeRestorationWorkItem != nil)
            #expect(NSPasteboard.general.string(forType: .string) == transcript)
            #expect(helper.keptClipboardSnapshotChangeCount == nil)
        }
    }

    // MARK: - #1591: a copy made inside the restore window survives the restore

    /// #1591: the user copies something after the transcript was written and
    /// before the delayed restore runs. That copy is now the user's clipboard:
    /// the restore must write nothing and drop the snapshot, so no later restore
    /// writes the stale clipboard back either. Before #1591 the restore wrote
    /// "clipboard before recording" over the copy.
    ///
    /// The suppressed exit arms the real restore (see the test above). The test
    /// runs the armed work item at once instead of waiting out the user's delay
    /// (`clipboardRestoreDelaySeconds` is `@AppStorage`, never written here).
    @Test(.restoreClipboardIsOn, .sentryIsOff)
    func userCopyInsideRestoreWindowSurvivesRestore() async throws {
        let helper = AccessibilityHelper.shared
        let userCopy = "copied by the user inside the restore window #1591"

        try await withSavedPasteState(deliverySuppressed: true,
                                      requireAccessibilityUntrusted: false) {
            let restore = try await armRestoreThroughSuppressedPaste("restored-over transcript #1591")

            NSPasteboard.general.clearContents()
            NSPasteboard.general.setString(userCopy, forType: .string)
            runArmedRestore(restore)

            #expect(NSPasteboard.general.string(forType: .string) == userCopy,
                    "the restore wrote over the user's own copy")
            #expect(helper.originalClipboardData == nil,
                    "the stale snapshot was kept for a later restore")
            #expect(helper.activeRestorationWorkItem == nil)
            #expect(helper.restoreExpectedChangeCount == nil)
        }
    }

    /// #1591 keeps the restore itself: with no write after the transcript, the
    /// restore still writes the record-start clipboard back.
    @Test(.restoreClipboardIsOn, .sentryIsOff)
    func restoreWithoutUserCopyStillWritesOriginalClipboardBack() async throws {
        let helper = AccessibilityHelper.shared

        try await withSavedPasteState(deliverySuppressed: true,
                                      requireAccessibilityUntrusted: false) {
            let restore = try await armRestoreThroughSuppressedPaste("transcript before the restore #1591")

            runArmedRestore(restore)

            #expect(NSPasteboard.general.string(forType: .string) == "clipboard before recording")
            #expect(helper.activeRestorationWorkItem == nil)
            #expect(helper.restoreExpectedChangeCount == nil)
        }
    }

    /// #1591: the streaming paste writes its text and puts the clipboard back
    /// inside the window. That round trip is the app's own, so it hands the
    /// restore the new count and the restore still runs. A user copy before the
    /// round trip is not handed over: the restore keeps skipping.
    @Test(.restoreClipboardIsOn, .sentryIsOff)
    func streamingRoundTripKeepsRestoreButUserCopyBeforeItDoesNot() async throws {
        let helper = AccessibilityHelper.shared
        let pasteboard = NSPasteboard.general

        try await withSavedPasteState(deliverySuppressed: true,
                                      requireAccessibilityUntrusted: false) {
            let transcript = "transcript before a streaming paste #1591"
            let restore = try await armRestoreThroughSuppressedPaste(transcript)

            // The streaming paste's round trip, as `TextInputService` makes it.
            func streamingRoundTrip() {
                let before = pasteboard.changeCount
                let held = pasteboard.string(forType: .string) ?? ""
                pasteboard.clearContents()
                pasteboard.setString("streamed segment #1591", forType: .string)
                pasteboard.clearContents()
                pasteboard.setString(held, forType: .string)
                helper.clipboardRoundTripRestored(from: before, to: pasteboard.changeCount)
            }

            streamingRoundTrip()
            try #require(pasteboard.string(forType: .string) == transcript)
            #expect(helper.restoreExpectedChangeCount == pasteboard.changeCount,
                    "the app's own round trip was not handed to the restore")

            let userCopy = "copied by the user before a streaming paste #1591"
            pasteboard.clearContents()
            pasteboard.setString(userCopy, forType: .string)
            streamingRoundTrip()
            runArmedRestore(restore)

            #expect(pasteboard.string(forType: .string) == userCopy)
            #expect(helper.originalClipboardData == nil)
        }
    }

    /// #1591 meets #1061: a no-paste exit inside the window leaves an unpasted
    /// transcript of the app's own on the clipboard. The restore must not write
    /// over it, and the snapshot stays, so the next recording keeps the user's
    /// older clipboard as #1061 requires.
    @Test(.restoreClipboardIsOn, .sentryIsOff)
    func unpastedTranscriptInsideRestoreWindowKeepsSnapshot() async throws {
        let helper = AccessibilityHelper.shared
        let unpasted = "unpasted transcript inside the restore window #1591"

        try await withSavedPasteState(deliverySuppressed: true,
                                      requireAccessibilityUntrusted: false) {
            let restore = try await armRestoreThroughSuppressedPaste("pasted transcript #1591")

            helper.copyToClipboard(unpasted)
            helper.keepClipboardSnapshotForNextRecording(transcriptChangeCount: NSPasteboard.general.changeCount,
                                                         settings: SettingsManager.shared)
            runArmedRestore(restore)

            #expect(NSPasteboard.general.string(forType: .string) == unpasted)
            #expect(savedSnapshotText() == "clipboard before recording",
                    "the #1061 snapshot was dropped")
            #expect(helper.activeRestorationWorkItem == nil)
        }
    }

    /// Drives the suppressed send-paste exit, which arms the real restore, and
    /// returns the armed work item. Call inside `withSavedPasteState` with the
    /// gate suppressed; no keystroke is sent on any Mac.
    private func armRestoreThroughSuppressedPaste(_ transcript: String) async throws -> DispatchWorkItem {
        let helper = AccessibilityHelper.shared
        helper.canPasteOverrideForTesting = { true }

        let result = await pasteIntoThisProcess(transcript)

        var failure: Error?
        if case .failed(let error) = result { failure = error }
        try #require(failure.map { ($0 as NSError).domain } == "AccessibilityHelper",
                     "expected the suppressed send-paste exit, got \(result)")
        try #require(NSPasteboard.general.string(forType: .string) == transcript)
        try #require(helper.restoreExpectedChangeCount == NSPasteboard.general.changeCount,
                     "the restore was not armed with the transcript's change count")
        return try #require(helper.activeRestorationWorkItem)
    }

    /// Runs the armed restore now, as its timer would, then cancels it so the
    /// timer that is still scheduled does nothing.
    private func runArmedRestore(_ restore: DispatchWorkItem) {
        restore.perform()
        restore.cancel()
    }

    // MARK: - #1061: the next recording keeps the user's older clipboard

    /// #1061 (Ray's option B): after a no-paste exit leaves the transcript on the
    /// clipboard, the next `startRecordingSession()` must keep the snapshot of
    /// the clipboard from before the first recording, so the restore after the
    /// next paste writes the user's clipboard back, not the transcript. Before
    /// #1061 no restore was pending here, so the call re-snapshotted the
    /// transcript. Drives the no-focused-field exit, as the #1034 test above does.
    @Test(.restoreClipboardIsOn, .sentryIsOff)
    func nextRecordingAfterNoPasteExitKeepsOlderClipboardSnapshot() async throws {
        let helper = AccessibilityHelper.shared
        let transcript = "unpasted transcript #1061"

        try await withSavedPasteState(deliverySuppressed: false,
                                      requireAccessibilityUntrusted: false) {
            helper.canPasteOverrideForTesting = { false }
            let result = await pasteIntoThisProcess(transcript)
            let noFocusedField: Bool
            if case .noFocusedField = result { noFocusedField = true } else { noFocusedField = false }
            try #require(noFocusedField, "expected .noFocusedField, got \(result)")
            try #require(NSPasteboard.general.string(forType: .string) == transcript)

            await helper.startRecordingSession()

            #expect(savedSnapshotText() == "clipboard before recording")
            #expect(helper.keptClipboardSnapshotChangeCount == nil, "the mark is single use")
        }
    }

    /// #1061: when the user copies something between the no-paste exit and the
    /// next recording, that copy is theirs. The next `startRecordingSession()`
    /// takes a fresh snapshot of it, so the later restore never overwrites it.
    @Test(.restoreClipboardIsOn, .sentryIsOff)
    func nextRecordingAfterUserCopyTakesFreshClipboardSnapshot() async throws {
        let helper = AccessibilityHelper.shared
        let userCopy = "copied by the user between dictations #1061"

        try await withSavedPasteState(deliverySuppressed: false,
                                      requireAccessibilityUntrusted: false) {
            helper.canPasteOverrideForTesting = { false }
            _ = await pasteIntoThisProcess("unpasted transcript #1061")
            try #require(helper.keptClipboardSnapshotChangeCount == NSPasteboard.general.changeCount,
                         "the no-paste exit did not record the transcript's change count")

            NSPasteboard.general.clearContents()
            NSPasteboard.general.setString(userCopy, forType: .string)
            await helper.startRecordingSession()

            #expect(savedSnapshotText() == userCopy)
            #expect(helper.keptClipboardSnapshotChangeCount == nil)
        }
    }

    /// #1061: turning restore off drops the kept snapshot (SettingsManager's
    /// `didSet` calls `dropKeptClipboardSnapshot()`; the setting is
    /// `@AppStorage` and is never written from a test). The next recording
    /// then snapshots the clipboard as it is.
    @Test(.restoreClipboardIsOn, .sentryIsOff)
    func droppedKeptSnapshotLetsNextRecordingSnapshotAfresh() async throws {
        let helper = AccessibilityHelper.shared
        let transcript = "unpasted transcript, restore turned off #1061"

        try await withSavedPasteState(deliverySuppressed: false,
                                      requireAccessibilityUntrusted: false) {
            helper.canPasteOverrideForTesting = { false }
            _ = await pasteIntoThisProcess(transcript)
            try #require(helper.keptClipboardSnapshotChangeCount != nil)

            helper.dropKeptClipboardSnapshot()
            #expect(helper.keptClipboardSnapshotChangeCount == nil)
            await helper.startRecordingSession()

            #expect(savedSnapshotText() == transcript)
        }
    }

    // MARK: - #879: the guards around the record-start read

    /// #879 generation guard: a record-start read that returns after a newer
    /// `startRecordingSession()` began must not write the session state. The
    /// newer read returns first here, so without the guard the older, stale
    /// read lands last and overwrites it. Nothing writes the pasteboard during
    /// the run, so the changeCount guard passes and cannot hide this one.
    @Test func staleRecordingStartReadIsDroppedAfterANewerStart() async throws {
        let helper = AccessibilityHelper.shared

        try await withHeldRecordingStartReads { reads in
            let older = Task { await helper.startRecordingSession() }
            try await reads.waitUntilStarted(1)
            let newer = Task { await helper.startRecordingSession() }
            try await reads.waitUntilStarted(2)

            reads.release(1, with: clipboardSnapshot("newer read #879"))
            await newer.value
            try #require(savedSnapshotText() == "newer read #879")

            reads.release(0, with: clipboardSnapshot("stale read #879"))
            await older.value
            #expect(savedSnapshotText() == "newer read #879",
                    "the stale read overwrote the newer session's snapshot")
            #expect(helper.isInRecordingSession)
        }
    }

    /// #879 changeCount guard: a pasteboard write while the read is in flight
    /// (a paste, or the user's own copy) means the read no longer shows the
    /// clipboard from before the recording, so nothing is saved. One start only,
    /// so the generation guard passes and cannot hide this one.
    @Test func recordingStartReadIsDroppedWhenTheClipboardChangedDuringIt() async throws {
        let helper = AccessibilityHelper.shared

        try await withHeldRecordingStartReads { reads in
            let start = Task { await helper.startRecordingSession() }
            try await reads.waitUntilStarted(1)

            NSPasteboard.general.clearContents()
            NSPasteboard.general.setString("copied while the read was held #879", forType: .string)
            reads.release(0, with: clipboardSnapshot("read from before the copy #879"))
            await start.value

            #expect(helper.originalClipboardData == nil,
                    "a read that overlapped a clipboard write was saved")
            #expect(helper.isInRecordingSession)
        }
    }

    /// #879 reset before the await: the session opens and the last session's
    /// snapshot goes BEFORE the read awaits. While the read is held, a paste must
    /// find no older snapshot to restore, and an `endRecordingSession()` in that
    /// window must not be undone when the read returns.
    @Test func recordingStartDropsOlderSnapshotAndOpensSessionBeforeTheRead() async throws {
        let helper = AccessibilityHelper.shared

        try await withHeldRecordingStartReads { reads in
            // An older session's snapshot, with no restore pending and no kept
            // mark, so the call takes the read path, not a keep path.
            helper.originalClipboardData = clipboardSnapshot("older session's clipboard #879")

            let start = Task { await helper.startRecordingSession() }
            try await reads.waitUntilStarted(1)

            #expect(helper.originalClipboardData == nil,
                    "the older snapshot was still there while the read was in flight")
            #expect(helper.isInRecordingSession,
                    "the session was not open while the read was in flight")

            helper.endRecordingSession()
            reads.release(0, with: clipboardSnapshot("fresh read #879"))
            await start.value

            #expect(helper.isInRecordingSession == false,
                    "the returning read reopened a session that had ended")
        }
    }

    /// One saved-clipboard snapshot holding `text`.
    private func clipboardSnapshot(_ text: String) -> [AccessibilityHelper.ClipboardItemData] {
        [AccessibilityHelper.ClipboardItemData(types: [.string], data: [.string: Data(text.utf8)])]
    }

    /// Saves the tester's clipboard and the record-start state, starts from no
    /// snapshot, no session, no kept mark and no pending restore, routes every
    /// record-start read through `HeldSnapshotReads`, runs `body`, and puts
    /// everything back. Reads still held when `body` throws are released with
    /// nil, so no task is left suspended.
    private func withHeldRecordingStartReads(_ body: (HeldSnapshotReads) async throws -> Void) async throws {
        let helper = AccessibilityHelper.shared
        let pasteboard = NSPasteboard.general

        let savedClipboard: [NSPasteboardItem] = (pasteboard.pasteboardItems ?? []).map { item in
            let copy = NSPasteboardItem()
            for type in item.types {
                if let data = item.data(forType: type) { copy.setData(data, forType: type) }
            }
            return copy
        }
        let savedOriginal = helper.originalClipboardData
        let savedInSession = helper.isInRecordingSession
        let savedKeptChangeCount = helper.keptClipboardSnapshotChangeCount
        let reads = HeldSnapshotReads()
        defer {
            reads.releaseAll()
            helper.recordingStartSnapshotOverrideForTesting = nil
            helper.cancelPendingClipboardRestoration()
            helper.originalClipboardData = savedOriginal
            helper.isInRecordingSession = savedInSession
            helper.keptClipboardSnapshotChangeCount = savedKeptChangeCount
            pasteboard.clearContents()
            if !savedClipboard.isEmpty { pasteboard.writeObjects(savedClipboard) }
        }

        helper.cancelPendingClipboardRestoration()
        helper.originalClipboardData = nil
        helper.isInRecordingSession = false
        helper.keptClipboardSnapshotChangeCount = nil
        helper.recordingStartSnapshotOverrideForTesting = { await reads.read() }

        try await body(reads)
    }

    /// The plain text of the first item in the saved record-start snapshot.
    private func savedSnapshotText() -> String? {
        guard let data = AccessibilityHelper.shared.originalClipboardData?.first?.data[.string] else { return nil }
        return String(decoding: data, as: UTF8.self)
    }

    /// Runs `executePasteAsync` against this process under its own bundle ID, so
    /// `resolveCapturedTarget` accepts it and the #783 refusal is not taken.
    private func pasteIntoThisProcess(_ text: String) async -> AccessibilityHelper.SmartPasteResult {
        await AccessibilityHelper.shared.executePasteAsync(
            text,
            previousAppPID: ProcessInfo.processInfo.processIdentifier,
            previousAppBundleID: NSRunningApplication.current.bundleIdentifier,
            settings: SettingsManager.shared
        )
    }

    /// Saves the tester's clipboard and the shared state these paste tests change,
    /// seeds the record-start clipboard a restoration would write back, runs
    /// `body`, and puts everything back, disarming any restoration it armed.
    /// The one owner of the save/restore contract for every executePasteAsync
    /// test here. Pass `requireAccessibilityUntrusted: true` only for a test that
    /// reaches `sendPasteCommand()` with the gate open and the focus seam true,
    /// where a Mac that grants Accessibility would post a real Cmd+V.
    private func withSavedPasteState(deliverySuppressed: Bool,
                                     requireAccessibilityUntrusted: Bool,
                                     _ body: () async throws -> Void) async throws {
        let helper = AccessibilityHelper.shared
        let pasteboard = NSPasteboard.general

        // The traits checked these before the test began. Check again, so a
        // state change in between fails the run instead of sending an event or
        // passing vacuously.
        try #require(SettingsManager.shared.restoreClipboardAfterPaste)
        try #require(SentryService.isReportingEnabled == false)
        if requireAccessibilityUntrusted {
            try #require(AXIsProcessTrusted() == false)
        }

        let savedClipboard: [NSPasteboardItem] = (pasteboard.pasteboardItems ?? []).map { item in
            let copy = NSPasteboardItem()
            for type in item.types {
                if let data = item.data(forType: type) { copy.setData(data, forType: type) }
            }
            return copy
        }
        let savedOriginal = helper.originalClipboardData
        let savedInSession = helper.isInRecordingSession
        let savedKeptChangeCount = helper.keptClipboardSnapshotChangeCount
        let savedSuppressed = TextDeliveryGate.isSuppressed
        let savedReportedMissingPermission = helper.hasReportedMissingPastePermission
        defer {
            helper.pastePermissionOverrideForTesting = nil
            helper.canPasteOverrideForTesting = nil
            helper.cancelPendingClipboardRestoration()
            helper.currentPasteTask = nil
            helper.originalClipboardData = savedOriginal
            helper.isInRecordingSession = savedInSession
            helper.keptClipboardSnapshotChangeCount = savedKeptChangeCount
            TextDeliveryGate.setSuppressed(savedSuppressed)
            helper.hasReportedMissingPastePermission = savedReportedMissingPermission
            pasteboard.clearContents()
            if !savedClipboard.isEmpty { pasteboard.writeObjects(savedClipboard) }
        }

        // The clipboard record-start captured: what a restoration would write back.
        helper.originalClipboardData = [
            AccessibilityHelper.ClipboardItemData(
                types: [.string],
                data: [.string: Data("clipboard before recording".utf8)]
            )
        ]
        helper.keptClipboardSnapshotChangeCount = nil
        helper.pastePermissionOverrideForTesting = true
        TextDeliveryGate.setSuppressed(deliverySuppressed)

        try await body()
    }
    #endif
}

#if DEBUG
/// The skip conditions the executePasteAsync tests share, one wording per reason.
/// Each reads live state (never writes it) and SKIPS the test, never fails it.
fileprivate extension Trait where Self == ConditionTrait {
    /// With restore off no exit arms a restoration, so the run cannot tell an
    /// exit that arms one from an exit that does not.
    static var restoreClipboardIsOn: Self {
        .enabled("restoreClipboardAfterPaste is off on this Mac, so the run cannot tell whether an exit arms a restore", {
            await MainActor.run { SettingsManager.shared.restoreClipboardAfterPaste }
        })
    }

    /// A paste outcome can be a reportable event, and a test must never send one.
    static var sentryIsOff: Self {
        .enabled("Sentry reporting is live on this Mac; the paste outcome would send a real Sentry event", {
            await MainActor.run { SentryService.isReportingEnabled == false }
        })
    }

    /// With Accessibility granted, a test that reaches `sendPasteCommand()` with
    /// the gate open and `canPasteOverrideForTesting` true would post a real Cmd+V.
    static var accessibilityIsNotTrusted: Self {
        .enabled("Accessibility is granted on this Mac; the run would post a real Cmd+V", {
            AXIsProcessTrusted() == false
        })
    }
}

/// Counts calls to the `canPasteOverrideForTesting` seam. The #783 refusal also
/// returns `.noFocusedField`, so a non-zero count is what proves a #1034 test
/// got past the target guard to the exit it means to drive.
@MainActor
private final class CanPasteProbe {
    var calls = 0
}

/// Stands in for the record-start clipboard read (#879). Each read suspends
/// until the test releases it by its start index, so a test can act while a
/// read is in flight and choose the order the reads return in.
@MainActor
private final class HeldSnapshotReads {
    typealias Snapshot = [AccessibilityHelper.ClipboardItemData]?

    private(set) var started = 0
    private var held: [Int: CheckedContinuation<Snapshot, Never>] = [:]

    func read() async -> Snapshot {
        let index = started
        started += 1
        return await withCheckedContinuation { continuation in
            held[index] = continuation
        }
    }

    /// Lets the main actor run until `count` reads have begun. Bounded, so a
    /// start that never reaches its read fails the test instead of hanging it.
    func waitUntilStarted(_ count: Int) async throws {
        var spins = 0
        while started < count && spins < 10_000 {
            spins += 1
            await Task.yield()
        }
        try #require(started >= count, "only \(started) of \(count) record-start reads began")
    }

    func release(_ index: Int, with snapshot: Snapshot) {
        held.removeValue(forKey: index)?.resume(returning: snapshot)
    }

    func releaseAll() {
        for continuation in held.values { continuation.resume(returning: nil) }
        held.removeAll()
    }
}
#endif
