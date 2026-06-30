//
//  StorageSettingsSection.swift
//  hyperwhisper
//
//  Manages the recordings directory controls and auto-delete settings.
//

import SwiftUI
import AppKit

struct StorageSettingsSection: View {
    @EnvironmentObject var settingsManager: SettingsManager

    /// Local state for the auto-delete duration value (for text field binding)
    @State private var autoDeleteValueText: String = ""

    /// Current time for countdown calculation (updates every second)
    @State private var currentTime = Date()

    /// Timer for updating countdown display
    private let countdownTimer = Timer.publish(every: 1, on: .main, in: .common).autoconnect()

    var body: some View {
        SettingsSection(title: "settings.section.storage") {
            // RECORDING LOCATION CARD:
            // Groups folder selection and "Show in Finder" action together
            SettingsCard(horizontalPadding: 8, maxWidth: SettingsLayout.cardWidth) {
                VStack(spacing: 0) {
                    SettingsFolderRow(
                        title: "settings.storage.recordings.title",
                        info: nil,
                        folderPath: $settingsManager.recordingsFolder,
                        standalone: false,
                        onFolderChange: { url in
                            settingsManager.changeRecordingsFolder(to: url)
                        }
                    )

                    Divider()

                    SettingsActionRow(
                        title: "settings.storage.showFolder.title",
                        buttonTitle: "settings.storage.showFolder.button",
                        standalone: false,
                        action: {
                            NSWorkspace.shared.selectFile(nil, inFileViewerRootedAtPath: settingsManager.recordingsFolder)
                        }
                    )
                }
            }

            // WARNING NOTICE:
            // Placed directly under folder settings to explain that changes only affect new recordings
            HStack(spacing: 8) {
                Image(systemName: "info.circle")
                    .foregroundColor(.secondary)
                    .font(.caption)
                Text("settings.storage.notice".localized)
                    .font(.caption)
                    .foregroundColor(.secondary)
                Spacer()
            }
            .padding(.horizontal, 4)

            // COMPRESSION SETTINGS:
            // Separate row for audio compression options to visually distinguish
            // recording location settings from audio format/compression settings
            // Using standalone: true applies its own card background styling
            SettingsToggleRow(
                title: "settings.storage.storeAsM4A.title",
                subtitle: "settings.storage.storeAsM4A.subtitle",
                isOn: $settingsManager.storeAsM4A,
                standalone: true
            )
            .frame(maxWidth: SettingsLayout.cardWidth)

            // AUTO-DELETE SECTION:
            // Allows users to configure automatic deletion of old recordings
            autoDeleteSection
        }
        .onAppear {
            autoDeleteValueText = String(settingsManager.autoDelete.autoDeleteValue)
        }
    }

    // MARK: - Auto-Delete Section

    /// Inline auto-delete configuration section
    /// Contains enable toggle, duration settings, and warning about permanent deletion
    private var autoDeleteSection: some View {
        SettingsCard(horizontalPadding: 8, maxWidth: SettingsLayout.cardWidth) {
            VStack(spacing: 0) {
                // ENABLE TOGGLE:
                // Master switch to enable/disable automatic deletion
                HStack(spacing: 12) {
                    VStack(alignment: .leading, spacing: 2) {
                        Text(localized: "history.autoDelete.enable.title")
                            .font(.headline)
                        Text(localized: "history.autoDelete.enable.subtitle")
                            .font(.caption)
                            .foregroundColor(.secondary)
                    }

                    Spacer()

                    Toggle("", isOn: $settingsManager.autoDelete.autoDeleteEnabled)
                        .toggleStyle(.switch)
                        .labelsHidden()
                }
                .padding(10)

                // DURATION CONFIGURATION:
                // Only shown when auto-delete is enabled
                if settingsManager.autoDelete.autoDeleteEnabled {
                    Divider()

                    HStack(spacing: 12) {
                        Text(localized: "history.autoDelete.duration.prefix")
                            .foregroundColor(.secondary)

                        // Value text field
                        TextField("30", text: $autoDeleteValueText)
                            .textFieldStyle(.roundedBorder)
                            .frame(width: 60)
                            .multilineTextAlignment(.center)
                            .onChange(of: autoDeleteValueText) { newValue in
                                // Filter to only allow numbers
                                let filtered = newValue.filter { $0.isNumber }
                                if filtered != newValue {
                                    autoDeleteValueText = filtered
                                }
                                // Update settings if valid number
                                if let intValue = Int(filtered), intValue > 0 {
                                    settingsManager.autoDelete.setAutoDeleteValue(intValue)
                                }
                            }
                            .onSubmit {
                                // Ensure minimum value of 1
                                if let intValue = Int(autoDeleteValueText) {
                                    let validValue = max(1, intValue)
                                    settingsManager.autoDelete.setAutoDeleteValue(validValue)
                                    autoDeleteValueText = String(validValue)
                                } else {
                                    // Reset to current value if invalid
                                    autoDeleteValueText = String(settingsManager.autoDelete.autoDeleteValue)
                                }
                            }

                        // Time unit picker
                        Picker("", selection: $settingsManager.autoDelete.autoDeleteTimeUnit) {
                            ForEach(AutoDeleteTimeUnit.allCases, id: \.self) { unit in
                                Text(unit.localizedName).tag(unit)
                            }
                        }
                        .pickerStyle(.menu)
                        .frame(width: 100)
                        .labelsHidden()

                        Spacer()
                    }
                    .padding(10)

                    // NEXT CLEANUP COUNTDOWN:
                    // Shows when the next automatic cleanup will run
                    if let nextCleanup = settingsManager.autoDelete.cleanupService?.nextCleanupDate {
                        Divider()

                        HStack(spacing: 8) {
                            Image(systemName: "clock")
                                .font(.caption)
                                .foregroundColor(.secondary)
                            Text("Next check: \(countdownString(to: nextCleanup))")
                                .font(.caption)
                                .foregroundColor(.secondary)
                                .monospacedDigit()
                            Spacer()
                        }
                        .padding(10)
                        .onReceive(countdownTimer) { _ in
                            currentTime = Date()
                        }
                    }
                }
            }
        }
    }

    // MARK: - Countdown Formatting

    /// Formats the time remaining until the next cleanup as a countdown string
    /// - Parameter date: The target date for the countdown
    /// - Returns: A formatted string like "45s", "2m 30s", or "1h 5m"
    private func countdownString(to date: Date) -> String {
        let remaining = date.timeIntervalSince(currentTime)

        if remaining <= 0 {
            return "now"
        }

        let seconds = Int(remaining) % 60
        let minutes = (Int(remaining) / 60) % 60
        let hours = Int(remaining) / 3600

        if hours > 0 {
            return "\(hours)h \(minutes)m"
        } else if minutes > 0 {
            return "\(minutes)m \(seconds)s"
        } else {
            return "\(seconds)s"
        }
    }
}

// MARK: - Folder Row

private struct SettingsFolderRow: View {
    let title: LocalizedStringKey
    let info: LocalizedStringKey?
    @Binding var folderPath: String
    var standalone: Bool
    var onFolderChange: (URL) -> Void

    @State private var showingInfo = false

    var body: some View {
        HStack(spacing: 12) {
            VStack(alignment: .leading, spacing: 2) {
                Text(title)
                    .font(.headline)
                if let info {
                    Text(info)
                        .font(.caption)
                        .foregroundColor(.secondary)
                }
            }

            Spacer(minLength: 12)

            Text(truncatedPath)
                .font(.system(.body, design: .monospaced))
                .foregroundColor(.secondary)
                .lineLimit(1)
                .truncationMode(.middle)
                .frame(maxWidth: 300, alignment: .trailing)

            Button(LocalizedStringKey("settings.storage.choose.button")) {
                selectFolder()
            }

            if let info {
                Button(action: { showingInfo.toggle() }) {
                    Image(systemName: "info.circle")
                        .foregroundColor(.secondary)
                        .font(.system(size: 13))
                }
                .buttonStyle(.plain)
                .popover(isPresented: $showingInfo, arrowEdge: .trailing) {
                    Text(info)
                        .font(.caption)
                        .foregroundColor(.primary)
                        .lineSpacing(2)
                        .multilineTextAlignment(.leading)
                        .padding(12)
                        .frame(maxWidth: 280, alignment: .leading)
                        .fixedSize()
                }
            }
        }
        .padding(10)
        .applyConditionalBackground(standalone: standalone)
    }

    private var truncatedPath: String {
        let path = folderPath
        let homeDir = FileManager.default.homeDirectoryForCurrentUser.path
        if path.hasPrefix(homeDir) {
            return path.replacingOccurrences(of: homeDir, with: "~")
        }
        return path
    }

    /// Shows folder selection dialog asynchronously to prevent UI freezing
    ///
    /// ASYNC PATTERN:
    /// - Uses panel.begin() instead of panel.runModal() to avoid blocking the main thread
    /// - The panel.runModal() synchronous call can make the UI appear frozen/hung
    /// - panel.begin() presents the dialog and returns immediately
    /// - The completion handler is called when the user makes a selection
    ///
    /// This ensures the app remains responsive while the file picker is open
    private func selectFolder() {
        let panel = NSOpenPanel()
        panel.canChooseFiles = false
        panel.canChooseDirectories = true
        panel.canCreateDirectories = true
        panel.allowsMultipleSelection = false
        panel.prompt = "settings.storage.dialog.select".localized
        panel.message = "settings.storage.dialog.message".localized
        panel.directoryURL = URL(fileURLWithPath: folderPath)

        // ASYNC PRESENTATION:
        // Use begin() instead of runModal() to prevent blocking the main thread
        // This keeps the UI responsive while the panel is open
        panel.begin { response in
            // COMPLETION HANDLER:
            // Called when user clicks "Select" (.OK) or "Cancel" (.cancel)
            if response == .OK, let url = panel.url {
                onFolderChange(url)
            }
        }
    }
}
