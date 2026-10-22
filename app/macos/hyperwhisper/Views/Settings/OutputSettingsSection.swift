//
//  OutputSettingsSection.swift
//  hyperwhisper
//

import SwiftUI
import AppKit

struct OutputSettingsSection: View {
    @EnvironmentObject var settingsManager: SettingsManager

    var body: some View {
        SettingsSection(title: "settings.section.output") {
            outputCard
            SettingsGroupHeader(title: "settings.output.clipboard.header")
            clipboardCard
        }
    }

    private var outputCard: some View {
        SettingsCard(horizontalPadding: 8) {
            VStack(spacing: 0) {
                SettingsToggleRow(
                    title: "settings.output.paste.title",
                    subtitle: "settings.output.paste.subtitle",
                    info: "settings.output.paste.info",
                    isOn: $settingsManager.pasteResultText,
                    standalone: false
                )

                Divider()
                SettingsToggleRow(
                    title: "settings.output.removeFillerWords.title",
                    subtitle: "settings.output.removeFillerWords.subtitle",
                    info: "settings.output.removeFillerWords.info",
                    isOn: $settingsManager.removeFillerWords,
                    standalone: false
                )

                Divider()
                SettingsToggleRow(
                    title: "settings.output.autocapitalizeInsert.title",
                    subtitle: "settings.output.autocapitalizeInsert.subtitle",
                    info: "settings.output.autocapitalizeInsert.info",
                    isOn: autocapitalizeInsertBinding,
                    standalone: false
                )
            }
        }
    }

    private var clipboardCard: some View {
        SettingsCard(horizontalPadding: 8) {
            VStack(spacing: 0) {
                SettingsToggleRow(
                    title: "settings.output.restoreClipboard.title",
                    subtitle: "settings.output.restoreClipboard.subtitle",
                    info: "settings.output.restoreClipboard.info",
                    isOn: $settingsManager.restoreClipboardAfterPaste,
                    standalone: false
                )

                if settingsManager.restoreClipboardAfterPaste {
                    Divider()
                    HStack {
                        Image(systemName: "clock")
                            .foregroundColor(.secondary)
                        Text(LocalizedStringKey("settings.output.restore.after"))
                        Spacer()
                        Stepper(value: $settingsManager.clipboardRestoreDelaySeconds, in: 1...60, step: 1) {
                            Text("settings.output.restore.seconds".localized(arguments: Int(settingsManager.clipboardRestoreDelaySeconds)))
                                .monospacedDigit()
                        }
                        .fixedSize()
                    }
                    .padding(10)
                    .accessibilityLabel("settings.output.restore.accessibility".localized)
                }

                Divider()
                SettingsToggleRow(
                    title: "settings.output.hideClipboardHistory.title",
                    subtitle: "settings.output.hideClipboardHistory.subtitle",
                    info: "settings.output.hideClipboardHistory.info",
                    isOn: $settingsManager.hideFromClipboardHistory,
                    standalone: false
                )
            }
        }
    }

    /// Binding that gates ON-transitions on Accessibility permission. If the
    /// user enables the toggle without AX granted, show the standard alert and
    /// revert the toggle if they cancel.
    private var autocapitalizeInsertBinding: Binding<Bool> {
        Binding(
            get: { settingsManager.autocapitalizeInsert },
            set: { newValue in
                if newValue && !AccessibilityHelper.shared.hasAccessibilityPermission() {
                    // The toggle stays OFF until the user answers.
                    presentAutocapitalizeAccessibilityAlert { granted in
                        // Open Settings — turn the toggle ON; the cursor probe
                        // silently no-ops until permission lands. Cancel — leave it OFF.
                        settingsManager.autocapitalizeInsert = granted
                    }
                } else {
                    settingsManager.autocapitalizeInsert = newValue
                }
            }
        )
    }

    /// Calls `completion` with true if the user clicked "Open Settings", false if cancelled.
    ///
    /// Modeless, not runModal (issue #1734): runModal held the main actor until the
    /// click, and the Local API (@MainActor) answered nothing meanwhile.
    private func presentAutocapitalizeAccessibilityAlert(completion: @escaping (Bool) -> Void) {
        let alert = NSAlert()
        alert.messageText = "audio.alert.accessibility.title".localized
        alert.informativeText = "settings.output.autocapitalizeInsert.permissionMessage".localized
        alert.addButton(withTitle: "audio.alert.accessibility.open".localized)
        alert.addButton(withTitle: "common.cancel".localized)

        ModelessAlert.show(alert) { response in
            let granted = response == .alertFirstButtonReturn
            if granted {
                AccessibilityHelper.shared.openAccessibilitySettings()
            }
            completion(granted)
        }
    }
}
