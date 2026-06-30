//
//  GeneralSettingsSection.swift
//  hyperwhisper
//
//  Extracted general/application settings section. Using the shared
//  SettingsSection/SettingsCard helpers keeps sizing consistent
//  across every tab in the settings view.
//

import SwiftUI
import AppKit

struct GeneralSettingsSection: View {
    @EnvironmentObject var settingsManager: SettingsManager
    @State private var launchAtLoginEnabled = false
    @State private var ignoreNextLaunchAtLoginChange = false

    var body: some View {
        SettingsSection(title: "settings.section.general") {
            applicationBehaviourCard

            loggingAndUpdatesCard

            updatesAndSupportCard

            versionRow
        }
    }

    // MARK: - Cards

    private var applicationBehaviourCard: some View {
        SettingsCard(horizontalPadding: 8) {
            VStack(spacing: 0) {
                // LAUNCH AT LOGIN
                // Routes through LaunchAtLoginManager (native SMAppService wrapper)
                // instead of the LaunchAtLogin package's computed Binding(get:set:),
                // which infinite-recurses through SerialExecutor.isMainExecutor.getter
                // on macOS 26.2 (Sentry HYPERWHISPER-3V).
                SettingsToggleRow(
                    title: "settings.general.launchAtLogin.title",
                    subtitle: nil,
                    info: "settings.general.launchAtLogin.info",
                    isOn: $launchAtLoginEnabled,
                    standalone: false
                )
                .onAppear {
                    launchAtLoginEnabled = LaunchAtLoginManager.isEnabled
                }
                .onChange(of: launchAtLoginEnabled) { _, newValue in
                    if ignoreNextLaunchAtLoginChange {
                        ignoreNextLaunchAtLoginChange = false
                        return
                    }
                    LaunchAtLoginManager.setEnabled(newValue)
                    // Resync from SMAppService — the system may reject the
                    // change (user denied approval, app unsigned, etc.) and
                    // setEnabled only logs the error. Without this, the
                    // toggle keeps the unapplied value (#286 review P2).
                    let actual = LaunchAtLoginManager.isEnabled
                    if actual != newValue {
                        ignoreNextLaunchAtLoginChange = true
                        launchAtLoginEnabled = actual
                    }
                }

                Divider()

                SettingsToggleRow(
                    title: "settings.general.launchMinimized.title",
                    subtitle: nil,
                    info: "settings.general.launchMinimized.info",
                    isOn: $settingsManager.launchMinimized,
                    standalone: false
                )

                Divider()

                SettingsToggleRow(
                    title: "settings.general.showInDock.title",
                    subtitle: nil,
                    info: "settings.general.showInDock.info",
                    isOn: $settingsManager.showInDock,
                    standalone: false
                )

                Divider()

                SettingsToggleRow(
                    title: "settings.general.showRecordingWindow.title",
                    subtitle: nil,
                    info: "settings.general.showRecordingWindow.info",
                    isOn: $settingsManager.showRecordingWindow,
                    standalone: false
                )
            }
        }
    }

    private var loggingAndUpdatesCard: some View {
        SettingsCard(horizontalPadding: 8) {
            VStack(spacing: 0) {
                SettingsToggleRow(
                    title: "settings.general.errorLogging.title",
                    subtitle: nil,
                    info: "settings.general.errorLogging.info",
                    isOn: $settingsManager.enableErrorLogging,
                    standalone: false
                )

                Divider()

                SettingsToggleRow(
                    title: "settings.general.autoUpdate.title",
                    subtitle: nil,
                    info: "settings.general.autoUpdate.info",
                    isOn: $settingsManager.checkForUpdatesAutomatically,
                    standalone: false
                )
            }
        }
    }

    private var updatesAndSupportCard: some View {
        SettingsCard(horizontalPadding: 8) {
            VStack(spacing: 0) {
                SettingsActionRow(
                    title: "settings.general.support.title",
                    subtitle: nil,
                    buttonTitle: "menu.command.contact.support",
                    standalone: false,
                    action: {
                        if let url = URL(string: "https://www.hyperwhisper.com/support") {
                            NSWorkspace.shared.open(url)
                        }
                    }
                )
            }
        }
    }

    // MARK: - Rows

    private var versionRow: some View {
        HStack {
            let shortVersion = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "Unknown"
            let buildVersion = Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String ?? "Unknown"

            // DEVELOPMENT MODE INDICATOR:
            // Shows "(Development)" after the build number when running in DEBUG mode
            // This helps distinguish development builds from production releases
            #if DEBUG
            let versionText = "settings.version.detail".localized(arguments: shortVersion, buildVersion) + " (Development)"
            #else
            let versionText = "settings.version.detail".localized(arguments: shortVersion, buildVersion)
            #endif

            Text(versionText)
                .font(.caption)
                .foregroundColor(.secondary)
            Spacer()
        }
    }

}
