import AppKit

/// Shows an `NSAlert` as a free-standing window that does not block the main thread.
///
/// **Why not `runModal()` (issue #1539):**
/// `runModal()` spins a nested modal run loop inside the main-actor job that called it,
/// and that job does not return until the user clicks OK. Everything else that needs the
/// main actor waits behind it — including every Local API route (`LocalAPIServer` is
/// `@MainActor`), so even `/health` stops answering while the alert sits on screen.
///
/// **Why not `beginSheetModal(for:)`:**
/// A sheet needs a visible host window, and a menu-bar app often has none when a
/// background job fails.
///
/// Instead the alert's own panel is ordered front as a normal window, and each button is
/// rewired to close it. `show` returns immediately; the completion handler, if any, runs
/// on the main actor with the clicked button's response when the user dismisses it.
@MainActor
final class ModelessAlert: NSObject {
    /// Live presenters. AppKit does not retain a button's target, so this keeps each
    /// presenter (and its alert) alive until the user dismisses it.
    private static var open: Set<ModelessAlert> = []

    private let alert: NSAlert
    private let completion: ((NSApplication.ModalResponse) -> Void)?

    private init(alert: NSAlert, completion: ((NSApplication.ModalResponse) -> Void)?) {
        self.alert = alert
        self.completion = completion
    }

    /// Presents `alert` without a modal run loop and returns at once.
    ///
    /// - Parameters:
    ///   - alert: A configured alert with at least one button added.
    ///   - completion: Called with `.alertFirstButtonReturn`, `.alertSecondButtonReturn`, …
    ///     for the button the user clicked.
    static func show(_ alert: NSAlert, completion: ((NSApplication.ModalResponse) -> Void)? = nil) {
        if alert.buttons.isEmpty {
            alert.addButton(withTitle: "common.ok".localized)
        }

        let presenter = ModelessAlert(alert: alert, completion: completion)
        open.insert(presenter)

        for (index, button) in alert.buttons.enumerated() {
            button.tag = NSApplication.ModalResponse.alertFirstButtonReturn.rawValue + index
            button.target = presenter
            button.action = #selector(buttonClicked(_:))
        }

        alert.layout()
        let window = alert.window
        window.isReleasedWhenClosed = false
        window.level = .modalPanel
        window.center()

        // Like the file picker, this is often reached while another app is frontmost.
        NSApp.activate(ignoringOtherApps: true)
        window.makeKeyAndOrderFront(nil)
    }

    @objc private func buttonClicked(_ sender: NSButton) {
        alert.window.orderOut(nil)
        Self.open.remove(self)
        completion?(NSApplication.ModalResponse(rawValue: sender.tag))
    }
}
