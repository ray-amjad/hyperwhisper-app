//
//  NormalizedShortcutRecorder.swift
//  hyperwhisper
//
//  Ensures the recorder display uses readable labels (Esc/Return/etc.)
//  even when the underlying KeyboardShortcuts library returns glyphs.
//

import SwiftUI
import KeyboardShortcuts

/// SwiftUI wrapper around `KeyboardShortcuts.RecorderCocoa` that normalizes the rendered shortcut text.
struct NormalizedShortcutRecorder: View {
    let name: KeyboardShortcuts.Name
    var onChange: ((KeyboardShortcuts.Shortcut?) -> Void)? = nil

    var body: some View {
        NormalizedShortcutRecorderRepresentable(name: name, onChange: onChange)
    }
}

private struct NormalizedShortcutRecorderRepresentable: NSViewRepresentable {
    let name: KeyboardShortcuts.Name
    var onChange: ((KeyboardShortcuts.Shortcut?) -> Void)?

    func makeCoordinator() -> Coordinator {
        Coordinator(name: name, onChange: onChange)
    }

    func makeNSView(context: Context) -> KeyboardShortcuts.RecorderCocoa {
        let recorder = KeyboardShortcuts.RecorderCocoa(for: name) { shortcut in
            context.coordinator.handleShortcutChange(shortcut)
        }
        context.coordinator.attach(recorder: recorder)
        return recorder
    }

    func updateNSView(_ nsView: KeyboardShortcuts.RecorderCocoa, context: Context) {
        Task { @MainActor in
            context.coordinator.refreshDisplay()
        }
    }
}

private extension NormalizedShortcutRecorderRepresentable {
    final class Coordinator {
        let name: KeyboardShortcuts.Name
        var recorder: KeyboardShortcuts.RecorderCocoa?
        var onChange: ((KeyboardShortcuts.Shortcut?) -> Void)?
        private var observers: [NSObjectProtocol] = []

        init(name: KeyboardShortcuts.Name, onChange: ((KeyboardShortcuts.Shortcut?) -> Void)?) {
            self.name = name
            self.onChange = onChange
        }

        deinit {
            observers.forEach { NotificationCenter.default.removeObserver($0) }
        }

        func attach(recorder: KeyboardShortcuts.RecorderCocoa) {
            self.recorder = recorder
            Task { @MainActor in
                refreshDisplay()
            }

            // Note: KeyboardShortcuts library's internal notification is not accessible
            // We rely on the app's custom .shortcutDidChange notification instead
            // which is posted whenever shortcuts are modified

            let appNotificationObserver = NotificationCenter.default.addObserver(
                forName: .shortcutDidChange,
                object: nil,
                queue: .main
            ) { [weak self] _ in
                guard let self else { return }
                Task { @MainActor in
                    self.refreshDisplay()
                }
            }
            observers.append(appNotificationObserver)
        }

        func handleShortcutChange(_ shortcut: KeyboardShortcuts.Shortcut?) {
            onChange?(shortcut)
            // Defer normalization until the recorder finishes updating its internal state.
            Task { @MainActor [weak self] in
                self?.refreshDisplay()
            }
        }

        @MainActor
        func refreshDisplay() {
            guard
                let recorder,
                recorder.currentEditor() == nil  // Avoid overriding while the user is actively recording.
            else {
                return
            }

            let description = KeyboardShortcuts.getShortcut(for: name)?.description ?? ""
            let normalized = Self.normalizeShortcutText(description)

            if recorder.stringValue != normalized {
                recorder.stringValue = normalized
            }
        }

        private static func normalizeShortcutText(_ text: String) -> String {
            guard !text.isEmpty else {
                return text
            }

            var value = text.trimmingCharacters(in: .whitespacesAndNewlines)

            let escapeLabel = "keyboard.escape".localized
            let returnLabel = "keyboard.return".localized
            let spaceLabel = "keyboard.space".localized

            value = value.replacingOccurrences(of: "⎋", with: escapeLabel)
            value = value.replacingOccurrences(of: "Escape", with: escapeLabel)
            value = value.replacingOccurrences(of: "↩︎", with: returnLabel)
            value = value.replacingOccurrences(of: "↩", with: returnLabel)
            value = value.replacingOccurrences(of: "Enter", with: returnLabel)
            value = value.replacingOccurrences(of: "Space", with: spaceLabel)

            return value
        }
    }
}
