//
//  ModeEditorSheetHeightTests.swift
//  hyperwhisperTests
//
//  Regression cover for issue #711: the mode editor sheet was a hard
//  `.frame(width: 480, height: 700)`, and on the reporter's 1280x800-point
//  display its footer — Cancel, Create/Save and Delete — was drawn below the
//  bottom of the screen's visible area, on a window that cannot be resized.
//
//  `ModeEditorView.sheetMaxHeight(visibleScreenHeight:)` carries the why, and
//  the why is measured: AppKit lifts the parent window when a sheet does not
//  fit, so a clamp that reads the window POSITION is stale by the time the
//  sheet lands. This clamp is a function of sizes only.
//
//  What these tests add is that both ends of the clamp are load-bearing: the
//  `max(...)` floor because `maxHeight < minHeight` is an invalid SwiftUI frame
//  range — which is where the issue's own `min(700, screen - 80)` breaks — and
//  the design cap because a roomy display must stay byte-identical to today.
//
//  The geometry below is SHAPED like the rented-Mac measurement (a 1280x800
//  display measured a `visibleFrame` height of 697), but every input is
//  synthetic: no runtime constant is asserted, because none can be measured
//  from this target.
//

import Foundation
import SwiftUI
import Testing

@testable import HyperWhisper

@Suite("Mode editor sheet height")
struct ModeEditorSheetHeightTests {

    /// The visible height a 1280x800-point display measured on a rented Mac.
    private static let shortScreenVisibleHeight: CGFloat = 697

    private static func height(_ visibleScreenHeight: CGFloat?) -> CGFloat {
        ModeEditorView.sheetMaxHeight(visibleScreenHeight: visibleScreenHeight)
    }

    @MainActor
    @Test func keepsTheDesignHeightWhereThereIsRoom() {
        // A 1440pt or 6K display: the sheet looks exactly as it always has.
        // This is the case the withdrawn parent-content-height clamp got wrong
        // for 100% of users, on displays where #711 cannot happen at all.
        #expect(Self.height(1440) == ModeEditorView.sheetDesignHeight)
        #expect(Self.height(780) == ModeEditorView.sheetDesignHeight)
    }

    @MainActor
    @Test func shrinksToFitTheShortScreenFromTheIssue() {
        // 697 - 80 = 617, against a fixed 700 that did not fit the 697 of room.
        let height = Self.height(Self.shortScreenVisibleHeight)
        #expect(height == 617)
        #expect(height < ModeEditorView.sheetDesignHeight)
        #expect(height < Self.shortScreenVisibleHeight)
    }

    @MainActor
    @Test func neverReturnsAnInvalidFrameRange() {
        // The floor is not decoration: `minHeight` is `sheetMinHeight`, and a
        // `maxHeight` below it is an invalid range. The issue's own proposed
        // `min(700, visible - 80)` returns 220 here.
        #expect(Self.height(300) == ModeEditorView.sheetMinHeight)
        #expect(Self.height(0) == ModeEditorView.sheetMinHeight)
        #expect(Self.height(-100) == ModeEditorView.sheetMinHeight)
    }

    @MainActor
    @Test func fallsBackToTheDesignHeightWithNoScreen() {
        // `MainWindowStore.window` is nil before `WindowConfigurator` runs, and
        // `NSScreen.main` is nil with no display attached. A missing input is
        // "no limit known", not "no room".
        #expect(Self.height(nil) == ModeEditorView.sheetDesignHeight)
    }

    @MainActor
    @Test func isAlwaysAValidFrameRange() {
        // The result is piecewise linear in the visible height, with breakpoints
        // only where the floor and the design cap take over. Both breakpoints,
        // both sides of each, and the two extremes give identical coverage to
        // sweeping every point in between.
        let floorCrossover = ModeEditorView.sheetMinHeight + ModeEditorView.sheetScreenInset
        let capCrossover = ModeEditorView.sheetDesignHeight + ModeEditorView.sheetScreenInset
        let visibleHeights: [CGFloat] = [
            -1_000, 0,
            floorCrossover - 1, floorCrossover, floorCrossover + 1,
            (floorCrossover + capCrossover) / 2,
            capCrossover - 1, capCrossover, capCrossover + 1,
            10_000,
        ]
        for visibleHeight in visibleHeights {
            let height = Self.height(visibleHeight)
            #expect(height >= ModeEditorView.sheetMinHeight)
            #expect(height <= ModeEditorView.sheetDesignHeight)
        }
    }

    @MainActor
    @Test func isMonotonicInTheRoomAvailable() {
        // A shorter screen never yields a taller sheet.
        #expect(Self.height(500) <= Self.height(600))
        #expect(Self.height(600) <= Self.height(697))
        #expect(Self.height(697) <= Self.height(1440))
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
            from ModeEditorView.sheetMaxHeight(visibleScreenHeight:).
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
        // the sheet's screen must not be resolved through either.
        #expect(!property.contains("NSApp.keyWindow"))
        #expect(!property.contains("NSApp.mainWindow"))
    }
}
