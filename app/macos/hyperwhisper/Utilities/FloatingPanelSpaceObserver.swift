//
//  FloatingPanelSpaceObserver.swift
//  hyperwhisper
//
//  FLOATING PANEL SPACE OBSERVER
//  One implementation of the "keep this floating panel on top when the Space or
//  the screen layout changes" observers that every floating NSPanel manager in
//  the app needs.
//

import AppKit

/// Space- and screen-change observers for a floating `NSPanel`.
///
/// **Why This Exists:**
/// A non-activating panel loses its place in the z-order when the user switches
/// Spaces, and its frame stops making sense when a display is connected or the
/// resolution changes. Every floating-panel manager here answered that with the
/// same pair of observers — `NSWorkspace.activeSpaceDidChangeNotification`
/// re-asserts the z-order, `NSApplication.didChangeScreenParametersNotification`
/// repositions and then re-asserts — and each had grown its own copy, down to
/// the same comments. The copies differed only in *whether* a reposition
/// happens on the screen-parameter change and in which positioning method runs,
/// so "keep this panel on top" was defined in five places at once. Adding a
/// sixth panel meant copying it a sixth time.
///
/// **What It Does NOT Own:**
/// Panel creation, styling, level, collection behaviour and dismissal all stay
/// with each manager — those genuinely differ per panel. This owns the two
/// notification registrations and their teardown, nothing else.
@MainActor
enum FloatingPanelSpaceObserver {
    /// Register the Space and screen-parameter observers for a panel.
    ///
    /// Any tokens already in `tokens` are unregistered first, so calling this
    /// twice cannot leak an observer.
    ///
    /// Both handlers run on the main queue and no-op once `panel` returns nil,
    /// so a manager that has already dismissed its panel does no work.
    ///
    /// - Parameters:
    ///   - tokens: The caller's token storage. Pass the same array back to
    ///     ``remove(_:)`` at dismiss time.
    ///   - panel: Resolves the live panel each time a notification fires.
    ///     Capture the owning manager weakly here.
    ///   - repositionOnScreenChange: Runs with the live panel on a
    ///     screen-parameter change, before the z-order is re-asserted. Pass nil
    ///     for a panel that must keep its own frame across a display change —
    ///     the recording dialog restores a user-dragged position of its own and
    ///     must not be moved here.
    static func install(
        into tokens: inout [NSObjectProtocol],
        panel: @escaping @MainActor () -> NSPanel?,
        repositionOnScreenChange: (@MainActor (NSPanel) -> Void)? = nil
    ) {
        remove(&tokens)

        // Track space changes (user switches virtual desktops)
        let spaceToken = NSWorkspace.shared.notificationCenter.addObserver(
            forName: NSWorkspace.activeSpaceDidChangeNotification,
            object: nil,
            queue: .main
        ) { _ in
            // `queue: .main` delivers on the main thread, so the panel and the
            // caller's closures are already on their own actor.
            MainActor.assumeIsolated {
                guard let panel = panel() else { return }
                // Reassert z-order without activating app
                panel.orderFrontRegardless()
            }
        }
        tokens.append(spaceToken)

        // Track screen parameter changes (display connect/disconnect, resolution changes)
        let screenToken = NotificationCenter.default.addObserver(
            forName: NSApplication.didChangeScreenParametersNotification,
            object: nil,
            queue: .main
        ) { _ in
            MainActor.assumeIsolated {
                guard let panel = panel() else { return }
                // Reposition and reassert z-order
                repositionOnScreenChange?(panel)
                panel.orderFrontRegardless()
            }
        }
        tokens.append(screenToken)
    }

    /// Unregister the tokens from ``install(into:panel:repositionOnScreenChange:)``
    /// and empty the array.
    ///
    /// Cleans up notification observers to prevent memory leaks and stale
    /// callbacks. Called during dismiss and before installing new observers.
    static func remove(_ tokens: inout [NSObjectProtocol]) {
        for token in tokens {
            // Try removing from both centers; harmless if not registered
            NSWorkspace.shared.notificationCenter.removeObserver(token)
            NotificationCenter.default.removeObserver(token)
        }
        tokens.removeAll()
    }
}
