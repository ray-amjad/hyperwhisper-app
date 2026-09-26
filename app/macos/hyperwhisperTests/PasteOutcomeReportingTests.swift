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
    /// keystroke is sent. Traits as in the #783 test, plus
    /// `.accessibilityIsNotTrusted`: on a Mac that grants Accessibility the run
    /// would read real focus, and the send-failed test could post a real Cmd+V.
    #if DEBUG
    @Test(.restoreClipboardIsOn, .sentryIsOff, .accessibilityIsNotTrusted)
    func noFocusedFieldKeepsTranscriptOnClipboardWithoutArmingRestore() async throws {
        let helper = AccessibilityHelper.shared
        let probe = CanPasteProbe()
        let transcript = "no focused field transcript #1034"

        try await withSavedPasteState(deliverySuppressed: false) {
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
        }
    }

    /// #1034: a paste cancelled just before the keystroke (a newer paste
    /// superseded it) pasted nothing, so it must not arm a restoration either.
    /// The focus seam cancels the in-flight paste task and reports a field, so
    /// the run reaches the `Task.isCancelled` check right before the paste.
    @Test(.restoreClipboardIsOn, .sentryIsOff, .accessibilityIsNotTrusted)
    func cancelledBeforePasteKeepsTranscriptOnClipboardWithoutArmingRestore() async throws {
        let helper = AccessibilityHelper.shared
        let probe = CanPasteProbe()
        let transcript = "cancelled transcript #1034"

        try await withSavedPasteState(deliverySuppressed: false) {
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
        }
    }

    /// #1034: when `sendPasteCommand()` fails, nothing was pasted, so the exit
    /// must not arm a restoration. The focus seam reports a field and this Mac
    /// does not grant Accessibility, so `sendPasteCommand()` returns false at
    /// its permission check and the exit classifies `.noAccessibilityPermission`.
    /// A real `.commandFailed` needs a CGEvent that cannot be built, which a test
    /// cannot arrange; this drives the same exit and the same decision.
    @Test(.restoreClipboardIsOn, .sentryIsOff, .accessibilityIsNotTrusted)
    func sendPasteWithoutPermissionKeepsTranscriptOnClipboardWithoutArmingRestore() async throws {
        let helper = AccessibilityHelper.shared
        let probe = CanPasteProbe()
        let transcript = "send failed transcript #1034"

        try await withSavedPasteState(deliverySuppressed: false) {
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
        }
    }

    /// #1034 keeps one restore on the send-failed exit: the onboarding gate
    /// (`TextDeliveryGate`) withholds the text on purpose, like a secure field
    /// (#783), so the exit classifies `.suppressed` and still arms it. Pins that
    /// the fix narrowed the restore rather than removing it.
    @Test(.restoreClipboardIsOn, .sentryIsOff, .accessibilityIsNotTrusted)
    func suppressedSendPasteStillArmsRestore() async throws {
        let helper = AccessibilityHelper.shared
        let probe = CanPasteProbe()
        let transcript = "suppressed transcript #1034"

        try await withSavedPasteState(deliverySuppressed: true) {
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
        }
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
    /// test here. `requireAccessibilityUntrusted` is false only for the #783
    /// test, whose refusal returns before any focus read or keystroke.
    private func withSavedPasteState(deliverySuppressed: Bool,
                                     requireAccessibilityUntrusted: Bool = true,
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
        let savedSuppressed = TextDeliveryGate.isSuppressed
        let savedReportedMissingPermission = helper.hasReportedMissingPastePermission
        defer {
            helper.pastePermissionOverrideForTesting = nil
            helper.canPasteOverrideForTesting = nil
            helper.cancelPendingClipboardRestoration()
            helper.currentPasteTask = nil
            helper.originalClipboardData = savedOriginal
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

    /// With Accessibility granted the run would read real focus, and a test that
    /// sets `canPasteOverrideForTesting` to true would post a real Cmd+V.
    static var accessibilityIsNotTrusted: Self {
        .enabled("Accessibility is granted on this Mac; the run would read real focus and could post a real Cmd+V", {
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
#endif
