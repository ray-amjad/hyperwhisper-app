//
//  ModeEditorSheetHeightTests.swift
//  hyperwhisperTests
//
//  Regression cover for issue #711.
//
//  The mode editor sheet was a hard `.frame(width: 480, height: 700)`. A macOS
//  display of 1280x800 points leaves about 713 points of `visibleFrame` between
//  the menu bar and the Dock, so the sheet's footer — Cancel, Create/Save and
//  Delete — was drawn below the screen edge, and the main window is deliberately
//  not resizable, so the user could not reach it.
//
//  The clamp has two ends, and both are load-bearing:
//
//    * the sheet never asks for more than the 700pt design height, so nothing
//      changes on a display that has room for it;
//    * the result never drops under `sheetMinHeight`, because the value feeds a
//      `maxHeight` whose `minHeight` is that constant — `maxHeight < minHeight`
//      is an invalid SwiftUI frame range. A bare `min(700, visible - 80)`, which
//      is what the issue proposed, breaks exactly there.
//

import Foundation
import SwiftUI
import Testing

@testable import HyperWhisper

@Suite("Mode editor sheet height")
struct ModeEditorSheetHeightTests {

    @MainActor
    @Test func shrinksToFitTheShortScreenFromTheIssue() {
        // 1280x800 points: ~713pt of visibleFrame, the display in the bug report.
        #expect(ModeEditorView.sheetMaxHeight(visibleScreenHeight: 713) == 633)
    }

    @MainActor
    @Test func keepsTheDesignHeightWhereThereIsRoom() {
        // A 2560x1440 display: the sheet must look exactly as it always has.
        #expect(ModeEditorView.sheetMaxHeight(visibleScreenHeight: 1440) == 700)
    }

    @MainActor
    @Test func neverReturnsLessThanTheMinimum() {
        // The invalid-range trap: 300 - 80 = 220, under the 420 floor.
        #expect(ModeEditorView.sheetMaxHeight(visibleScreenHeight: 300) == ModeEditorView.sheetMinHeight)
        #expect(ModeEditorView.sheetMaxHeight(visibleScreenHeight: 0) >= ModeEditorView.sheetMinHeight)
    }

    @MainActor
    @Test func fallsBackToTheDesignHeightWithNoScreen() {
        // `NSScreen.main` is nil with no display attached; the sheet then behaves
        // as it did before this change rather than collapsing to the floor.
        #expect(ModeEditorView.sheetMaxHeight(visibleScreenHeight: nil) == ModeEditorView.sheetDesignHeight)
    }

    @MainActor
    @Test func isAlwaysAValidFrameRange() {
        // Every screen height from a tiny one to a 6K panel, in 1pt steps,
        // produces a maxHeight at or above the minHeight the frame also sets.
        for height in stride(from: CGFloat(0), through: CGFloat(3000), by: 1) {
            let maxHeight = ModeEditorView.sheetMaxHeight(visibleScreenHeight: height)
            #expect(maxHeight >= ModeEditorView.sheetMinHeight)
            #expect(maxHeight <= ModeEditorView.sheetDesignHeight)
        }
    }
}
