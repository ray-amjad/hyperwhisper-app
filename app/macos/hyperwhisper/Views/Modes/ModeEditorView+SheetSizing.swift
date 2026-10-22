//
//  ModeEditorView+SheetSizing.swift
//  HyperWhisper
//
//  The mode editor sheet's height clamp (issue #711), as pure statics. The
//  instance property that reads the window, and the frame that applies the
//  clamp, stay in ModeEditorView.swift.
//

import SwiftUI

extension ModeEditorView {

    // MARK: - Sheet sizing

    /// Shortest the sheet may be drawn. Below this the form is unusable, and a
    /// `maxHeight` under a `minHeight` is an invalid frame range — which is why
    /// the clamp below is a `max(...)` and not a bare `min(700, available)`.
    static let sheetMinHeight: CGFloat = 420
    /// The height the sheet has always used where there is room for it.
    static let sheetDesignHeight: CGFloat = 700
    /// Room left for the window chrome above the sheet and for clearance below
    /// it, taken out of the screen's visible height.
    static let sheetScreenInset: CGFloat = 80

    /// Static so hyperwhisperTests can assert the clamp without standing up the
    /// view. The bound is the screen's VISIBLE height — what is left between the
    /// menu bar and the Dock — less the inset. It is deliberately a function of
    /// SIZES only, never of the window's position, and the reason is measured:
    ///
    /// On macOS 26.3.1, AppKit MOVES THE PARENT WINDOW UP when a sheet does not
    /// fit below its content top (measured lifts, by window content top:
    /// 250 → 192, 150 → 143, and with the old fixed 700pt sheet 75 → 31). So a
    /// clamp that samples the window position is stale by the time the sheet
    /// lands: a position-aware version of this function overshot by 3pt at a
    /// content top of 150 and by 28pt at 250, and clipped the footer again. A
    /// height that does not depend on the position is a fixed input to that
    /// lift instead of a moving target of it.
    ///
    /// The other rejected shape was a parent-content-HEIGHT clamp. The app has
    /// exactly one sheet parent — a fixed 1000x600 window — so that limit bound
    /// on every display and cost every user about 100pt of form height, even on
    /// displays where #711 cannot happen. This one only ever binds on a short
    /// screen. A nil input means no limit is known, so it falls back to the
    /// design height rather than narrowing the clamp.
    static func sheetMaxHeight(visibleScreenHeight: CGFloat?) -> CGFloat {
        guard let visibleScreenHeight else { return sheetDesignHeight }
        return max(sheetMinHeight, min(sheetDesignHeight, visibleScreenHeight - sheetScreenInset))
    }
}
