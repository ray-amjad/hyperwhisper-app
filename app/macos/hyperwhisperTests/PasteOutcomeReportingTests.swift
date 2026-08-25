//
//  PasteOutcomeReportingTests.swift
//  hyperwhisperTests
//
//  Guards the two properties the auto-paste diagnostics depend on:
//  the outcome slugs are stable (Sentry queries and dashboards key off them),
//  and only genuine failures raise an event.
//

import Foundation
import Testing
@testable import HyperWhisper

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
}
