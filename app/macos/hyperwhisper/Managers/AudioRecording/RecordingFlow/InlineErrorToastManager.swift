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
    /// Centred above the visible recording dialog, or above where the dialog
    /// opens by default when it is not visible (`RecordingDialogAnchor`).
    private func positionPanelAboveRecordingDialog(_ panel: NSPanel) {
        let size = NSSize(width: panelWidth, height: panelHeight)
        guard let placement = RecordingDialogAnchor.place(panel, size: size, gap: gapAboveRecordingDialog) else {
            return
        }
        if placement.isAboveVisibleDialog {
            logger.debug("📍 Positioned toast above recording dialog at (\(placement.frame.origin.x), \(placement.frame.origin.y))")
        } else {
            logger.debug("📍 Positioned toast at fallback position (\(placement.frame.origin.x), \(placement.frame.origin.y)) - recording dialog not found")
        }
    }
    }

    // MARK: - Space/Screen Change Handling

    /// Keep the toast on top across Space switches, and re-place it above the
    /// recording dialog when a display is added or its resolution changes
    /// (`FloatingPanelSpaceObserver`).
    private func installSpaceChangeObserver() {
        FloatingPanelSpaceObserver.install(
            into: &obsTokens,
            panel: { [weak self] in self?.panel },
            repositionOnScreenChange: { [weak self] panel in
                self?.positionPanelAboveRecordingDialog(panel)
            }
        )
    }

    /// Remove the Space/screen observers. Called from `dismiss()`.
    private func removeSpaceChangeObserver() {
        FloatingPanelSpaceObserver.remove(&obsTokens)
    }
}
