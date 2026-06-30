//
//  FileTranscriptionPopupManager.swift
//  hyperwhisper
//
//  FILE TRANSCRIPTION POPUP MANAGER
//  Manages the floating NSPanel that displays file transcription progress.
//
//  PANEL CONFIGURATION:
//  - Non-activating (doesn't steal focus from other apps)
//  - Floats above fullscreen apps (screen saver level)
//  - Can appear on all Spaces
//  - Transparent background (SwiftUI handles the visual style)
//
//  POSITIONING:
//  - Centered on the main screen
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
/// centered on screen. The panel is non-activating and stays on top even when other
/// apps are focused.
///
/// **How It Works:**
/// 1. `show()` is called with progress state and cancel callback
/// 2. Creates an NSPanel with the FileTranscriptionProgressPopup SwiftUI view
/// 3. Centers the panel on the main screen
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
    /// - Creates non-activating panel centered on screen
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
        positionPanelCentered(panel)

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
    /// **Panel Configuration:**
    /// - Non-activating: Doesn't steal focus from other apps
    /// - Floating: Stays above normal windows
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
        panel.isMovable = true
        panel.isMovableByWindowBackground = true  // Allow dragging by clicking anywhere
        panel.hidesOnDeactivate = false
        panel.becomesKeyOnlyIfNeeded = false

        // Set content size
        panel.setContentSize(NSSize(width: panelWidth, height: panelHeight))

        // Ensure content view is transparent
        panel.contentView?.wantsLayer = true
        panel.contentView?.layer?.backgroundColor = NSColor.clear.cgColor
    }

    /// Position the panel centered on the main screen
    ///
    /// **Positioning Logic:**
    /// Centers the panel both horizontally and vertically on the main screen.
    /// Falls back to NSPanel.center() if screen info unavailable.
    private func positionPanelCentered(_ panel: NSPanel) {
        guard let screen = NSScreen.main else {
            panel.center()
            return
        }

        let visible = screen.visibleFrame

        // Center the panel on screen
        let x = visible.origin.x + (visible.width - panelWidth) / 2
        let y = visible.origin.y + (visible.height - panelHeight) / 2

        panel.setFrame(
            NSRect(x: x, y: y, width: panelWidth, height: panelHeight),
            display: true
        )

        logger.debug("📍 Positioned popup at center (\(x), \(y))")
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
            self.positionPanelCentered(panel)
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
