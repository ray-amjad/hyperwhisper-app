//
//  ModeEditorSheetHeightTests.swift
//  hyperwhisperTests
//
//  Regression cover for issue #711: the mode editor sheet was a hard
//  `.frame(width: 480, height: 700)`, and on the reporter's 1280x800-point
//  display its footer — Cancel, Create/Save and Delete — was drawn below the
//  bottom of the screen's visible area, on a window that cannot be resized.
//
//  `ModeEditorView.sheetMaxHeight(sheetTopY:visibleBottomY:)` carries the why.
//  What these add is that the answer moves with the WINDOW and not just with the
//  screen, and that both ends of the clamp are load-bearing: the `max(...)` floor
//  because `maxHeight < minHeight` is an invalid SwiftUI frame range (where the
//  issue's own `min(700, available)` breaks), and the design cap because a roomy
//  display must stay byte-identical to today.
//
//  The geometry below is SHAPED like the rented-Mac measurement (1280x800:
//  `visibleFrame` height 697-698, a default-placed window giving a ~612pt sheet
//  clear of the Dock), but every input is synthetic: no runtime constant is
//  asserted, because none can be measured from this target.
//

import Foundation
import SwiftUI
import Testing

@testable import HyperWhisper

@Suite("Mode editor sheet height")
struct ModeEditorSheetHeightTests {

    // A 1280x800-shaped display, and the content top of the 600pt main window at
    // its default placement and dragged down onto the Dock. All estimates.
    private static let visibleBottomY: CGFloat = 71
    private static let defaultContentTopY: CGFloat = 685
    private static let loweredContentTopY: CGFloat = 643

    private static func height(topY: CGFloat?, bottomY: CGFloat?) -> CGFloat {
        ModeEditorView.sheetMaxHeight(sheetTopY: topY, visibleBottomY: bottomY)
    }

    @MainActor
    @Test func keepsTheDesignHeightWhereThereIsRoom() {
        // A window high on a tall display has over 700pt below its content top,
        // so the sheet looks exactly as it always has. This is the case the
        // withdrawn parent-content-height clamp got wrong for 100% of users.
        #expect(Self.height(topY: 1200, bottomY: 60) == 700)
    }

    @MainActor
    @Test func shrinksToFitTheShortScreenFromTheIssue() {
        let height = Self.height(topY: Self.defaultContentTopY, bottomY: Self.visibleBottomY)
        #expect(height == 606)
        // The point of the fix: the bottom edge lands above the visible bottom.
        #expect(
            Self.defaultContentTopY - height
                == Self.visibleBottomY + ModeEditorView.sheetBottomMargin
        )
    }

    @MainActor
    @Test func followsTheWindowDownTheScreen() {
        // Same display, window dragged onto the Dock: less room, shorter sheet.
        // A clamp built from heights alone — the screen's or the parent content's
        // — cannot tell these two placements apart.
        #expect(Self.height(topY: Self.loweredContentTopY, bottomY: Self.visibleBottomY) == 564)
        #expect(
            Self.height(topY: Self.loweredContentTopY, bottomY: Self.visibleBottomY)
                < Self.height(topY: Self.defaultContentTopY, bottomY: Self.visibleBottomY)
        )
    }

    @MainActor
    @Test func handlesTheEdgesOfTheCoordinateSpace() {
        // Window dragged nearly off the bottom, then past it: still a valid frame
        // range, never a tiny or negative height.
        #expect(Self.height(topY: 300, bottomY: Self.visibleBottomY) == ModeEditorView.sheetMinHeight)
        #expect(Self.height(topY: 0, bottomY: Self.visibleBottomY) == ModeEditorView.sheetMinHeight)
        // A display below the primary has a negative `visibleFrame.minY`, so both
        // inputs are negative while the difference between them is not.
        #expect(Self.height(topY: -200, bottomY: -900) == 692)
    }

    @MainActor
    @Test func fallsBackToTheDesignHeightWhenAnInputIsMissing() {
        // `MainWindowStore.window` is nil before `WindowConfigurator` runs, and
        // `NSScreen.main` is nil with no display attached. Neither may narrow the
        // clamp: a missing input is "no limit known", not "no room".
        let design = ModeEditorView.sheetDesignHeight
        #expect(Self.height(topY: nil, bottomY: nil) == design)
        #expect(Self.height(topY: nil, bottomY: Self.visibleBottomY) == design)
        #expect(Self.height(topY: Self.defaultContentTopY, bottomY: nil) == design)
    }

    @MainActor
    @Test func isAlwaysAValidFrameRange() {
        // The result is piecewise linear in the available room, with breakpoints
        // only where the floor and the design cap take over. Both breakpoints,
        // both sides of each and the two extremes give identical coverage to
        // sweeping every point in between.
        let floorCrossover = ModeEditorView.sheetMinHeight + ModeEditorView.sheetBottomMargin
        let capCrossover = ModeEditorView.sheetDesignHeight + ModeEditorView.sheetBottomMargin
        let roomValues: [CGFloat] = [
            -1_000, 0,
            floorCrossover - 1, floorCrossover, floorCrossover + 1,
            (floorCrossover + capCrossover) / 2,
            capCrossover - 1, capCrossover, capCrossover + 1,
            10_000,
        ]
        for room in roomValues {
            let height = Self.height(topY: room, bottomY: 0)
            #expect(height >= ModeEditorView.sheetMinHeight)
            #expect(height <= ModeEditorView.sheetDesignHeight)
        }
    }

    // MARK: - The call site, not just the formula

    // The clamp above is pure and callable; the DEFECT is in `body`, which is
    // not. So these two read the production source the way
    // `LocalAPIBodyLimitTests.theOnlyBodyReadInTheLocalApiIsTheBoundedOne` does,
    // through the shared `ProductionSource` fixture — which strips comment lines
    // first, so prose about `height: 700` cannot stand in for the code.

    private static let viewPath = "app/macos/hyperwhisper/Views/Modes/ModeEditorView.swift"

    @Test func theSheetFrameIsNotAHardHeightAnyMore() throws {
        let body = try ProductionSource.slice(
            of: Self.viewPath,
            from: "var body: some View {",
            to: ".background(Color(NSColor.windowBackgroundColor))"
        )
        #expect(!body.contains("height: 700"), """
            The mode editor sheet is a fixed 700pt again — issue #711 verbatim. Its height must come \
            from ModeEditorView.sheetMaxHeight(sheetTopY:visibleBottomY:).
            """)
        #expect(body.contains("minHeight: Self.sheetMinHeight"))
        #expect(body.contains("idealHeight: maxHeight"))
        #expect(body.contains("maxHeight: maxHeight"))
    }

    @Test func theClampReadsTheAppsOwnMainWindowReference() throws {
        let property = try ProductionSource.slice(
            of: Self.viewPath,
            from: "private var maxSheetHeight: CGFloat {",
            to: "var body: some View {"
        )
        #expect(property.contains("MainWindowStore.window"))
        #expect(property.contains("Self.sheetMaxHeight("))
        // Key and main window follow app state and any panel that takes key, so
        // the sheet's parent must not be resolved through either.
        #expect(!property.contains("NSApp.keyWindow"))
        #expect(!property.contains("NSApp.mainWindow"))
    }
}
