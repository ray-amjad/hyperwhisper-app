//
//  PageChangeGate.swift
//  hyperwhisper
//
//  Issue #1672: a page change must never remove a page in the same update
//  that still presents a sheet.
//
//  `MainAppView.contentView` switches on the page. When the page changes while
//  the old page presents a `.sheet` (the Modes page's Edit or Create Mode
//  sheet, a Model Library sheet, the Backup import sheet...), SwiftUI tears the
//  sheet down during layout inside a display-cycle commit. AppKit's
//  sheet-close animation then spins a nested run loop that re-enters the
//  update cycle from inside its own commit, and the app dies with SIGSEGV.
//
//  So the page that is SHOWN (`AppState.displayedNavigationItem`) is kept
//  apart from the page that is ASKED FOR (`AppState.selectedNavigationItem`,
//  which every caller still writes). With no sheet on the main window the shown
//  page follows at once. With a sheet up, the gate holds the request, AppState
//  asks the page to close its sheets, and the page changes on a later run-loop
//  turn, once the sheet is gone. The shown page NEVER changes while the sheet
//  is still attached: if the sheet has not gone by the time limit, the held
//  page change is cancelled and the selection goes back to the shown page, so
//  a sheet that ignores the request can neither crash the app nor pin the
//  sidebar to a page the window does not show.
//
//  The gate is a plain value with no window, timer or SwiftUI in it, so the
//  rules are unit-tested by calling it (`PageChangeGateTests`).
//

import Foundation

struct PageChangeGate: Equatable {

    /// What the caller must do after asking the gate something.
    enum Outcome: Equatable {
        /// Show this page now.
        case show(NavigationItem)
        /// A sheet is up: ask the shown page to close its sheets, and check
        /// again on a later run-loop turn.
        case waitForSheet
        /// The sheet outlasted the wait limit: the held page change is
        /// dropped. Put the selection back to this page (the shown one).
        case cancelled(stayOn: NavigationItem)
        /// Nothing to do.
        case unchanged
    }

    /// How often AppState checks whether the sheet has gone.
    static let sheetPollInterval: TimeInterval = 0.05

    /// How long a held page waits for the sheet before the page change is
    /// cancelled. A sheet's close animation takes about 0.25 s; this only
    /// fires when a sheet does not answer the close request.
    static let sheetWaitLimit: TimeInterval = 2.0

    /// The page the window shows.
    private(set) var displayed: NavigationItem

    /// The page held back while a sheet closes; nil when none is held.
    private(set) var pending: NavigationItem?

    init(displayed: NavigationItem) {
        self.displayed = displayed
    }

    var isWaitingForSheet: Bool { pending != nil }

    /// A page change was asked for.
    /// - Parameter sheetPresented: whether the main window has a sheet up now.
    mutating func request(_ item: NavigationItem, sheetPresented: Bool) -> Outcome {
        if item == displayed {
            // Staying on the shown page removes nothing. It also cancels a
            // page that was held back (the last request wins).
            pending = nil
            return .unchanged
        }
        if !sheetPresented {
            pending = nil
            displayed = item
            return .show(item)
        }
        // The last request wins.
        pending = item
        return .waitForSheet
    }

    /// A later run-loop turn: show the held page once the sheet has gone.
    /// While the sheet is attached the shown page never changes; past the
    /// wait limit the held page change is cancelled instead.
    mutating func recheck(sheetPresented: Bool, waitLimitPassed: Bool) -> Outcome {
        guard let item = pending else { return .unchanged }
        if sheetPresented {
            guard waitLimitPassed else { return .waitForSheet }
            pending = nil
            return .cancelled(stayOn: displayed)
        }
        pending = nil
        displayed = item
        return .show(item)
    }
}
