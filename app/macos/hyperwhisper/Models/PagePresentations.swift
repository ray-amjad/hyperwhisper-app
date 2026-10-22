//
//  PagePresentations.swift
//  hyperwhisper
//
//  Issue #1672: the channel between AppState's page-change gate and the pages
//  that present a sheet, alert or confirmation dialog.
//
//  - Pages REPORT whether they present something right now
//    (`closesPresentationsOnPageChange(isPresenting:)`), so a page change is
//    held only for a sheet a PAGE presents. A sheet no page change removes
//    (MainAppView's own alerts, the AutoPasteHandler NSAlert, an AppKit file
//    panel) does not hold or cancel navigation.
//  - AppState asks the pages to CLOSE through `closeRequests`.
//
//  It holds no reference to AppState, so a view that uses the modifier needs
//  nothing in its environment (a `#Preview`, a sheet's own content).
//

import AppKit
import Combine

@MainActor
final class PagePresentations {

    static let shared = PagePresentations()

    /// Sent when a page change waits for a page's sheet. Every page that
    /// presents something sets it back to closed.
    let closeRequests = PassthroughSubject<Void, Never>()

    /// The main window's attached sheet, if any. Tests replace it.
    var attachedSheet: @MainActor () -> NSWindow? = { MainWindowStore.window?.attachedSheet }

    /// Pages that report a sheet, alert or dialog open right now.
    private(set) var openOwners: Set<UUID> = []

    /// The main window's sheet when a page last reported its presentation
    /// closed. That sheet is still animating out and is still the page's, so
    /// it holds a page change until AppKit has detached it.
    private weak var closingSheet: NSWindow?

    init() {}

    /// A page's presentation opened or closed.
    func report(_ owner: UUID, isPresenting: Bool) {
        if isPresenting {
            openOwners.insert(owner)
        } else if openOwners.remove(owner) != nil {
            closingSheet = attachedSheet()
        }
    }

    /// The page is gone; it presents nothing any more.
    func remove(_ owner: UUID) {
        openOwners.remove(owner)
    }

    /// True when the main window's attached sheet belongs to a page: a page
    /// reports a presentation open, or the sheet is the one a page has just
    /// closed. An AppKit open/save panel run as a sheet (Backup's file picker)
    /// is not torn down by SwiftUI when the page goes, so it never counts.
    var pageOwnsAttachedSheet: Bool {
        guard let sheet = attachedSheet(), !(sheet is NSSavePanel) else { return false }
        return !openOwners.isEmpty || sheet === closingSheet
    }

    /// Asks every page to close what it presents.
    func requestClose() {
        closeRequests.send()
    }
}
