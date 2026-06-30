//
//  ModeChangeToastManager.swift
//  hyperwhisper
//
//  MODE CHANGE TOAST MANAGER
//  Manages the floating NSPanel that displays a brief notification when the user
//  changes modes via the keyboard shortcut (Control+Shift+K).
//
//  POSITIONING:
//  - Centered horizontally above the recording dialog
//  - Positioned with a fixed 12px gap above the recording dialog
//
//  PANEL CONFIGURATION:
//  - Non-activating (doesn't steal focus)
//  - Floats above fullscreen apps (.screenSaver level)
//  - Can appear on all Spaces
//  - Transparent background (SwiftUI handles the visual style)
//
//  BEHAVIOR:
//  - Auto-dismisses after 2 seconds
//  - Replaces existing toast if triggered rapidly (no stacking)
//

import SwiftUI
import AppKit
import os

/// Logger for ModeChangeToastManager
private let logger = Logger(subsystem: "com.hyperwhisper.app", category: "ModeChangeToastManager")

/// Manages the floating mode change toast panel
///
/// **What This Does:**
/// Creates and manages an NSPanel that displays the ModeChangeToast view
/// when the user cycles through modes via keyboard shortcut.
///
/// **How It Works:**
/// 1. `show(modeName:)` is called with the new mode name
/// 2. Any existing toast is dismissed first (prevents stacking)
/// 3. Creates an NSPanel with the ModeChangeToast SwiftUI view
/// 4. Positions the panel above the recording dialog
/// 5. Panel auto-dismisses after 2 seconds
///
/// **Thread Safety:**
/// All methods are MainActor-isolated for UI consistency.
@MainActor
final class ModeChangeToastManager {

    // MARK: - Singleton

    static let shared = ModeChangeToastManager()

    // MARK: - Properties

    /// The floating panel that contains the toast
    private var panel: NSPanel?

    /// The hosting controller for the SwiftUI view
    private var hostingController: NSHostingController<ModeChangeToast>?

    /// Timer for auto-dismiss
    private var dismissTimer: Timer?

    /// Notification observer tokens for space/screen change tracking
    /// These ensure the toast stays visible after space switches or screen changes
    private var obsTokens: [NSObjectProtocol] = []

    // MARK: - Size Configuration

    /// Toast panel width
    private let panelWidth: CGFloat = 200

    /// Toast panel height
    private let panelHeight: CGFloat = 36

    /// Vertical gap between recording dialog and this toast
    private let gapAboveRecordingDialog: CGFloat = 12

    /// Auto-dismiss delay in seconds
    private let dismissDelay: TimeInterval = 1.2

    // MARK: - Initialization

    private init() {}

    // MARK: - Public Methods

    /// Show the mode change toast
    ///
    /// **Parameters:**
    /// - `modeName`: The name of the newly selected mode
    ///
    /// **Behavior:**
    /// - Dismisses any existing toast before showing new one (prevents stacking)
    /// - Creates non-activating panel positioned at center-top of screen
    /// - Toast auto-dismisses after 2 seconds
    func show(modeName: String) {
        // Close any existing toast first (prevents stacking on rapid presses)
        dismiss()

        logger.info("📢 Showing mode change toast: \(modeName, privacy: .public)")

        // Build the SwiftUI view
        let toastView = ModeChangeToast(modeName: modeName)

        // Create hosting controller
        let host = NSHostingController(rootView: toastView)
        self.hostingController = host

        // Create the panel
        let panel = NSPanel(contentViewController: host)
        configurePanelAppearance(panel)
        positionPanelAboveRecordingDialog(panel)

        // Show the panel without activating the app
        panel.orderFrontRegardless()
        self.panel = panel

        // Install observers for space/screen changes to maintain z-order
        installSpaceChangeObserver()

        // Start auto-dismiss timer
        startDismissTimer()
    }

    /// Dismiss the current mode change toast
    ///
    /// **What This Does:**
    /// - Cancels the auto-dismiss timer
    /// - Closes the panel
    /// - Cleans up references and observers
    /// - Safe to call even if no toast is showing
    func dismiss() {
        dismissTimer?.invalidate()
        dismissTimer = nil
        removeSpaceChangeObserver()
        panel?.orderOut(nil)
        panel = nil
        hostingController = nil
        logger.debug("🗑️ Mode change toast dismissed")
    }

    // MARK: - Private Methods

    /// Configure the panel appearance for floating, non-activating behavior
    ///
    /// **Panel Configuration:**
    /// - Non-activating: Doesn't steal focus from other apps
    /// - Screen saver level: Appears above fullscreen apps
    /// - Can join all spaces: Visible on all virtual desktops
    /// - Transparent: SwiftUI handles the visual style
    private func configurePanelAppearance(_ panel: NSPanel) {
        // Non-activating panel that doesn't become key
        panel.styleMask = [.nonactivatingPanel]

        // Same level as recording dialog - appears above fullscreen apps
        panel.level = .screenSaver

        // Collection behavior for multi-space and fullscreen support
        panel.collectionBehavior = [
            .canJoinAllSpaces,      // Visible on all virtual desktops
            .fullScreenAuxiliary,   // Can appear over fullscreen apps
            .stationary,            // Doesn't move with spaces
            .ignoresCycle           // Not included in Cmd+Tab cycle
        ]

        // Visual appearance
        panel.backgroundColor = .clear
        panel.isOpaque = false
        panel.hasShadow = false  // SwiftUI shadow is used instead
        panel.isMovable = false
        panel.hidesOnDeactivate = false
        panel.becomesKeyOnlyIfNeeded = false

        // Set content size
        panel.setContentSize(NSSize(width: panelWidth, height: panelHeight))

        // Ensure content view is transparent
        panel.contentView?.wantsLayer = true
        panel.contentView?.layer?.backgroundColor = NSColor.clear.cgColor
    }

    /// Position the panel above the recording dialog
    ///
    /// **Positioning Logic:**
    /// 1. Find the recording dialog window by title
    /// 2. Calculate position centered above it with a fixed gap
    /// 3. If recording dialog not found, center on screen with default offset
    private func positionPanelAboveRecordingDialog(_ panel: NSPanel) {
        // Try to find the recording dialog window
        let recordingDialogTitle = "recording.dialog.window.title".localized
        let recordingDialog = NSApplication.shared.windows.first { window in
            window.title == recordingDialogTitle && window.isVisible
        }

        if let recordingDialog = recordingDialog {
            // FOUND: Position above the recording dialog
            let dialogFrame = recordingDialog.frame

            // Center the toast horizontally relative to the dialog
            let xOffset = (panelWidth - dialogFrame.width) / 2
            let x = dialogFrame.origin.x - xOffset

            // Position above the dialog with gap
            let y = dialogFrame.origin.y + dialogFrame.height + gapAboveRecordingDialog

            panel.setFrame(
                NSRect(x: x, y: y, width: panelWidth, height: panelHeight),
                display: true
            )

            logger.debug("📍 Positioned mode change toast above recording dialog at (\(x), \(y))")

        } else {
            // FALLBACK: Center on screen, above where dialog would be
            guard let screen = NSScreen.main else {
                panel.center()
                return
            }

            let visible = screen.visibleFrame

            // Recording dialog defaults to bottom-center, 40px from bottom
            let recordingDialogY: CGFloat = visible.origin.y + 40
            let recordingDialogHeight: CGFloat = 40

            let x = visible.origin.x + (visible.width - panelWidth) / 2
            let y = recordingDialogY + recordingDialogHeight + gapAboveRecordingDialog

            panel.setFrame(
                NSRect(x: x, y: y, width: panelWidth, height: panelHeight),
                display: true
            )

            logger.debug("📍 Positioned mode change toast at fallback position (\(x), \(y)) - recording dialog not found")
        }
    }

    /// Start the auto-dismiss timer
    private func startDismissTimer() {
        dismissTimer = Timer.scheduledTimer(withTimeInterval: dismissDelay, repeats: false) { [weak self] _ in
            Task { @MainActor in
                self?.dismiss()
            }
        }
    }

    // MARK: - Space/Screen Change Handling

    /// Install observers for space and screen changes
    ///
    /// **Why This Is Needed:**
    /// When user switches Spaces or screen resolution changes, the panel's z-order
    /// can be lost. These observers detect such changes and re-assert the panel's
    /// position in the z-order stack.
    ///
    /// **Events Tracked:**
    /// - `activeSpaceDidChangeNotification`: User switches virtual desktops (Spaces)
    /// - `didChangeScreenParametersNotification`: Display connect/disconnect, resolution changes
    private func installSpaceChangeObserver() {
        removeSpaceChangeObserver()

        // Track space changes (user switches virtual desktops)
        let token1 = NSWorkspace.shared.notificationCenter.addObserver(
            forName: NSWorkspace.activeSpaceDidChangeNotification,
            object: nil,
            queue: .main
        ) { [weak self] _ in
            guard let self, let panel = self.panel else { return }
            // Reassert z-order without activating app
            panel.orderFrontRegardless()
        }
        obsTokens.append(token1)

        // Track screen parameter changes (display connect/disconnect, resolution changes)
        let token2 = NotificationCenter.default.addObserver(
            forName: NSApplication.didChangeScreenParametersNotification,
            object: nil,
            queue: .main
        ) { [weak self] _ in
            guard let self, let panel = self.panel else { return }
            // Reposition and reassert z-order
            self.positionPanelAboveRecordingDialog(panel)
            panel.orderFrontRegardless()
        }
        obsTokens.append(token2)
    }

    /// Remove space/screen change observers
    ///
    /// **What This Does:**
    /// Cleans up notification observers to prevent memory leaks and stale callbacks.
    /// Called during dismiss() and before installing new observers.
    private func removeSpaceChangeObserver() {
        for token in obsTokens {
            // Try removing from both centers; harmless if not registered
            NSWorkspace.shared.notificationCenter.removeObserver(token)
            NotificationCenter.default.removeObserver(token)
        }
        obsTokens.removeAll()
    }
}
