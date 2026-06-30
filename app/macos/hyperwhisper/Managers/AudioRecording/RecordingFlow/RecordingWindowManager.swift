//
//  RecordingWindowManager.swift
//  hyperwhisper
//
//  Centralized controller for the waveform recording dialog window.
//  Ensures the panel can appear above fullscreen apps and without
//  requiring the main app window to be visible.
//

import SwiftUI
import AppKit
import ApplicationServices
import KeyboardShortcuts

final class RecordingWindowManager {
    static let shared = RecordingWindowManager()

    private(set) var panel: NSPanel?
    private var obsTokens: [NSObjectProtocol] = []

    // MARK: - Position Persistence


    /// Observer token for window move notifications
    private var moveObserverToken: NSObjectProtocol?
    private var previousActiveApp: NSRunningApplication?
    private var storedPanelStyleMask: NSWindow.StyleMask?
    private var storedPanelLevel: NSWindow.Level?
    private var storedBecomesKeyOnlyIfNeeded: Bool?
    private var preInteractionVisibleWindowNumbers: Set<Int> = []
    // CGEvent tap state for cancel overlay interception
    private var cancelOverlayEventTap: CFMachPort?
    private var cancelOverlayRunLoopSource: CFRunLoopSource?
    private var cancelOverlayOnReturn: (() -> Void)?
    private var cancelOverlayOnEscape: (() -> Void)?
    // Fallback NSEvent monitor used when the CGEventTap cannot be created
    // (e.g. the app is not Accessibility-trusted). See beginOverlayKeyInterception.
    private var cancelOverlayLocalMonitor: Any?
    private var cancelOverlayDidFocusForFallback: Bool = false
    private var cancelOverlayDisabledGlobalShortcut: Bool = false
    private var cancelOverlayShouldRestoreGlobalShortcut: Bool = false
    // CRITICAL: Track if overlay is actually visible to prevent processing keys when it's not
    private var isOverlayVisible: Bool = false

    private init() {}

    @MainActor
    func open(
        appState: AppState,
        audioManager: AudioRecordingManager,
        transcriptionPipeline: TranscriptionPipeline,
        settingsManager: SettingsManager
    ) {
        // Respect user setting to hide recording window
        if !settingsManager.showRecordingWindow { return }

        // CRITICAL: Idempotent guard — fixes HYPERWHISPER-PY / HYPERWHISPER-PZ.
        //
        // `appState.showRecordingDialog` is observed by two separate `.onChange`
        // handlers: one in MainAppView (the WindowGroup scene) and one in the
        // MenuBarExtra scene in hyperwhisperApp.swift. Both fire on every flip
        // and each schedules `Task { @MainActor in RecordingWindowManager.shared.open(...) }`,
        // so on a single recording start we run `open()` twice back-to-back on
        // the main actor.
        //
        // Without this guard the second invocation calls `close()` (tearing
        // down the panel the first invocation just created) and immediately
        // constructs a brand-new NSPanel + NSHostingController. AppKit is still
        // mid-teardown of the previous panel's view tree, and the second
        // `NSPanel(contentViewController:)` raises an uncaught NSException
        // inside `-[NSView layoutSubtreeIfNeeded]` during the window's initial
        // layout pass, which AppKit forwards to `+[NSApplication _crashOnException:]`
        // — a fatal EXC_BREAKPOINT bubbled up through the Swift concurrency
        // runtime (see Sentry HYPERWHISPER-PY + HYPERWHISPER-PZ in 2.33.0).
        //
        // If a panel is already live, just re-assert its z-order and return.
        // The dialog's state is driven entirely by @EnvironmentObject, so
        // there is nothing to rebuild on a duplicate `open()` call.
        if let existing = self.panel {
            existing.orderFrontRegardless()
            return
        }

        // Close any existing (stale) observers to avoid duplicates.
        close()

        // Build SwiftUI view with required dependencies
        let showBinding = Binding<Bool>(
            get: { appState.showRecordingDialog },
            set: { appState.showRecordingDialog = $0 }
        )

        let recordingView = RecordingDialog(isPresented: showBinding)
            .environmentObject(audioManager)
            .environmentObject(appState)
            .environmentObject(settingsManager)
            .environmentObject(transcriptionPipeline)
            // High-frequency metrics isolated for performance (prevents main window invalidation at 30 FPS)
            .environmentObject(audioManager.liveMetrics)
            .environment(\.managedObjectContext, PersistenceController.shared.container.viewContext)

        let hostingController = NSHostingController(rootView: recordingView)
        let panel = NSPanel(contentViewController: hostingController)
        let windowTitle = "recording.dialog.window.title".localized
        panel.title = windowTitle

        // Non-activating floating panel that can appear over fullscreen apps
        panel.styleMask = [.nonactivatingPanel]
        panel.isFloatingPanel = true

        // Make sure it truly stays on top, even over fullscreen apps
        // and across all Spaces.
        panel.level = .screenSaver
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]

        // Visuals / interactions
        panel.isMovableByWindowBackground = true
        panel.backgroundColor = .clear
        panel.isOpaque = false
        panel.hasShadow = false  // Use SwiftUI shadow so corners stay perfectly rounded
        panel.hidesOnDeactivate = false
        panel.becomesKeyOnlyIfNeeded = true

        let dialogSize = NSSize(width: 200, height: 40)
        panel.setContentSize(dialogSize)
        applyRoundedHostingMask(for: panel, cornerRadius: dialogSize.height / 2)

        // Restore saved position (ratios) or fall back to bottom-center
        let position = restoreDialogPosition(panelSize: dialogSize) ?? defaultDialogPosition(panelSize: dialogSize)
        panel.setFrame(NSRect(origin: position, size: dialogSize), display: true)

        // Show without activating the app or bringing main window forward
        panel.orderFrontRegardless()

        // Keep the panel above after Space/Screen changes
        installSpaceChangeObserver()

        // Save position when user drags the dialog
        installMoveObserver(for: panel)

        self.panel = panel
    }

    private func applyRoundedHostingMask(for panel: NSPanel, cornerRadius: CGFloat) {
        guard let contentView = panel.contentView else { return }

        // Ensure the AppKit hosting views stay transparent and match our capsule shape.
        contentView.wantsLayer = true
        contentView.layer?.backgroundColor = NSColor.clear.cgColor
        contentView.layer?.cornerRadius = cornerRadius
        contentView.layer?.masksToBounds = false

        if let hostingView = contentView.subviews.first {
            hostingView.wantsLayer = true
            hostingView.layer?.backgroundColor = NSColor.clear.cgColor
            hostingView.layer?.cornerRadius = cornerRadius
            hostingView.layer?.masksToBounds = false
        }
    }

    // MARK: - Position Calculation

    /// Converts saved position ratios to absolute coordinates, validating the result is on-screen.
    /// Returns nil if no saved position or if the saved position would be off-screen.
    private func restoreDialogPosition(panelSize: NSSize) -> NSPoint? {
        let defaults = UserDefaults.standard
        guard let screen = NSScreen.main,
              let xRatio = defaults.object(forKey: "recordingDialogPositionXRatio") as? Double,
              let yRatio = defaults.object(forKey: "recordingDialogPositionYRatio") as? Double else { return nil }

        let screenFrame = screen.visibleFrame
        let maxX = screenFrame.width - panelSize.width
        let maxY = screenFrame.height - panelSize.height

        let absoluteX = screenFrame.origin.x + (xRatio * maxX)
        let absoluteY = screenFrame.origin.y + (yRatio * maxY)

        // Validate: center of dialog must be on some screen
        let dialogCenter = NSPoint(x: absoluteX + panelSize.width / 2, y: absoluteY + panelSize.height / 2)
        guard NSScreen.screens.contains(where: { $0.frame.contains(dialogCenter) }) else { return nil }

        return NSPoint(x: absoluteX, y: absoluteY)
    }

    /// Returns bottom-center position on main screen, or center of screen if main screen unavailable.
    private func defaultDialogPosition(panelSize: NSSize) -> NSPoint {
        guard let screen = NSScreen.main else {
            // Fallback if no screen available
            return NSPoint(x: 0, y: 0)
        }

        let screenFrame = screen.visibleFrame
        let bottomMargin: CGFloat = 40

        return NSPoint(
            x: screenFrame.origin.x + (screenFrame.width - panelSize.width) / 2,
            y: screenFrame.origin.y + bottomMargin
        )
    }

    @MainActor
    func close() {
        if let panel = self.panel {
            panel.close()
            self.panel = nil
        }
        removeSpaceChangeObserver()
        removeMoveObserver()

        // NOTE: Do NOT dismiss InlineErrorToastManager here
        // The toast should stay visible for its full countdown even after the dialog closes

        // Also close any stray windows with the title, as a safety net
        let windowTitle = "recording.dialog.window.title".localized
        for window in NSApplication.shared.windows where window.title == windowTitle {
            window.close()
        }
    }

    // MARK: - Space/Screen Change Handling
    private func installSpaceChangeObserver() {
        removeSpaceChangeObserver()
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
        // Also track screen parameter changes (display connect/disconnect, resolution changes)
        let token2 = NotificationCenter.default.addObserver(
            forName: NSApplication.didChangeScreenParametersNotification,
            object: nil,
            queue: .main
        ) { [weak self] _ in
            guard let self, let panel = self.panel else { return }
            panel.orderFrontRegardless()
        }
        obsTokens.append(token2)
    }

    private func removeSpaceChangeObserver() {
        for token in obsTokens {
            // Try removing from both centers; harmless if not registered
            NSWorkspace.shared.notificationCenter.removeObserver(token)
            NotificationCenter.default.removeObserver(token)
        }
        obsTokens.removeAll()
    }

    // MARK: - Position Persistence

    /// Saves dialog position as ratios (0.0-1.0) when user drags it.
    /// Ratios allow position to scale properly across resolution/monitor changes.
    private func installMoveObserver(for panel: NSPanel) {
        removeMoveObserver()

        moveObserverToken = NotificationCenter.default.addObserver(
            forName: NSWindow.didMoveNotification,
            object: panel,
            queue: .main
        ) { [weak self] notification in
            guard let self,
                  let panel = notification.object as? NSPanel,
                  let screen = panel.screen ?? NSScreen.main else { return }

            let screenFrame = screen.visibleFrame
            let panelFrame = panel.frame

            // Calculate position as ratio of available space
            let maxX = screenFrame.width - panelFrame.width
            let maxY = screenFrame.height - panelFrame.height
            guard maxX > 0, maxY > 0 else { return }

            let xRatio = (panelFrame.origin.x - screenFrame.origin.x) / maxX
            let yRatio = (panelFrame.origin.y - screenFrame.origin.y) / maxY

            // Clamp to valid range (user might drag partially off-screen)
            UserDefaults.standard.set(min(max(xRatio, 0), 1), forKey: "recordingDialogPositionXRatio")
            UserDefaults.standard.set(min(max(yRatio, 0), 1), forKey: "recordingDialogPositionYRatio")
        }
    }

    private func removeMoveObserver() {
        guard let token = moveObserverToken else { return }
        NotificationCenter.default.removeObserver(token)
        moveObserverToken = nil
    }

    // MARK: - Temporary Focus Management
    /// Bring the recording panel forward and make it key so default buttons (Enter)
    /// and keyboard shortcuts work reliably. Stores the previously active app to
    /// restore focus after interaction.
    @MainActor
    func focusForInteraction() {
        // Capture the previously frontmost app if it's not us
        if previousActiveApp == nil,
           let front = NSWorkspace.shared.frontmostApplication,
           front.bundleIdentifier != Bundle.main.bundleIdentifier {
            previousActiveApp = front
        }

        // Temporarily adjust panel to accept key events
        if let p = panel {
            if storedPanelStyleMask == nil { storedPanelStyleMask = p.styleMask }
            if storedPanelLevel == nil { storedPanelLevel = p.level }
            if storedBecomesKeyOnlyIfNeeded == nil { storedBecomesKeyOnlyIfNeeded = p.becomesKeyOnlyIfNeeded }
            var mask = p.styleMask
            if mask.contains(.nonactivatingPanel) {
                mask.remove(.nonactivatingPanel)
            }
            p.styleMask = mask
            p.becomesKeyOnlyIfNeeded = false
            // Use a modal-level while interacting so it's key and visible
            p.level = .modalPanel
        }

        // Record which of our app windows were visible before activation (excluding our panel)
        preInteractionVisibleWindowNumbers = Set(
            NSApp.windows
                .filter { $0 !== panel && $0.isVisible }
                .map { $0.windowNumber }
        )

        // Activate our app and make the panel key
        NSApp.activate(ignoringOtherApps: true)
        panel?.makeKeyAndOrderFront(nil)

        // Immediately re-hide any app windows that became visible only due to activation
        for w in NSApp.windows where w !== panel {
            if w.isVisible && !preInteractionVisibleWindowNumbers.contains(w.windowNumber) {
                w.orderOut(nil)
            }
        }
    }

    /// Restore focus to the previously active app after a temporary interaction
    /// (e.g., after dismissing the confirmation overlay).
    @MainActor
    func restorePreviousFocus() {
        defer { previousActiveApp = nil }
        // Restore original panel configuration
        if let p = panel {
            if let lvl = storedPanelLevel { p.level = lvl }
            if let originalMask = storedPanelStyleMask { p.styleMask = originalMask }
            if let originalBKN = storedBecomesKeyOnlyIfNeeded { p.becomesKeyOnlyIfNeeded = originalBKN }
        }
        storedPanelLevel = nil
        storedPanelStyleMask = nil
        storedBecomesKeyOnlyIfNeeded = nil
        preInteractionVisibleWindowNumbers.removeAll()

        if let app = previousActiveApp {
            app.activate(options: [.activateIgnoringOtherApps])
        } else {
            // Fallback: simply deactivate our app so the last app regains focus
            NSApp.deactivate()
        }
    }

    // MARK: - Overlay Key Interception via CGEventTap (no app activation)
    /// Starts a short-lived global key interceptor to capture Return/Escape while the
    /// cancel overlay is visible. When Accessibility permission is available this uses a
    /// CGEventTap and avoids activating the app (so the main window never appears). When
    /// the tap cannot be created (not Accessibility-trusted) it falls back to a local
    /// NSEvent monitor, briefly making the panel key so Esc/Return still work.
    /// Returns true if some form of interception was installed.
    @discardableResult
    @MainActor
    func beginOverlayKeyInterception(
        onReturn: @escaping () -> Void,
        onEscape: @escaping () -> Void
    ) -> Bool {
        // CRITICAL: Always clean up an existing tap/fallback before creating a new one.
        // This prevents stale taps (or a leaked local monitor) from persisting.
        if cancelOverlayEventTap != nil || cancelOverlayLocalMonitor != nil {
            endOverlayKeyInterception()
        }

        // Create tap to intercept keyDown/keyUp before apps receive them. The
        // action runs on keyUp so the global cancel shortcut cannot see the
        // same physical key release after the overlay is dismissed.
        let mask = (1 << CGEventType.keyDown.rawValue) | (1 << CGEventType.keyUp.rawValue)
        cancelOverlayOnReturn = onReturn
        cancelOverlayOnEscape = onEscape
        isOverlayVisible = true  // Mark overlay as visible

        let tapCallback: CGEventTapCallBack = { _, type, event, refcon in
            guard let refcon = refcon else { return Unmanaged.passUnretained(event) }
            let manager = Unmanaged<RecordingWindowManager>.fromOpaque(refcon).takeUnretainedValue()

            // CRITICAL: Handle tap being disabled by the system.
            // A CGEventTap is automatically disabled in two scenarios:
            // 1. .tapDisabledByTimeout - callback took too long to return
            // 2. .tapDisabledByUserInput - Secure Event Input engaged (e.g. a sudo/
            //    Touch ID prompt from a background process during a long recording)
            // Without re-enabling here the overlay's Esc/Return would stay dead until
            // the overlay is dismissed and re-shown. Mirrors BareModifierKeyMonitor.
            if type == .tapDisabledByTimeout || type == .tapDisabledByUserInput {
                if let tap = manager.cancelOverlayEventTap {
                    CGEvent.tapEnable(tap: tap, enable: true)
                    let reason = type == .tapDisabledByTimeout ? "timeout" : "user input"
                    AppLogger.ui.debug("RecordingWindowManager: cancel-overlay CGEventTap re-enabled after \(reason)")
                    SentryService.addBreadcrumb(
                        message: "Cancel-overlay CGEventTap re-enabled",
                        category: "recording.cancelOverlay",
                        data: ["reason": reason]
                    )
                }
                return Unmanaged.passUnretained(event)
            }

            guard type == .keyDown || type == .keyUp else {
                return Unmanaged.passUnretained(event)
            }

            // CRITICAL: Only process keys if overlay is actually visible
            // This prevents intercepting keys after the overlay has been dismissed
            guard manager.isOverlayVisible else {
                return Unmanaged.passUnretained(event)
            }
            
            let keyCode = event.getIntegerValueField(.keyboardEventKeycode)
            switch keyCode {
            case 36, 76: // Return, Numpad Enter
                if type == .keyDown {
                    return nil // consume until keyUp triggers the action
                }
                if let handler = manager.cancelOverlayOnReturn {
                    manager.cancelOverlayShouldRestoreGlobalShortcut = false
                    DispatchQueue.main.async { handler() }
                    return nil // consume
                }
            case 53: // Escape
                if type == .keyDown {
                    return nil // consume until keyUp triggers the action
                }
                if let handler = manager.cancelOverlayOnEscape {
                    manager.cancelOverlayShouldRestoreGlobalShortcut = true
                    DispatchQueue.main.async { handler() }
                    return nil // consume
                }
            default:
                break
            }
            return Unmanaged.passUnretained(event)
        }

        guard let tap = CGEvent.tapCreate(
            tap: .cgSessionEventTap,
            place: .headInsertEventTap,
            options: .defaultTap,
            eventsOfInterest: CGEventMask(mask),
            callback: tapCallback,
            userInfo: UnsafeMutableRawPointer(Unmanaged.passUnretained(self).toOpaque())
        ) else {
            // CGEvent.tapCreate returns nil when the app is not Accessibility-trusted
            // (e.g. a first-run user skipped the permission step). Rather than leaving
            // the overlay with a dead keyboard, fall back to a local NSEvent monitor.
            // A local monitor only sees events routed to our app, so we also make the
            // panel key (focusForInteraction) — this is undone in endOverlayKeyInterception.
            AppLogger.ui.error("RecordingWindowManager: failed to create cancel-overlay CGEventTap (not Accessibility-trusted?) — using local NSEvent fallback")
            SentryService.addBreadcrumb(
                message: "Cancel-overlay CGEventTap unavailable — using local NSEvent fallback",
                category: "recording.cancelOverlay",
                data: ["axTrusted": AXIsProcessTrusted()]
            )
            beginOverlayKeyInterceptionFallback()
            return true
        }

        cancelOverlayEventTap = tap
        let src = CFMachPortCreateRunLoopSource(kCFAllocatorDefault, tap, 0)
        cancelOverlayRunLoopSource = src
        CFRunLoopAddSource(CFRunLoopGetMain(), src, .commonModes)
        CGEvent.tapEnable(tap: tap, enable: true)
        return true
    }

    /// Fallback key interception used when the CGEventTap cannot be installed
    /// (no Accessibility permission). Makes the panel key so keyboard events are
    /// routed to our app, then consumes Return/Escape via a local NSEvent monitor.
    /// `cancelOverlayOnReturn` / `cancelOverlayOnEscape` / `isOverlayVisible` are
    /// already set by `beginOverlayKeyInterception` before this is called.
    @MainActor
    private func beginOverlayKeyInterceptionFallback() {
        // Route keyboard events to our app so the local monitor can see them.
        focusForInteraction()
        cancelOverlayDidFocusForFallback = true
        KeyboardShortcuts.disable(.cancelRecording)
        cancelOverlayDisabledGlobalShortcut = true
        cancelOverlayShouldRestoreGlobalShortcut = true

        cancelOverlayLocalMonitor = NSEvent.addLocalMonitorForEvents(matching: [.keyDown, .keyUp]) { [weak self] event in
            guard let self, self.isOverlayVisible else { return event }
            if event.type == .keyDown {
                switch event.keyCode {
                case 36, 76, 53: // Return, Numpad Enter, Escape
                    return nil // consume until keyUp triggers the action
                default:
                    return event
                }
            }

            switch event.keyCode {
            case 36, 76: // Return, Numpad Enter
                if let handler = self.cancelOverlayOnReturn {
                    self.cancelOverlayShouldRestoreGlobalShortcut = false
                    DispatchQueue.main.async { handler() }
                    return nil // consume
                }
            case 53: // Escape
                if let handler = self.cancelOverlayOnEscape {
                    self.cancelOverlayShouldRestoreGlobalShortcut = true
                    DispatchQueue.main.async { handler() }
                    return nil // consume
                }
            default:
                break
            }
            return event
        }
    }

    @MainActor
    func endOverlayKeyInterception(restoreGlobalCancelShortcut: Bool? = nil) {
        // CRITICAL: Mark overlay as not visible immediately to prevent processing any more keys
        isOverlayVisible = false

        if let src = cancelOverlayRunLoopSource {
            CFRunLoopRemoveSource(CFRunLoopGetMain(), src, .commonModes)
            cancelOverlayRunLoopSource = nil
        }
        if let tap = cancelOverlayEventTap {
            CGEvent.tapEnable(tap: tap, enable: false)
            CFMachPortInvalidate(tap)
            cancelOverlayEventTap = nil
        }
        // Tear down the no-Accessibility fallback (local monitor + temporary focus)
        if let monitor = cancelOverlayLocalMonitor {
            NSEvent.removeMonitor(monitor)
            cancelOverlayLocalMonitor = nil
        }
        if cancelOverlayDidFocusForFallback {
            cancelOverlayDidFocusForFallback = false
            restorePreviousFocus()
        }
        if cancelOverlayDisabledGlobalShortcut {
            if restoreGlobalCancelShortcut ?? cancelOverlayShouldRestoreGlobalShortcut {
                KeyboardShortcuts.enable(.cancelRecording)
            }
            cancelOverlayDisabledGlobalShortcut = false
            cancelOverlayShouldRestoreGlobalShortcut = false
        }
        cancelOverlayOnReturn = nil
        cancelOverlayOnEscape = nil
    }

    // MARK: - Modal Confirmation (NSAlert)
    /// Presents a sheet-based NSAlert attached to the recording panel to confirm cancellation.
    /// Enter triggers the default button (confirm), Escape triggers resume. Keyboard events do
    /// not leak to other apps because the panel is made key/active during the interaction.
    @MainActor
    func presentCancelConfirmation(
        messageText: String = "recording.cancel.dialog.title".localized,
        informativeText: String = "recording.cancel.dialog.message".localized,
        onConfirm: @escaping () -> Void,
        onResume: @escaping () -> Void
    ) {
        let alert = NSAlert()
        alert.alertStyle = .warning
        alert.messageText = messageText
        alert.informativeText = informativeText
        // Default button (Return)
        _ = alert.addButton(withTitle: "recording.cancel.dialog.confirm".localized)
        // Secondary button (Escape)
        let resumeButton = alert.addButton(withTitle: "recording.cancel.dialog.resume".localized)
        resumeButton.keyEquivalent = "\u{1b}" // ESC

        // Ensure our panel is eligible/key for the sheet and key handling
        focusForInteraction()

        if let p = panel {
            alert.beginSheetModal(for: p) { [weak self] response in
                switch response {
                case .alertFirstButtonReturn:
                    onConfirm()
                default:
                    onResume()
                }
                self?.restorePreviousFocus()
            }
        } else {
            // Fallback: app-modal if panel is unavailable
            let response = alert.runModal()
            switch response {
            case .alertFirstButtonReturn:
                onConfirm()
            default:
                onResume()
            }
            restorePreviousFocus()
        }
    }
}
