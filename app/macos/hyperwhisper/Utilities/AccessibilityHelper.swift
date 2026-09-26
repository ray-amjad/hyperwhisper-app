//
//  AccessibilityHelper.swift
//  hyperwhisper
//
//  Created by Assistant on 16/08/2025.
//
//  ACCESSIBILITY HELPER
//  Centralized utility class for accessibility and clipboard operations.
//  This eliminates code duplication across the app for:
//  - Checking accessibility permissions
//  - Opening System Settings
//  - Clipboard operations
//  - Auto-paste functionality
//
//  All accessibility-related functionality should use this helper
//  instead of implementing their own versions.

import Foundation
import AppKit
import os

/// Centralized helper for accessibility and clipboard operations
/// This is a singleton to ensure consistent behavior across the app
@MainActor
public class AccessibilityHelper {

    // MARK: - Singleton

    /// Shared instance for app-wide use
    static let shared = AccessibilityHelper()

    /// Logger for accessibility operations
    let logger = Logger(subsystem: "com.hyperwhisper.app", category: "Accessibility")

    // Private init to enforce singleton pattern
    private init() {}

    // MARK: - Logging control
    /// Avoid spamming the console with repeated permission guidance
    var hasLoggedPermissionGuidance = false

    /// True once this app run reported a missing accessibility permission to
    /// Sentry from the auto-paste path. The permission is missing for every
    /// dictation until the user grants it, so one unhappy setup would otherwise
    /// send one event per recording. See `reportPasteOutcome(_:attempt:)`.
    var hasReportedMissingPastePermission = false

    // MARK: - Clipboard Restoration Management
    /// The currently active clipboard restoration work item (if any)
    /// This allows us to cancel pending restorations when a new recording starts
    var activeRestorationWorkItem: DispatchWorkItem?

    /// Structure to hold clipboard data for restoration
    /// We can't reuse NSPasteboardItem objects, so we extract and store the raw data
    struct ClipboardItemData {
        let types: [NSPasteboard.PasteboardType]
        let data: [NSPasteboard.PasteboardType: Data]
    }

    /// The original clipboard content before any recordings started
    /// This is preserved across multiple recordings to ensure we restore the true original
    /// ENHANCED: Now stores ALL pasteboard data (text, images, files, rich text, etc.)
    /// instead of just plain text. This prevents data loss when user has non-text content copied.
    /// Note: We store the DATA, not the NSPasteboardItem objects themselves, because
    /// pasteboard items cannot be reused after the pasteboard is cleared.
    var originalClipboardData: [ClipboardItemData]?

    /// Track whether we're in an active recording session
    /// Used to determine if we should save the clipboard as "original"
    var isInRecordingSession = false

    // MARK: - Async Paste Management
    /// The currently active paste task (if any)
    /// This allows us to cancel in-flight paste operations when starting a new one
    var currentPasteTask: Task<SmartPasteResult, Never>?

    #if DEBUG
    /// TEST SEAM, Debug builds only (tests run against Debug). Always nil in the
    /// app. When set, `executePasteAsync` uses it in place of
    /// `hasAccessibilityPermission()`, so a test can reach the branches after the
    /// permission guard on a CI Mac that never grants Accessibility. A Release
    /// build has no such property and no way around the permission check.
    var pastePermissionOverrideForTesting: Bool?

    /// TEST SEAM, Debug builds only. Always nil in the app. When set,
    /// `canPasteIntoFocusedElement()` returns its result instead of reading the
    /// focused element, so a test can drive the no-focused-field, cancelled and
    /// send-failed exits of `executePasteAsync` (#1034). It also answers the focus
    /// guard inside `sendPasteCommand()`, so a test that sets it to true on a Mac
    /// that grants Accessibility would post a real Cmd+V; that is why the tests
    /// that set it skip when `AXIsProcessTrusted()`. Release has no such property.
    var canPasteOverrideForTesting: (@MainActor () -> Bool)?
    #endif

    // MARK: - Permission Polling Management
    /// The currently active accessibility permission polling task (if any)
    /// Ensures a single shared polling loop — concurrent callers queue their
    /// completions instead of spawning parallel timer chains
    var permissionPollingTask: Task<Void, Never>?

    /// Completions waiting on the active permission polling loop
    var permissionPollingCompletions: [(Bool) -> Void] = []

    /// Deadline for the active polling loop. Restarted whenever a new caller
    /// queues, so a late joiner doesn't inherit a nearly-expired timeout from
    /// an older abandoned prompt
    var permissionPollingDeadline: Date?
}
