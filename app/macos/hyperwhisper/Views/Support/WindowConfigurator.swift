//
//  WindowConfigurator.swift
//  hyperwhisper
//
//  Configures NSWindow properties that SwiftUI doesn't expose directly.
//  Used to extend content under the title bar and match the blurred background.
//

import SwiftUI
import AppKit

/// A tiny bridge that gives access to the underlying `NSWindow`
/// so we can tweak properties (e.g., full-size content view, transparency)
/// that SwiftUI doesn't expose directly.
struct WindowConfigurator: NSViewRepresentable {
    /// Called whenever the hosting NSView is attached to a window
    /// or when the window becomes available again (e.g., on updates).
    let onWindow: (NSWindow) -> Void

    func makeNSView(context: Context) -> NSView {
        let view = NSView(frame: .zero)
        // Defer until the view is actually in a window hierarchy
        DispatchQueue.main.async { [weak view] in
            if let window = view?.window {
                onWindow(window)
            }
        }
        return view
    }

    func updateNSView(_ nsView: NSView, context: Context) {
        // Re-apply in case SwiftUI recreates the window or view
        DispatchQueue.main.async { [weak nsView] in
            if let window = nsView?.window {
                onWindow(window)
            }
        }
    }
}
