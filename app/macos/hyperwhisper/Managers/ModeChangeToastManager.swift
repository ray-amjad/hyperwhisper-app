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

        // No mode name here: it is user-typed (issue #804). The caller logs the modeId.
        logger.info("📢 Showing mode change toast")

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
    /// Same level as the recording dialog (`.screenSaver`), so it appears above
    /// fullscreen apps. The rest is the shared floating-panel style.
    private func configurePanelAppearance(_ panel: NSPanel) {
        FloatingPanelStyle.apply(
            to: panel,
            contentSize: NSSize(width: panelWidth, height: panelHeight),
            dragging: .fixed,
            becomesKeyOnlyIfNeeded: false
        )
        panel.level = .screenSaver
    }

    /// Position the panel above the recording dialog
    ///
    /// Centred above the visible recording dialog with a fixed gap, or above
    /// where the dialog opens by default when it is not visible
    /// (`RecordingDialogAnchor`).
    private func positionPanelAboveRecordingDialog(_ panel: NSPanel) {
        let size = NSSize(width: panelWidth, height: panelHeight)
        guard let placement = RecordingDialogAnchor.place(panel, size: size, gap: gapAboveRecordingDialog) else {
            return
        }
        if placement.isAboveVisibleDialog {
            logger.debug("📍 Positioned mode change toast above recording dialog at (\(placement.frame.origin.x), \(placement.frame.origin.y))")
        } else {
            logger.debug("📍 Positioned mode change toast at fallback position (\(placement.frame.origin.x), \(placement.frame.origin.y)) - recording dialog not found")
        }
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

    /// Observe Space and screen-parameter changes for as long as the mode
    /// toast is up: the first re-asserts its z-order, the second moves it back
    /// above the recording dialog (`FloatingPanelSpaceObserver`).
    private func installSpaceChangeObserver() {
        FloatingPanelSpaceObserver.install(
            into: &obsTokens,
            panel: { [weak self] in self?.panel },
            repositionOnScreenChange: { [weak self] panel in
                self?.positionPanelAboveRecordingDialog(panel)
            }
        )
    }

    /// Stop observing; `dismiss()` calls this before the panel goes.
    private func removeSpaceChangeObserver() {
        FloatingPanelSpaceObserver.remove(&obsTokens)
    }
}
