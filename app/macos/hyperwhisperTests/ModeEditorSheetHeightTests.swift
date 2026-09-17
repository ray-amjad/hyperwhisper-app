//
//  ModeEditorSheetHeightTests.swift
//  hyperwhisperTests
//
//  Regression cover for issue #711.
//
//  The mode editor sheet was a hard `.frame(width: 480, height: 700)`. A macOS
//  display of 1280x800 points leaves about 700 points of `visibleFrame` between
//  the menu bar and the Dock (measured: 697-698 on macOS 26.3.1), so the sheet's
//  footer — Cancel, Create/Save and Delete — was drawn below the screen edge, and
//  the main window is deliberately not resizable, so the user could not reach it.
//  `713` below is a synthetic input, not a measurement.
//
//  Two independent limits bind, and the clamp takes the smaller:
//
//    * the SCREEN — `visibleFrame` minus the chrome inset;
//    * the PARENT WINDOW — a sheet hangs from its parent's content top and
//      AppKit never resizes it nor moves the parent, so a sheet taller than the
//      parent's content always overhangs the parent's bottom edge. The main
//      window is a fixed 1000x600 and is draggable from anywhere, so a
//      screen-only clamp put the footer back under the Dock the moment the
//      window was dragged down.
//
//  And two ends, both load-bearing:
//
//    * the sheet never asks for more than the 700pt design height, so nothing
//      changes where both limits have room for it;
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
        // A parent tall enough not to bind, so the screen limit is the one tested.
        #expect(ModeEditorView.sheetMaxHeight(visibleScreenHeight: 713, parentContentHeight: 1000) == 633)
    }

    @MainActor
    @Test func neverOutgrowsTheParentWindowContent() {
        // The real main window: a fixed 1000x600 with a 600pt content height, on a
        // display with room to spare. The sheet hangs from the parent's content
        // top, so anything over 600 overhangs the parent — and the window is
        // draggable, so that overhang lands under the Dock. 633 was the old answer.
        #expect(ModeEditorView.sheetMaxHeight(visibleScreenHeight: 1440, parentContentHeight: 600) == 600)
        #expect(ModeEditorView.sheetMaxHeight(visibleScreenHeight: 713, parentContentHeight: 600) == 600)
    }

    @MainActor
    @Test func takesWhicheverLimitIsSmaller() {
        // Screen binds (633 < 690) …
        #expect(ModeEditorView.sheetMaxHeight(visibleScreenHeight: 713, parentContentHeight: 690) == 633)
        // … parent binds (500 < 633).
        #expect(ModeEditorView.sheetMaxHeight(visibleScreenHeight: 713, parentContentHeight: 500) == 500)
    }

    @MainActor
    @Test func keepsTheDesignHeightWhereThereIsRoom() {
        // A 2560x1440 display and a parent taller than the design height: the
        // sheet must look exactly as it always has.
        #expect(ModeEditorView.sheetMaxHeight(visibleScreenHeight: 1440, parentContentHeight: 1200) == 700)
    }

    @MainActor
    @Test func neverReturnsLessThanTheMinimum() {
        // The invalid-range trap: 300 - 80 = 220, under the 420 floor.
        #expect(
            ModeEditorView.sheetMaxHeight(visibleScreenHeight: 300, parentContentHeight: 1000)
                == ModeEditorView.sheetMinHeight
        )
        // And the same trap from the parent side: a 200pt parent content rect.
        #expect(
            ModeEditorView.sheetMaxHeight(visibleScreenHeight: 1440, parentContentHeight: 200)
                == ModeEditorView.sheetMinHeight
        )
        #expect(ModeEditorView.sheetMaxHeight(visibleScreenHeight: 0, parentContentHeight: 0) >= ModeEditorView.sheetMinHeight)
    }

    @MainActor
    @Test func fallsBackToTheDesignHeightWhenAnInputIsMissing() {
        // Each input is optional and a missing one must NOT narrow the clamp:
        // `NSScreen.main` is nil with no display attached, and the host window is
        // nil before AppKit has resolved one.
        #expect(
            ModeEditorView.sheetMaxHeight(visibleScreenHeight: nil, parentContentHeight: nil)
                == ModeEditorView.sheetDesignHeight
        )
        // No screen, parent binds.
        #expect(ModeEditorView.sheetMaxHeight(visibleScreenHeight: nil, parentContentHeight: 600) == 600)
        // No parent, screen binds — the pre-round behaviour.
        #expect(ModeEditorView.sheetMaxHeight(visibleScreenHeight: 713, parentContentHeight: nil) == 633)
    }

    @MainActor
    @Test func isAlwaysAValidFrameRange() {
        // Every screen height from a tiny one to a 6K panel, in 1pt steps, and
        // against a nil, a short and a tall parent, produces a maxHeight at or
        // above the minHeight the frame also sets.
        let parents: [CGFloat?] = [nil, 0, 600, 3000]
        for parent in parents {
            for height in stride(from: CGFloat(0), through: CGFloat(3000), by: 1) {
                let maxHeight = ModeEditorView.sheetMaxHeight(
                    visibleScreenHeight: height,
                    parentContentHeight: parent
                )
                #expect(maxHeight >= ModeEditorView.sheetMinHeight)
                #expect(maxHeight <= ModeEditorView.sheetDesignHeight)
            }
        }
    }
}
