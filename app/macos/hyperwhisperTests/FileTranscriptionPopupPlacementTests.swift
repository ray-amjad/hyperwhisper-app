//
//  FileTranscriptionPopupPlacementTests.swift
//  hyperwhisperTests
//
//  Locks where the Transcribe File progress HUD sits (#1438).
//
//  The HUD stays up for minutes while the user keeps using the app. At
//  `.screenSaver` level and centred on screen it covered alert bodies, the open
//  panel, the Edit Mode sheet and its Model picker, and took clicks meant for
//  the window under it. Creating the NSPanel needs a window server, so these
//  read the wiring off the source instead.
//

import Foundation
import Testing

struct FileTranscriptionPopupPlacementTests {

    static let path = "app/macos/hyperwhisper/Managers/Transcription/Flows/FileTranscriptionPopupManager.swift"

    @Test func theHUDSitsBelowModalPanels() throws {
        let configure = try ProductionSource.slice(
            of: Self.path,
            from: "private func configurePanelAppearance(_ panel: NSPanel) {",
            to: "private func positionPanelTopRight(_ panel: NSPanel) {"
        )
        #expect(configure.contains("panel.level = .floating"))
        #expect(!configure.contains(".screenSaver"))
    }

    @Test func theHUDIsPlacedInTheTopRightCornerNotTheCentre() throws {
        let position = try ProductionSource.slice(
            of: Self.path,
            from: "private func positionPanelTopRight(_ panel: NSPanel) {",
            to: "private func installSpaceChangeObserver() {"
        )
        #expect(position.contains("visible.maxX - panelWidth - edgeMargin"))
        #expect(position.contains("visible.maxY - panelHeight - edgeMargin"))

        let code = try ProductionSource.code(of: Self.path)
        #expect(!code.contains("positionPanelCentered"))
        #expect(code.contains("positionPanelTopRight(panel)"))
    }
}
