//
//  AccessibilityHelper+SmartPaste.swift
//  hyperwhisper
//
//  Created by Assistant on 16/08/2025.
//

import Foundation
import AppKit
import os

extension AccessibilityHelper {

    // MARK: - Smart Paste Results

    /// Result of a smart paste operation
    enum SmartPasteResult {
        case success
        case noPermission
        case noFocusedField
        case secureField
        case failed(Error)
    }

    /// Perform smart paste operation with full flow
    /// - Parameters:
    ///   - text: Text to paste
    ///   - previousAppPID: PID of the app that was frontmost before recording
    ///   - previousAppBundleID: Bundle ID captured for that app at record-start,
    ///     used to defend against PID reuse (see `executePasteAsync`)
    ///   - settings: Optional settings manager for restoration preferences
    /// - Returns: SmartPasteResult indicating success or failure reason
    func performSmartPaste(_ text: String,
                           previousAppPID: pid_t?,
                           previousAppBundleID: String?,
                           settings: SettingsManager?) -> SmartPasteResult {
        // Check accessibility permission
        guard hasAccessibilityPermission() else {
            return .noPermission
        }

        // Cancel any pending restoration from previous recordings
        cancelPendingClipboardRestoration()

        // Reactivate the previous app and get its bundle ID
        var targetBundleID: String? = nil
        // Tracks whether we positively re-targeted the intended app (or hid our own
        // app so the previous app comes forward). If neither happens, the frontmost
        // app is an unknown third party and pasting into it would leak the transcript.
        var targetHandled = false
        // Resolve the captured target PID (if any) to a running app exactly once, then
        // validate it against the bundle ID we captured at record-start. If the original
        // target quit and macOS reused its PID for a DIFFERENT app, the lookup succeeds
        // but resolves to the wrong process — validating the bundle ID closes that
        // PID-reuse leak. A mismatch is treated exactly like a lost target.
        let capturedApp = resolveCapturedTarget(pid: previousAppPID,
                                                expectedBundleID: previousAppBundleID)
        // True when we captured a distinct target PID but it is no longer running
        // (the app quit mid-recording) OR the resolved app failed bundle-ID validation
        // (PID reuse). In that case we never know which app is frontmost now, so we
        // must refuse to paste rather than risk leaking the transcript into an
        // unrelated app — including our own, since hiding it would just reveal some
        // other third-party app.
        let capturedTargetLost = previousAppPID != nil && capturedApp == nil
        if let app = capturedApp {
            targetBundleID = app.bundleIdentifier
            logger.info("🔄 Reactivating previous app: \(targetBundleID ?? "unknown", privacy: .public)")
            app.activate(options: [.activateIgnoringOtherApps])
            targetHandled = true

            // Tuned delays: browsers > electron > others
            let isBrowser = isBrowserBundleId(targetBundleID ?? "")
            let isElectron = targetBundleID.map { isElectronCodeEditor($0) || $0 == "com.tinyspeck.slackmacgap" } ?? false
            let delay: TimeInterval = isBrowser ? 0.25 : (isElectron ? 0.18 : 0.1)
            Thread.sleep(forTimeInterval: delay)
        } else if previousAppPID == nil,
                  let front = NSWorkspace.shared.frontmostApplication,
                  front.bundleIdentifier == Bundle.main.bundleIdentifier {
            // We never captured a distinct target (e.g. HyperWhisper itself was
            // frontmost when recording started). Hiding our own app brings the
            // app behind it forward as the intended paste target.
            NSApp.hide(nil)
            targetHandled = true
            Thread.sleep(forTimeInterval: 0.12)
        }
        // NOTE: when previousAppPID is non-nil but NSRunningApplication lookup
        // failed above, the captured target quit mid-recording. We intentionally
        // leave targetHandled == false so the confidentiality guard below refuses
        // to paste into whatever third-party app is now frontmost — even if that
        // happens to be HyperWhisper (hiding it would reveal an unrelated app).

        // Confidentiality guard: refuse to auto-paste when we cannot be confident
        // the frontmost app is the intended target.
        //  - capturedTargetLost: the captured app quit mid-recording, so any app
        //    that is frontmost now (third party OR our own) is not the target.
        //  - !targetHandled && frontmost is not us: we never re-targeted and some
        //    unknown third-party app is frontmost.
        // In either case, pasting would leak the transcript into the wrong app, so
        // refuse and leave the text on the clipboard.
        if capturedTargetLost ||
           (!targetHandled &&
            NSWorkspace.shared.frontmostApplication?.bundleIdentifier != Bundle.main.bundleIdentifier) {
            logger.warning("⚠️ Captured paste target is gone or a different app is frontmost — refusing auto-paste. Text left on clipboard.")
            copyToClipboard(text)
            scheduleClipboardRestoration(settings: settings)
            return .noFocusedField
        }

        // Detect remote desktop target
        let isRemoteDesktop = targetBundleID.map { isRemoteDesktopBundleId($0) } ?? false

        // Skip ConcealedType for remote desktop apps — their clipboard forwarding may ignore concealed items
        copyToClipboard(text, skipConcealedType: isRemoteDesktop)

        // Check for secure field
        if isSecureFieldFocused() {
            logger.info("🔒 Secure field focused. Skipping auto-paste for safety.")
            scheduleClipboardRestoration(settings: settings)
            return .secureField
        }

        // Check if we can paste, with a short retry for Electron/Slack which update focus slowly
        if !canPasteIntoFocusedElement() {
            if let bid = targetBundleID, (isElectronCodeEditor(bid) || bid == "com.tinyspeck.slackmacgap") {
                Thread.sleep(forTimeInterval: 0.12)
                if canPasteIntoFocusedElement() {
                    logger.info("✅ Focus became ready after short retry")
                } else {
                    logger.info("ℹ️ Focus not ready after retry")
                }
            }
        }

        if !canPasteIntoFocusedElement() {
            logger.info("ℹ️ No paste target focused. Text on clipboard, scheduling restoration if enabled.")
            scheduleClipboardRestoration(settings: settings)
            return .noFocusedField
        }

        // Extra delay for remote desktop apps — give clipboard forwarding time to register the change
        if isRemoteDesktop {
            Thread.sleep(forTimeInterval: 0.15) // 150ms
        }

        // Try to paste
        let pasteSucceeded = sendPasteCommand()
        if !pasteSucceeded {
            return .failed(NSError(domain: "AccessibilityHelper",
                                   code: -1,
                                   userInfo: [NSLocalizedDescriptionKey: "accessibility.error.paste.commandFailed".localized]))
        }

        // Skip clipboard restoration for remote desktop apps — the remote session may need the clipboard
        // content available for subsequent manual pastes
        scheduleClipboardRestoration(settings: isRemoteDesktop ? nil : settings)

        logger.info("✅ Successfully auto-pasted into focused input")
        return .success
    }

    // MARK: - Async Smart Paste Methods (Non-blocking)

    /// Cancel any in-flight paste operation
    /// This should be called before starting a new paste to prevent overlapping keystrokes
    func cancelCurrentPasteTask() {
        currentPasteTask?.cancel()
        currentPasteTask = nil
        logger.debug("❌ Cancelled any in-flight paste operation")
    }

    /// Execute paste operation using accessibility APIs (async, non-blocking)
    ///
    /// **LOW-LEVEL IMPLEMENTATION:**
    /// This function performs the actual paste operations using macOS accessibility APIs.
    /// It should be called by higher-level wrapper functions (like handleAutoPaste) that
    /// interpret the result for UI decisions.
    ///
    /// - Parameters:
    ///   - text: Text to paste
    ///   - previousAppPID: PID of the app that was frontmost before recording
    ///   - previousAppBundleID: Bundle ID captured for that app at record-start.
    ///     macOS recycles PIDs, so if the captured target quits mid-recording its PID
    ///     can be reused by an unrelated app before the paste fires. Validating the
    ///     resolved `NSRunningApplication` against this bundle ID prevents the
    ///     transcript from leaking into that unrelated process.
    ///   - settings: Optional settings manager for restoration preferences
    /// - Returns: SmartPasteResult indicating success or failure reason
    ///
    /// **IMPROVEMENTS OVER SYNC VERSION:**
    /// - Non-blocking: Uses Task.sleep instead of Thread.sleep
    /// - Cancellable: Checks for cancellation between operations
    /// - Single-flight: Cancels any existing paste before starting
    /// - Always schedules clipboard restoration
    ///
    /// **RESPONSIBILITIES:**
    /// - Check accessibility permissions
    /// - Reactivate previous application
    /// - Detect app type (Electron, browser, etc.)
    /// - Copy text to clipboard
    /// - Send ⌘V command
    /// - Schedule clipboard restoration
    func executePasteAsync(_ text: String,
                           previousAppPID: pid_t?,
                           previousAppBundleID: String?,
                           settings: SettingsManager?) async -> SmartPasteResult {
        // Cancel any existing paste operation to prevent overlapping
        cancelCurrentPasteTask()

        // Create a new task for this paste operation
        let task = Task<SmartPasteResult, Never> { @MainActor in
            // Check accessibility permission
            guard hasAccessibilityPermission() else {
                return .noPermission
            }

            // Cancel any pending restoration from previous recordings
            cancelPendingClipboardRestoration()

            // Reactivate the previous app and get its bundle ID
            var targetBundleID: String? = nil
            // Tracks whether we positively re-targeted the intended app (or hid our own
            // app so the previous app comes forward). If neither happens, the frontmost
            // app is an unknown third party and pasting into it would leak the transcript.
            var targetHandled = false
            // Resolve the captured target PID (if any) to a running app exactly once,
            // then validate it against the bundle ID captured at record-start to guard
            // against PID reuse (see this method's doc comment).
            let capturedApp = self.resolveCapturedTarget(pid: previousAppPID,
                                                         expectedBundleID: previousAppBundleID)
            // True when we captured a distinct target PID but it is no longer running
            // (the app quit mid-recording) OR the resolved app failed bundle-ID
            // validation (PID reuse). In that case we never know which app is
            // frontmost now, so we must refuse to paste rather than risk leaking the
            // transcript into an unrelated app — including our own, since hiding it
            // would just reveal some other third-party app.
            let capturedTargetLost = previousAppPID != nil && capturedApp == nil
            if let app = capturedApp {
                targetBundleID = app.bundleIdentifier
                logger.info("🔄 Reactivating previous app: \(targetBundleID ?? "unknown", privacy: .public)")
                app.activate(options: [.activateIgnoringOtherApps])
                targetHandled = true

                // Non-blocking delay using Task.sleep
                let isBrowser = isBrowserBundleId(targetBundleID ?? "")
                let isElectron = targetBundleID.map { isElectronCodeEditor($0) || $0 == "com.tinyspeck.slackmacgap" } ?? false
                let delayMs: UInt64 = isBrowser ? 250 : (isElectron ? 180 : 100)

                // Check for cancellation before delay
                if Task.isCancelled { return .failed(CancellationError()) }

                try? await Task.sleep(nanoseconds: delayMs * 1_000_000)

                // Check for cancellation after delay
                if Task.isCancelled { return .failed(CancellationError()) }
            } else if previousAppPID == nil,
                      let front = NSWorkspace.shared.frontmostApplication,
                      front.bundleIdentifier == Bundle.main.bundleIdentifier {
                // We never captured a distinct target (e.g. HyperWhisper itself was
                // frontmost when recording started). Hiding our own app brings the
                // app behind it forward as the intended paste target.
                NSApp.hide(nil)
                targetHandled = true

                if Task.isCancelled { return .failed(CancellationError()) }
                try? await Task.sleep(nanoseconds: 120 * 1_000_000) // 120ms
                if Task.isCancelled { return .failed(CancellationError()) }
            }
            // NOTE: when previousAppPID is non-nil but NSRunningApplication lookup
            // failed above, the captured target quit mid-recording. We intentionally
            // leave targetHandled == false so the confidentiality guard below refuses
            // to paste into whatever third-party app is now frontmost — even if that
            // happens to be HyperWhisper (hiding it would reveal an unrelated app).

            // Confidentiality guard: refuse to auto-paste when we cannot be confident
            // the frontmost app is the intended target.
            //  - capturedTargetLost: the captured app quit mid-recording, so any app
            //    that is frontmost now (third party OR our own) is not the target.
            //  - !targetHandled && frontmost is not us: we never re-targeted and some
            //    unknown third-party app is frontmost.
            // In either case, pasting would leak the transcript into the wrong app, so
            // refuse and leave the text on the clipboard.
            if capturedTargetLost ||
               (!targetHandled &&
                NSWorkspace.shared.frontmostApplication?.bundleIdentifier != Bundle.main.bundleIdentifier) {
                logger.warning("⚠️ Captured paste target is gone or a different app is frontmost — refusing auto-paste. Text left on clipboard.")
                copyToClipboard(text)
                scheduleClipboardRestoration(settings: settings)
                return .noFocusedField
            }

            // Detect remote desktop target
            let isRemoteDesktop = targetBundleID.map { self.isRemoteDesktopBundleId($0) } ?? false

            // Skip ConcealedType for remote desktop apps — their clipboard forwarding may ignore concealed items
            copyToClipboard(text, skipConcealedType: isRemoteDesktop)

            // Check for secure field
            if isSecureFieldFocused() {
                logger.info("🔒 Secure field focused. Skipping auto-paste for safety.")
                scheduleClipboardRestoration(settings: settings)
                return .secureField
            }

            // Check if we can paste, with a short retry for Electron/Slack
            if !canPasteIntoFocusedElement() {
                if let bid = targetBundleID, (isElectronCodeEditor(bid) || bid == "com.tinyspeck.slackmacgap") {
                    if Task.isCancelled { return .failed(CancellationError()) }
                    try? await Task.sleep(nanoseconds: 120 * 1_000_000)
                    if Task.isCancelled { return .failed(CancellationError()) }

                    if canPasteIntoFocusedElement() {
                        logger.info("✅ Focus became ready after short retry")
                    } else {
                        logger.info("ℹ️ Focus not ready after retry")
                    }
                }
            }

            if !canPasteIntoFocusedElement() {
                logger.info("ℹ️ No paste target focused. Text on clipboard, scheduling restoration if enabled.")
                scheduleClipboardRestoration(settings: settings)
                return .noFocusedField
            }

            // Check cancellation before paste
            if Task.isCancelled {
                scheduleClipboardRestoration(settings: settings)
                return .failed(CancellationError())
            }

            // Extra delay for remote desktop apps — give clipboard forwarding time to register the change
            if isRemoteDesktop {
                try? await Task.sleep(nanoseconds: 150 * 1_000_000) // 150ms
            }

            // Try to paste
            let pasteSucceeded = sendPasteCommand()
            if !pasteSucceeded {
                // Always schedule restoration even on failure
                scheduleClipboardRestoration(settings: settings)
                return .failed(NSError(domain: "AccessibilityHelper",
                                       code: -1,
                                       userInfo: [NSLocalizedDescriptionKey: "accessibility.error.paste.commandFailed".localized]))
            }

            // Skip clipboard restoration for remote desktop apps — the remote session may need
            // the clipboard content available for subsequent manual pastes
            scheduleClipboardRestoration(settings: isRemoteDesktop ? nil : settings)

            logger.info("✅ Successfully auto-pasted into focused input (async)")
            return .success
        }

        // Store the task so it can be cancelled if needed
        currentPasteTask = task

        // Await and return the result
        return await task.value
    }

    // MARK: - PID-Reuse Defense

    /// Resolve a captured target PID to a running app, rejecting it when the resolved
    /// app's bundle ID does not match the one captured at record-start.
    ///
    /// macOS recycles PIDs: if the captured target quits between record-start and the
    /// paste, its PID may already belong to a DIFFERENT app. `NSRunningApplication`
    /// then resolves successfully but to the wrong process, which would leak the
    /// transcript. Validating the bundle ID closes that window. When the expected
    /// bundle ID is unknown (nil) we fall back to PID-existence only, preserving the
    /// previous behaviour.
    ///
    /// - Parameters:
    ///   - pid: PID captured for the paste target at record-start (nil if none).
    ///   - expectedBundleID: Bundle ID captured for that target at record-start.
    /// - Returns: The running app when it both resolves and (when known) matches the
    ///   expected bundle ID; otherwise `nil`.
    private func resolveCapturedTarget(pid: pid_t?, expectedBundleID: String?) -> NSRunningApplication? {
        guard let pid = pid,
              let resolved = NSRunningApplication(processIdentifier: pid) else {
            return nil
        }
        // Reject whenever we have a concrete expectation and the resolved app does
        // NOT match it — including the case where the resolved app has NO bundle ID
        // (e.g. PID reused by a bundle-less command-line tool, daemon, or helper).
        // Only a nil expected bundle ID falls through to the legacy PID-existence
        // behaviour, so we never break paste for callers that captured no expectation.
        if let expected = expectedBundleID,
           resolved.bundleIdentifier != expected {
            let actual = resolved.bundleIdentifier ?? "<nil>"
            logger.warning("⚠️ Captured paste target PID resolved to \(actual, privacy: .public) but expected \(expected, privacy: .public) — likely PID reuse. Treating target as lost.")
            return nil
        }
        return resolved
    }
}
