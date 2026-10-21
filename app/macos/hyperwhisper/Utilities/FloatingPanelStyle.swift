//
//  FloatingPanelStyle.swift
//  hyperwhisper
//
//  FLOATING PANEL STYLE
//  One implementation of the floating, non-activating NSPanel set-up that the
//  recording dialog, the streaming preview, the two toasts and the file
//  transcription HUD all share, plus the "sit above the recording dialog"
//  placement the two toasts share. The Space/screen observers live beside it in
//  FloatingPanelSpaceObserver.swift.
//

import AppKit

/// The style every floating, non-activating panel in the app is built with.
///
/// **Why This Exists:**
/// Five managers wrote the same style mask, collection behaviour and
/// transparency block by hand, and three of the copies were the same down to
/// the comments. The values that really differ between panels are parameters
/// here with no default, so each call site states the value it uses and none
/// can change by accident.
///
/// **What It Does NOT Own:**
/// The window level and `isFloatingPanel`. Those differ per panel (the file
/// transcription HUD sits at `.floating`, below modal panels, #1438), and
/// `isFloatingPanel` must be set before the level, so each manager writes both
/// itself. Anything only one panel sets (a title, `ignoresMouseEvents`, a
/// rounded mask) stays with that panel too.
@MainActor
enum FloatingPanelStyle {
    /// Whether the user can drag the panel.
    enum Dragging {
        /// `isMovable = false`: the panel stays where its manager puts it.
        case fixed
        /// `isMovable = true` and `isMovableByWindowBackground = true`: a drag
        /// anywhere on the panel moves it.
        case byWindowBackground
        /// Neither property is written, so both keep AppKit's defaults.
        case appKitDefault
    }

    /// Apply the shared floating-panel style.
    ///
    /// Writes the non-activating style mask, the all-Spaces/fullscreen
    /// collection behaviour, a clear non-opaque shadowless background, the
    /// dragging and key-window behaviour asked for, and the content size, and
    /// makes the content view's layer transparent (SwiftUI draws the visuals).
    ///
    /// - Parameters:
    ///   - panel: The panel to style.
    ///   - contentSize: The panel's content size.
    ///   - dragging: Whether and how the user can drag the panel.
    ///   - becomesKeyOnlyIfNeeded: The panel's `becomesKeyOnlyIfNeeded`.
    static func apply(
        to panel: NSPanel,
        contentSize: NSSize,
        dragging: Dragging,
        becomesKeyOnlyIfNeeded: Bool
    ) {
        // Non-activating: showing or clicking the panel never steals focus.
        // (`.borderless` is the empty mask, so it adds nothing to this.)
        panel.styleMask = [.nonactivatingPanel]

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

        switch dragging {
        case .fixed:
            panel.isMovable = false
        case .byWindowBackground:
            panel.isMovable = true
            panel.isMovableByWindowBackground = true
        case .appKitDefault:
            break
        }

        panel.hidesOnDeactivate = false
        panel.becomesKeyOnlyIfNeeded = becomesKeyOnlyIfNeeded

        panel.setContentSize(contentSize)

        // Ensure content view is transparent
        panel.contentView?.wantsLayer = true
        panel.contentView?.layer?.backgroundColor = NSColor.clear.cgColor
    }
}

/// Where a panel goes to sit centred above the recording dialog.
///
/// **Why This Exists:**
/// The inline error toast and the mode change toast both sit above the
/// recording dialog, and each had its own copy of the lookup, the centring and
/// a fallback that hard-coded the dialog's default size and margin. The
/// fallback now reads those from `RecordingWindowManager`, which owns them.
@MainActor
enum RecordingDialogAnchor {
    /// A frame for a panel above the recording dialog.
    struct Placement {
        let frame: NSRect
        /// True when a visible recording dialog was found; false when the
        /// frame sits above where the dialog opens by default.
        let isAboveVisibleDialog: Bool
    }

    /// Put `panel` at `size`, centred above the recording dialog, `gap` points
    /// above it.
    ///
    /// When no recording dialog is visible, the panel goes above where the
    /// dialog opens by default, at the bottom centre of the main screen. With
    /// no main screen either, the panel is centred with `NSWindow.center()`.
    ///
    /// - Returns: where the panel went, or nil when it was centred.
    @discardableResult
    static func place(_ panel: NSPanel, size: NSSize, gap: CGFloat) -> Placement? {
        let recordingDialogTitle = "recording.dialog.window.title".localized
        let recordingDialog = NSApplication.shared.windows.first { window in
            window.title == recordingDialogTitle && window.isVisible
        }

        let dialogFrame: NSRect
        if let recordingDialog {
            dialogFrame = recordingDialog.frame
        } else if let screen = NSScreen.main {
            dialogFrame = RecordingWindowManager.defaultDialogFrame(in: screen.visibleFrame)
        } else {
            panel.center()
            return nil
        }

        // Centre the panel horizontally on the dialog, `gap` above its top edge
        let x = dialogFrame.origin.x - (size.width - dialogFrame.width) / 2
        let y = dialogFrame.origin.y + dialogFrame.height + gap

        let frame = NSRect(x: x, y: y, width: size.width, height: size.height)
        panel.setFrame(frame, display: true)
        return Placement(frame: frame, isAboveVisibleDialog: recordingDialog != nil)
    }
}
