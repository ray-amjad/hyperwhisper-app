//
//  AccessibilityHelper+PasteDiagnostics.swift
//  hyperwhisper
//
//  Production visibility for the last step of the record → transcribe → paste
//  flow.
//
//  Auto-paste had no error reporting at all: every way it can fail returned a
//  coarse `SmartPasteResult` and wrote one os.log line on the user's own Mac.
//  A transcript that never reached the target app was therefore invisible in
//  production — Sentry held zero issues for the paste step while holding
//  hundreds for the transcription step in front of it.
//
//  PRIVACY: nothing here touches the transcript. The reported metadata is the
//  target bundle identifier, a character count, timings and a fixed outcome
//  slug.
//

import AppKit
import ApplicationServices
import Foundation
import os

extension AccessibilityHelper {

    // MARK: - Outcomes

    /// Why one auto-paste attempt ended the way it did.
    ///
    /// The raw values are stable, low-cardinality slugs. They become the Sentry
    /// message and a `paste_outcome` tag, so a query can count each failure mode
    /// over a release without a text search.
    enum PasteOutcome: String {
        /// ⌘V was posted into the intended app.
        case success
        /// Accessibility permission is not granted, so no paste is possible.
        case noAccessibilityPermission = "no_accessibility_permission"
        /// The app captured at record-start quit, or its PID was reused by a
        /// different app. The confidentiality guard refuses to paste.
        case targetLost = "target_lost"
        /// No target was captured and an unrelated app is frontmost, so the
        /// confidentiality guard refuses to paste.
        case targetUnknown = "target_unknown"
        /// A password field is focused. A deliberate refusal, not a fault.
        case secureField = "secure_field"
        /// No editable element is focused. The text stays on the clipboard.
        case noFocusedField = "no_focused_field"
        /// A newer paste superseded this one.
        case cancelled
        /// `TextDeliveryGate` blocks delivery (the onboarding sheet owns the
        /// result). A deliberate refusal, not a fault.
        case suppressed
        /// Permission and focus were both present, and posting the keystroke
        /// still failed. Always a defect.
        case commandFailed = "command_failed"

        /// True when the transcript did not reach the target app AND the cause is
        /// a defect or a broken environment, rather than a deliberate refusal.
        ///
        /// `noFocusedField` and `secureField` stay out on purpose: both are
        /// normal (the user clicked away, or is typing a password), both leave
        /// the text on the clipboard, and both are frequent enough to flood the
        /// issue stream. They are still counted through the scope extras below.
        var isReportable: Bool {
            switch self {
            case .commandFailed, .targetLost, .targetUnknown, .noAccessibilityPermission:
                return true
            case .success, .secureField, .noFocusedField, .cancelled, .suppressed:
                return false
            }
        }

        /// True when the outcome describes a defect in the app rather than the
        /// user's environment. Drives the Sentry level: a failed keystroke is an
        /// error, a lost target or a missing permission is a warning.
        var isDefect: Bool {
            self == .commandFailed
        }
    }

    // MARK: - Attempt Metadata

    /// Metadata for one auto-paste attempt.
    ///
    /// PRIVACY: this struct must never hold the transcript. `characterCount` is
    /// a count, not the text.
    struct PasteAttempt {
        /// Bundle identifier of the app the paste targets, when it is known.
        var targetBundleID: String?
        /// True when record-start captured a distinct target app.
        var hadCapturedTarget: Bool = false
        /// True when the target is a remote-desktop client, which needs the
        /// longer clipboard-forwarding delay.
        var isRemoteDesktop: Bool = false
        /// True when the Electron/Slack focus retry ran.
        var usedFocusRetry: Bool = false
        /// True when that retry made focus available.
        var focusRetrySucceeded: Bool = false
        /// Length of the text to deliver. A count only — never the text.
        var characterCount: Int = 0
        /// When the attempt started, used for the elapsed time.
        var startedAt: Date = Date()

        var elapsedMs: Int {
            Int(Date().timeIntervalSince(startedAt) * 1000)
        }
    }

    // MARK: - Reporting

    /// Record the end of one auto-paste attempt.
    ///
    /// Every attempt writes one os.log line. A reportable failure also sends one
    /// Sentry event, and every attempt refreshes the scope extras so a later
    /// error in the same session carries the paste context with it.
    ///
    /// This function only logs. It returns nothing, it throws nothing, and it
    /// must never change what the caller returns.
    func reportPasteOutcome(_ outcome: PasteOutcome, attempt: PasteAttempt) {
        let target = attempt.targetBundleID ?? "unknown"
        let elapsedMs = attempt.elapsedMs

        // Build the summary first: os.log privacy interpolation does not accept
        // nested expressions, so one plain String keeps the log call simple.
        let summary = """
            outcome=\(outcome.rawValue) target=\(target) \
            capturedTarget=\(attempt.hadCapturedTarget) \
            remoteDesktop=\(attempt.isRemoteDesktop) \
            focusRetry=\(attempt.usedFocusRetry) \
            focusRetrySucceeded=\(attempt.focusRetrySucceeded) \
            chars=\(attempt.characterCount) elapsedMs=\(elapsedMs)
            """

        if outcome == .success {
            AppLogger.accessibility.info("📤 Auto-paste delivered · \(summary, privacy: .public)")
        } else if outcome.isReportable {
            AppLogger.accessibility.error("📤 Auto-paste failed · \(summary, privacy: .public)")
        } else {
            AppLogger.accessibility.info("📤 Auto-paste not delivered · \(summary, privacy: .public)")
        }

        // Respect the user's opt-in before anything reaches Sentry.
        guard AppLogger.isErrorLoggingEnabled else { return }

        let extras: [String: Any] = [
            "paste_outcome": outcome.rawValue,
            "paste_target_bundle_id": target,
            "paste_had_captured_target": attempt.hadCapturedTarget,
            "paste_is_remote_desktop": attempt.isRemoteDesktop,
            "paste_used_focus_retry": attempt.usedFocusRetry,
            "paste_focus_retry_succeeded": attempt.focusRetrySucceeded,
            "paste_character_count": attempt.characterCount,
            "paste_elapsed_ms": elapsedMs,
            "paste_has_accessibility_permission": AXIsProcessTrusted(),
            "paste_delivery_suppressed": TextDeliveryGate.isSuppressed,
        ]

        // Scope extras survive `beforeSend`, which strips breadcrumbs. Any error
        // captured later in this session now says how the last paste ended.
        SentryService.setExtras(extras)

        guard outcome.isReportable else { return }

        // A missing accessibility permission repeats on every single dictation
        // until the user grants it. Report it once per app run so one unhappy
        // setup does not become hundreds of events.
        if outcome == .noAccessibilityPermission {
            guard !hasReportedMissingPastePermission else { return }
            hasReportedMissingPastePermission = true
        }

        // `includeRecentLogs` is off on purpose. It shells out to `log show` and
        // waits for it, and this runs on the main actor at the end of every
        // dictation — the same main thread that already reports app hangs
        // (HYPERWHISPER-F7). The extras above carry the reproduction context, so
        // the log dump is not worth a synchronous subprocess here.
        SentryService.captureMessage(
            "Auto-paste failed: \(outcome.rawValue)",
            level: outcome.isDefect ? .error : .warning,
            extras: extras,
            tags: [
                "component": "auto_paste",
                "paste_outcome": outcome.rawValue,
                "paste_target_bundle_id": target,
            ],
            includeRecentLogs: false
        )
    }
}
