//
//  StreamingPreviewWindowManager.swift
//  hyperwhisper
//
//  Owns the floating preview bubble panel that sits above the recording
//  dialog for streaming sessions targeting preview-only apps (e.g. terminals).
//  The panel is visual-only — mouse events pass through to the focused app —
//  and tracks the recording panel's position as the user drags it.
//

import AppKit
import SwiftUI

@MainActor
final class StreamingPreviewWindowManager {
    static let shared = StreamingPreviewWindowManager()

    private var panel: NSPanel?
    private var recordingPanelMoveToken: NSObjectProtocol?
    private var spaceChangeTokens: [NSObjectProtocol] = []

    private let panelSize = NSSize(width: 480, height: 220)
    private let gapAboveRecordingPanel: CGFloat = 8

    private init() {}

    func open(appState: AppState) {
        if let existing = panel {
            existing.orderFrontRegardless()
            repositionPanel(existing)
            return
        }

        let view = StreamingPreviewBubble()
            .environmentObject(appState)

        let hostingController = NSHostingController(rootView: view)
        hostingController.view.wantsLayer = true
        hostingController.view.layer?.backgroundColor = NSColor.clear.cgColor

        let panel = NSPanel(contentViewController: hostingController)
        panel.styleMask = [.nonactivatingPanel, .borderless]
        panel.isFloatingPanel = true
        panel.level = .screenSaver
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
        panel.backgroundColor = .clear
        panel.isOpaque = false
        panel.hasShadow = false
        panel.hidesOnDeactivate = false
        panel.ignoresMouseEvents = true
        panel.becomesKeyOnlyIfNeeded = true

        panel.setContentSize(panelSize)
        panel.orderFrontRegardless()

        self.panel = panel
        repositionPanel(panel)
        installRecordingPanelObserver()
        installSpaceChangeObservers()
    }

    func close() {
        if let panel {
            panel.orderOut(nil)
            panel.close()
            self.panel = nil
        }
        removeRecordingPanelObserver()
        removeSpaceChangeObservers()
    }

    // MARK: - Positioning

    private func repositionPanel(_ panel: NSPanel) {
        let anchor = anchorFrame()
        let origin = NSPoint(
            x: anchor.midX - panelSize.width / 2,
            y: anchor.maxY + gapAboveRecordingPanel
        )
        panel.setFrame(NSRect(origin: origin, size: panelSize), display: true)
    }

    /// Anchor: frame of the recording panel if open, otherwise a sensible
    /// fallback at bottom-center of the main screen.
    private func anchorFrame() -> NSRect {
        if let recordingPanel = RecordingWindowManager.shared.panel {
            return recordingPanel.frame
        }
        guard let screen = NSScreen.main else {
            return NSRect(origin: .zero, size: .zero)
        }
        let fallbackSize = NSSize(width: 200, height: 40)
        let frame = screen.visibleFrame
        return NSRect(
            x: frame.midX - fallbackSize.width / 2,
            y: frame.minY + 40,
            width: fallbackSize.width,
            height: fallbackSize.height
        )
    }

    // MARK: - Observers

    private func installRecordingPanelObserver() {
        removeRecordingPanelObserver()
        recordingPanelMoveToken = NotificationCenter.default.addObserver(
            forName: NSWindow.didMoveNotification,
            object: nil,
            queue: .main
        ) { [weak self] notification in
            guard let self,
                  let movedPanel = notification.object as? NSPanel,
                  movedPanel === RecordingWindowManager.shared.panel,
                  let previewPanel = self.panel else { return }
            self.repositionPanel(previewPanel)
        }
    }

    private func removeRecordingPanelObserver() {
        if let token = recordingPanelMoveToken {
            NotificationCenter.default.removeObserver(token)
            recordingPanelMoveToken = nil
        }
    }

    private func installSpaceChangeObservers() {
        removeSpaceChangeObservers()
        let spaceToken = NSWorkspace.shared.notificationCenter.addObserver(
            forName: NSWorkspace.activeSpaceDidChangeNotification,
            object: nil,
            queue: .main
        ) { [weak self] _ in
            guard let self, let panel = self.panel else { return }
            panel.orderFrontRegardless()
        }
        let screenToken = NotificationCenter.default.addObserver(
            forName: NSApplication.didChangeScreenParametersNotification,
            object: nil,
            queue: .main
        ) { [weak self] _ in
            guard let self, let panel = self.panel else { return }
            self.repositionPanel(panel)
            panel.orderFrontRegardless()
        }
        spaceChangeTokens = [spaceToken, screenToken]
    }

    private func removeSpaceChangeObservers() {
        for token in spaceChangeTokens {
            NSWorkspace.shared.notificationCenter.removeObserver(token)
            NotificationCenter.default.removeObserver(token)
        }
        spaceChangeTokens.removeAll()
    }
}
