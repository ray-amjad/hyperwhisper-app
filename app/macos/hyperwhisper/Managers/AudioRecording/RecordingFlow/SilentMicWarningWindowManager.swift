//
//  SilentMicWarningWindowManager.swift
//  hyperwhisper
//
//  Issue #2411: owns the small panel that shows "No sound from {mic}" next to the
//  recording dialog while the microphone hears nothing.
//
//  WHY A COMPANION PANEL: the recording dialog is a fixed 200x40 pill, and a mic
//  name does not fit in it beside the stop button and waveform, which must stay.
//  WHY BELOW THE PILL: the inline error toast, the mode change toast and the
//  streaming preview bubble all sit above it, and the preview bubble is on screen
//  for the whole of a preview-only streaming session. Below never collides with
//  any of them. When the pill sits too close to the bottom of its screen for
//  that, the warning goes above it instead.
//
//  Visual only: mouse events pass through. Follows the pill when it is dragged.
//  `RecordingDialog` shows and hides it; `RecordingWindowManager.close()` hides it.
//

import AppKit
import SwiftUI

@MainActor
final class SilentMicWarningWindowManager {
    static let shared = SilentMicWarningWindowManager()

    private var panel: NSPanel?
    private var hostingController: NSHostingController<SilentMicrophoneWarningView>?
    private var shownDeviceName: String?
    private var recordingPanelMoveToken: NSObjectProtocol?
    private var spaceChangeTokens: [NSObjectProtocol] = []

    private let gap: CGFloat = 6

    private init() {}

    /// Show (or update) the warning for `deviceName`. Idempotent.
    func show(deviceName: String) {
        if let panel, let hostingController {
            if shownDeviceName != deviceName {
                hostingController.rootView = SilentMicrophoneWarningView(deviceName: deviceName)
                shownDeviceName = deviceName
            }
            panel.orderFrontRegardless()
            reposition(panel)
            return
        }

        let host = NSHostingController(rootView: SilentMicrophoneWarningView(deviceName: deviceName))
        host.view.wantsLayer = true
        host.view.layer?.backgroundColor = NSColor.clear.cgColor

        let panel = NSPanel(contentViewController: host)
        FloatingPanelStyle.apply(
            to: panel,
            contentSize: SilentMicrophoneWarningView.size,
            dragging: .fixed,
            becomesKeyOnlyIfNeeded: true
        )
        panel.isFloatingPanel = true
        panel.level = .screenSaver
        panel.ignoresMouseEvents = true

        self.panel = panel
        self.hostingController = host
        self.shownDeviceName = deviceName

        reposition(panel)
        panel.orderFrontRegardless()
        installObservers()
        AppLogger.audio.info("Silent-mic warning panel shown")
    }

    /// Hide the warning. Safe to call when nothing is showing.
    func hide() {
        removeObservers()
        guard let panel else { return }
        panel.orderOut(nil)
        panel.close()
        self.panel = nil
        hostingController = nil
        shownDeviceName = nil
    }

    // MARK: - Positioning

    private func reposition(_ panel: NSPanel) {
        let size = SilentMicrophoneWarningView.size
        let anchor: NSRect
        let visibleFrame: NSRect?
        if let recordingPanel = RecordingWindowManager.shared.panel {
            anchor = recordingPanel.frame
            visibleFrame = (recordingPanel.screen ?? NSScreen.main)?.visibleFrame
        } else if let screen = NSScreen.main {
            anchor = RecordingWindowManager.defaultDialogFrame(in: screen.visibleFrame)
            visibleFrame = screen.visibleFrame
        } else {
            panel.center()
            return
        }
        let frame = Self.frame(size: size, below: anchor, gap: gap, within: visibleFrame)
        panel.setFrame(frame, display: true)
    }

    /// Centred under `anchor`, or above it when there is no room below inside
    /// `visibleFrame`.
    nonisolated static func frame(size: CGSize, below anchor: NSRect, gap: CGFloat, within visibleFrame: NSRect?) -> NSRect {
        let x = anchor.midX - size.width / 2
        let belowY = anchor.minY - gap - size.height
        let fitsBelow = visibleFrame.map { belowY >= $0.minY } ?? true
        let y = fitsBelow ? belowY : anchor.maxY + gap
        return NSRect(x: x, y: y, width: size.width, height: size.height)
    }

    // MARK: - Observers

    private func installObservers() {
        removeObservers()
        recordingPanelMoveToken = NotificationCenter.default.addObserver(
            forName: NSWindow.didMoveNotification,
            object: nil,
            queue: .main
        ) { [weak self] notification in
            // Same shape as `StreamingPreviewWindowManager`'s move observer.
            guard let self,
                  let movedPanel = notification.object as? NSPanel,
                  movedPanel === RecordingWindowManager.shared.panel,
                  let panel = self.panel else { return }
            self.reposition(panel)
        }
        FloatingPanelSpaceObserver.install(
            into: &spaceChangeTokens,
            panel: { [weak self] in self?.panel },
            repositionOnScreenChange: { [weak self] panel in
                self?.reposition(panel)
            }
        )
    }

    private func removeObservers() {
        if let token = recordingPanelMoveToken {
            NotificationCenter.default.removeObserver(token)
            recordingPanelMoveToken = nil
        }
        FloatingPanelSpaceObserver.remove(&spaceChangeTokens)
    }
}
