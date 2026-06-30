//
//  InlineErrorToastManager.swift
//  hyperwhisper
//
//  INLINE ERROR TOAST MANAGER
//  Manages the floating NSPanel that displays the inline error toast above the recording dialog.
//  Unlike the large modal ErrorToastManager, this creates a compact, auto-dismissing pill.
//
//  POSITIONING:
//  - Centered horizontally on screen
//  - Positioned ~50px above the recording dialog
//  - Tracks recording dialog position to stay aligned
//
//  PANEL CONFIGURATION:
//  - Non-activating (doesn't steal focus)
//  - Floats above fullscreen apps
//  - Can appear on all Spaces
//  - Transparent background (SwiftUI handles the visual style)
//

import SwiftUI
import AppKit
import os

/// Logger for InlineErrorToastManager
private let logger = Logger(subsystem: "com.hyperwhisper.app", category: "InlineErrorToastManager")

/// Manages the floating inline error toast panel
///
/// **What This Does:**
/// Creates and manages an NSPanel that displays the InlineErrorToast view
/// above the recording dialog. The panel is non-activating and stays
/// on top even when other apps are focused.
///
/// **How It Works:**
/// 1. `show()` is called with error message and callbacks
/// 2. Creates an NSPanel with the InlineErrorToast SwiftUI view
/// 3. Positions the panel above the recording dialog
/// 4. Panel auto-dismisses when toast countdown completes
///
/// **Thread Safety:**
/// All methods are MainActor-isolated for UI consistency.
@MainActor
final class InlineErrorToastManager {

    // MARK: - Singleton

    static let shared = InlineErrorToastManager()

    // MARK: - Properties

    /// The floating panel that contains the toast
    private var panel: NSPanel?

    /// The hosting controller for the SwiftUI view
    private var hostingController: NSHostingController<InlineErrorToast>?

    /// Reference to AppState for navigating to settings
    private weak var appState: AppState?

    /// Notification observer tokens for space/screen change tracking
    /// These ensure the toast stays visible after space switches or screen changes
    private var obsTokens: [NSObjectProtocol] = []

    // MARK: - Size Configuration

    /// Toast panel width (matches InlineErrorToast)
    private let panelWidth: CGFloat = 360

    /// Toast panel height (matches InlineErrorToast)
    private let panelHeight: CGFloat = 40

    /// Vertical gap between recording dialog and this toast
    private let gapAboveRecordingDialog: CGFloat = 12

    // MARK: - Initialization

    private init() {}

    // MARK: - Public Methods

    /// Show the inline error toast above the recording dialog
    ///
    /// **Parameters:**
    /// - `message`: The error message to display
    /// - `showSettingsButton`: Whether to show "Open Settings" button
    /// - `appState`: Reference to AppState for navigation
    ///
    /// **Behavior:**
    /// - Dismisses any existing toast before showing new one
    /// - Creates non-activating panel positioned above recording dialog
    /// - Toast auto-dismisses after countdown completes
    func show(message: String, showSettingsButton: Bool, appState: AppState) {
        // Close any existing toast first
        dismiss()

        self.appState = appState

        logger.info("📢 Showing inline error toast: \(message, privacy: .public)")

        // Build the SwiftUI view
        let toastView = InlineErrorToast(
            message: message,
            showSettingsButton: showSettingsButton,
            onDismiss: { [weak self] in
                self?.dismiss()
            },
            onOpenSettings: { [weak self, weak appState] in
                // Navigate to settings
                appState?.openSettingsFromErrorToast()
                self?.dismiss()
            }
        )

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
    }

    /// Show the inline error toast for a TranscriptionError
    ///
    /// **What This Does:**
    /// Convenience method that uses the error's `showSettingsButton` property
    /// to determine whether to show the Settings button.
    ///
    /// **Parameters:**
    /// - `error`: The TranscriptionError to display
    /// - `appState`: Reference to AppState for navigation
    func show(error: TranscriptionError, appState: AppState) {
        let message = error.localizedDescription
        show(message: message, showSettingsButton: error.showSettingsButton, appState: appState)
    }

    /// Dismiss the current inline error toast
    ///
    /// **What This Does:**
    /// - Closes the panel
    /// - Cleans up references and observers
    /// - Safe to call even if no toast is showing
    func dismiss() {
        removeSpaceChangeObserver()
        panel?.orderOut(nil)
        panel = nil
        hostingController = nil
        logger.debug("🗑️ Inline error toast dismissed")
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
    /// 2. Calculate position centered above it
    /// 3. If recording dialog not found, center on screen with default offset
    ///
    /// **Fallback:**
    /// If the recording dialog window can't be found (e.g., not visible),
    /// the toast is positioned in a sensible default location at the
    /// bottom-center of the screen.
    private func positionPanelAboveRecordingDialog(_ panel: NSPanel) {
        // Try to find the recording dialog window
        let recordingDialogTitle = "recording.dialog.window.title".localized
        let recordingDialog = NSApplication.shared.windows.first { window in
            window.title == recordingDialogTitle && window.isVisible
        }

        if let recordingDialog = recordingDialog {
            // FOUND: Position above the recording dialog
            let dialogFrame = recordingDialog.frame

            // Calculate x position: center the toast horizontally relative to the dialog
            // Since toast is wider (280px) than dialog (200px), offset by the difference / 2
            let xOffset = (panelWidth - dialogFrame.width) / 2
            let x = dialogFrame.origin.x - xOffset

            // Calculate y position: above the dialog with gap
            let y = dialogFrame.origin.y + dialogFrame.height + gapAboveRecordingDialog

            panel.setFrame(
                NSRect(x: x, y: y, width: panelWidth, height: panelHeight),
                display: true
            )

            logger.debug("📍 Positioned toast above recording dialog at (\(x), \(y))")

        } else {
            // FALLBACK: Center on screen, above where dialog would be
            guard let screen = NSScreen.main else {
                panel.center()
                return
            }

            let visible = screen.visibleFrame

            // Recording dialog is at bottom-center, 40px from bottom
            // Position this toast above where it would be
            let recordingDialogY: CGFloat = visible.origin.y + 40  // Recording dialog position
            let recordingDialogHeight: CGFloat = 40  // Recording dialog height

            let x = visible.origin.x + (visible.width - panelWidth) / 2
            let y = recordingDialogY + recordingDialogHeight + gapAboveRecordingDialog

            panel.setFrame(
                NSRect(x: x, y: y, width: panelWidth, height: panelHeight),
                display: true
            )

            logger.debug("📍 Positioned toast at fallback position (\(x), \(y)) - recording dialog not found")
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
