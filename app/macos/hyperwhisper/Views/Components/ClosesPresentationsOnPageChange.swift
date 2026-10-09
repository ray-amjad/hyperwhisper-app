//
//  ClosesPresentationsOnPageChange.swift
//  hyperwhisper
//
//  Issue #1672: a page that presents a sheet, alert or confirmation dialog
//  closes it when the window is about to change page, so the page is never
//  removed in the same update as its open sheet (that crashes AppKit).
//
//  `AppState.routePageChange(to:)` holds the page change while the main
//  window has a sheet up and bumps `pageSheetDismissalRequest`; this modifier
//  runs the page's own close action on that bump. The page changes once AppKit
//  has detached the sheet.
//

import SwiftUI

private struct ClosesPresentationsOnPageChange: ViewModifier {
    @EnvironmentObject private var appState: AppState
    let close: () -> Void

    func body(content: Content) -> some View {
        content.onChange(of: appState.pageSheetDismissalRequest) { _, _ in
            close()
        }
    }
}

extension View {
    /// Runs `close` when a page change waits for this page's sheets to close.
    /// `close` sets every sheet, alert and dialog state the view owns back to
    /// its closed value.
    func closesPresentationsOnPageChange(_ close: @escaping () -> Void) -> some View {
        modifier(ClosesPresentationsOnPageChange(close: close))
    }
}
