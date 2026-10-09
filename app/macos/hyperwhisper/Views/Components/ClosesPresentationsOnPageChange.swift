//
//  ClosesPresentationsOnPageChange.swift
//  hyperwhisper
//
//  Issue #1672: a page that presents a sheet, alert or confirmation dialog
//  closes it when the window is about to change page, so the page is never
//  removed in the same update as its open sheet (that crashes AppKit).
//
//  The page reports whether it presents something (`isPresenting`) to
//  `PagePresentations`, so `AppState.routePageChange(to:)` holds a page change
//  only for a sheet a page owns. When it holds one it sends
//  `PagePresentations.closeRequests`, and this modifier runs the page's own
//  close action. The page changes once AppKit has detached the sheet.
//
//  No environment object is read here: a view using this modifier works in a
//  `#Preview` or any host that does not inject AppState.
//

import SwiftUI

private struct ClosesPresentationsOnPageChange: ViewModifier {
    let isPresenting: Bool
    let close: () -> Void
    /// This view's identity in `PagePresentations`.
    @State private var owner = UUID()

    init(isPresenting: Bool, close: @escaping () -> Void) {
        self.isPresenting = isPresenting
        self.close = close
    }

    func body(content: Content) -> some View {
        content
            .onReceive(PagePresentations.shared.closeRequests) { _ in
                close()
            }
            .onAppear {
                PagePresentations.shared.report(owner, isPresenting: isPresenting)
            }
            .onChange(of: isPresenting) { _, presenting in
                PagePresentations.shared.report(owner, isPresenting: presenting)
            }
            .onDisappear {
                PagePresentations.shared.remove(owner)
            }
    }
}

extension View {
    /// Reports whether this view presents a sheet, alert or dialog, and runs
    /// `close` when a page change waits for it. `isPresenting` is true while
    /// any of them is open; `close` sets every one back to its closed value.
    func closesPresentationsOnPageChange(
        isPresenting: Bool,
        _ close: @escaping () -> Void
    ) -> some View {
        modifier(ClosesPresentationsOnPageChange(isPresenting: isPresenting, close: close))
    }
}
