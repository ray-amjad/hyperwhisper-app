//
//  FileTranscriptionPopupManager.swift
//  hyperwhisper
//
//  FILE TRANSCRIPTION POPUP MANAGER
//  Manages the floating NSPanel that displays file transcription progress.
//
//  PANEL CONFIGURATION:
//  - Non-activating (doesn't steal focus from other apps)
//  - Floats above normal windows and fullscreen apps (floating level), but
//    below modal panels so alerts and the open panel draw over it (#1438)
//  - Can appear on all Spaces
//  - Transparent background (SwiftUI handles the visual style)
//
//  POSITIONING:
//  - Top-right corner of the main screen, clear of the centre where alerts,
//    sheets and the open panel are drawn (#1438)
//  - Tracks screen changes to maintain position
//
//  PATTERN:
//  Follows the InlineErrorToastManager pattern for consistent behavior
//  across all floating panels in the app.
//

import SwiftUI
import AppKit
import os

/// Logger for FileTranscriptionPopupManager
private let logger = Logger(subsystem: "com.hyperwhisper.app", category: "FileTranscriptionPopupManager")

/// Manages the floating file transcription progress popup panel
///
/// **What This Does:**
/// Creates and manages an NSPanel that displays the FileTranscriptionProgressPopup view
/// in the top-right corner of the screen. The panel is non-activating and stays on top even when other
/// apps are focused.
///
/// **How It Works:**
/// 1. `show()` is called with progress state and cancel callback
/// 2. Creates an NSPanel with the FileTranscriptionProgressPopup SwiftUI view
/// 3. Places the panel in the top-right corner of the main screen
/// 4. Panel stays visible until `dismiss()` is called
///
/// **Thread Safety:**
/// All methods are MainActor-isolated for UI consistency.
@MainActor
final class FileTranscriptionPopupManager {

    // MARK: - Singleton

    static let shared = FileTranscriptionPopupManager()

    // MARK: - Properties

    /// The floating panel that contains the progress popup
    private var panel: NSPanel?

    /// The hosting controller for the SwiftUI view
    private var hostingController: NSHostingController<FileTranscriptionProgressPopup>?

    /// Reference to the progress state
    private var progressState: FileTranscriptionProgress?

    /// Notification observer tokens for space/screen change tracking
    /// These ensure the popup stays visible after space switches or screen changes
    private var obsTokens: [NSObjectProtocol] = []

    // MARK: - Size Configuration

    /// Popup panel width
    private let panelWidth: CGFloat = 280

    /// Popup panel height
    private let panelHeight: CGFloat = 110

    /// Gap between the popup and the top-right edges of the visible frame
    private let edgeMargin: CGFloat = 16

    // MARK: - Initialization

    private init() {}

    // MARK: - Public Methods

    /// Show the file transcription progress popup
    ///
    /// **Parameters:**
    /// - `progress`: The progress state to observe
    /// - `onCancel`: Callback when user clicks cancel button
    ///
    /// **Behavior:**
    /// - Dismisses any existing popup before showing new one
    /// - Creates non-activating panel in the top-right corner of the screen
    /// - Popup stays visible until `dismiss()` is called
    func show(progress: FileTranscriptionProgress, onCancel: @escaping () -> Void) {
        // Close any existing popup first
        dismiss()

        self.progressState = progress

        logger.info("📢 Showing file transcription progress popup")

        // Build the SwiftUI view
        let popupView = FileTranscriptionProgressPopup(
            progress: progress,
            onCancel: { [weak self] in
                onCancel()
                self?.dismiss()
            }
        )

        // Create hosting controller
        let host = NSHostingController(rootView: popupView)
        self.hostingController = host

        // Create the panel
        let panel = NSPanel(contentViewController: host)
        configurePanelAppearance(panel)
        positionPanelTopRight(panel)

        // Show the panel without activating the app
        panel.orderFrontRegardless()
        self.panel = panel

        // Install observers for space/screen changes to maintain z-order
        installSpaceChangeObserver()
    }

    /// Dismiss the current progress popup
    ///
    /// **What This Does:**
    /// - Closes the panel
    /// - Cleans up references and observers
    /// - Safe to call even if no popup is showing
    func dismiss() {
        removeSpaceChangeObserver()
        panel?.orderOut(nil)
        panel = nil
        hostingController = nil
        progressState = nil
        logger.debug("🗑️ File transcription progress popup dismissed")
    }

    // MARK: - Private Methods

    /// Configure the panel appearance for floating, non-activating behavior
    ///
    /// The shared floating-panel style, draggable by clicking anywhere on it,
    /// at `.floating` level (below).
    private func configurePanelAppearance(_ panel: NSPanel) {
        FloatingPanelStyle.apply(
            to: panel,
            contentSize: NSSize(width: panelWidth, height: panelHeight),
            dragging: .byWindowBackground,
            becomesKeyOnlyIfNeeded: false
        )

        // Floating, not .screenSaver like the recording dialog: this HUD stays up
        // for minutes while the user keeps using the app, and at .screenSaver it
        // covered alerts, the open panel and the Model picker (#1438). .floating
        // still sits above normal windows (and fullscreen apps, with the
        // shared collection behaviour) but under .modalPanel windows.
        panel.level = .floating
    }

    /// Position the panel in the top-right corner of the main screen
    ///
    /// **Positioning Logic:**
    /// Alerts, sheets and the open panel are drawn centred, so a centred HUD
    /// hid their text and took clicks meant for the window under it (#1438).
    /// The top-right corner (where notifications sit) keeps it out of their way;
    /// the bottom centre is taken by the recording dialog and its toasts.
    /// Falls back to NSPanel.center() if screen info unavailable.
    private func positionPanelTopRight(_ panel: NSPanel) {
        guard let screen = NSScreen.main else {
            panel.center()
            return
        }

        let visible = screen.visibleFrame

        let x = visible.maxX - panelWidth - edgeMargin
        let y = visible.maxY - panelHeight - edgeMargin

        panel.setFrame(
            NSRect(x: x, y: y, width: panelWidth, height: panelHeight),
            display: true
        )

        logger.debug("📍 Positioned popup at top-right (\(x), \(y))")
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
        FloatingPanelSpaceObserver.install(
            into: &obsTokens,
            panel: { [weak self] in self?.panel },
            repositionOnScreenChange: { [weak self] panel in
                self?.positionPanelTopRight(panel)
            }
        )
    }

    /// Remove space/screen change observers
    ///
    /// **What This Does:**
    /// Cleans up notification observers to prevent memory leaks and stale callbacks.
    /// Called during dismiss() and before installing new observers.
    private func removeSpaceChangeObserver() {
        FloatingPanelSpaceObserver.remove(&obsTokens)
    }
}
