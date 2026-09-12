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
}
