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
