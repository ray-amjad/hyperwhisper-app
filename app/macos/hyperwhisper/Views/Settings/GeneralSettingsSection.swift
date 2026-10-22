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
    /// Bumped on every user toggle, so a read or write that finishes after a
    /// newer toggle does not overwrite the newer state.
    @State private var launchAtLoginRequest = 0

    var body: some View {
        SettingsSection(title: "settings.section.general") {
            applicationBehaviourCard

            loggingAndUpdatesCard

            updatesAndSupportCard

            versionRow
        }
    }

    // MARK: - Launch at login

    /// Shows the login item's real state on the toggle, unless the user has
    /// toggled again since `request` began. Sets the ignore flag only when the
    /// value actually changes — onChange does not fire otherwise, and a flag
    /// left set would swallow the user's next click.
    private func applyLaunchAtLoginState(_ actual: Bool, ifStillRequest request: Int) {
        guard request == launchAtLoginRequest, actual != launchAtLoginEnabled else { return }
        ignoreNextLaunchAtLoginChange = true
        launchAtLoginEnabled = actual
    }

    // MARK: - Cards

    private var applicationBehaviourCard: some View {
        SettingsCard(horizontalPadding: 8) {
            VStack(spacing: 0) {
                // LAUNCH AT LOGIN
                // Routes through LaunchAtLoginManager (native login-item wrapper)
                // instead of the LaunchAtLogin package's computed Binding(get:set:),
                // which infinite-recurses through SerialExecutor.isMainExecutor.getter
                // on macOS 26.2 (Sentry HYPERWHISPER-3V).
                //
                // Every read and write is awaited off the main thread: each is a
                // blocking XPC call that froze this page on appear (#853, Sentry
                // HYPERWHISPER-SY). The toggle fills in a moment after the page draws.
                SettingsToggleRow(
                    title: "settings.general.launchAtLogin.title",
                    subtitle: nil,
                    info: "settings.general.launchAtLogin.info",
                    isOn: $launchAtLoginEnabled,
                    standalone: false
                )
                .task {
                    // Re-read on every appear: the user can change the login
                    // item in System Settings while the app runs.
                    let request = launchAtLoginRequest
                    let actual = await settingsManager.refreshLaunchAtLogin()
                    applyLaunchAtLoginState(actual, ifStillRequest: request)
                }
                .onChange(of: launchAtLoginEnabled) { _, newValue in
                    if ignoreNextLaunchAtLoginChange {
                        ignoreNextLaunchAtLoginChange = false
                        return
                    }
                    launchAtLoginRequest += 1
                    let request = launchAtLoginRequest
                    Task {
                        // Resync from the returned state — the system may
                        // reject the change (user denied approval, app
                        // unsigned, etc.) and setLaunchAtLogin only logs the
                        // error. Without this, the toggle keeps the unapplied
                        // value (#286 review P2).
                        let actual = await settingsManager.setLaunchAtLogin(newValue)
                        applyLaunchAtLoginState(actual, ifStillRequest: request)
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

                // ANONYMOUS SPEED DATA
                // Sharing is the default. Turning this off makes every cloud
                // transcription send `X-Latency-Opt-Out: 1` (see LatencyOptOut),
                // and the server drops the measurement instead of storing it.
                //
                // The comparison page is the pay-off for the toggle, so it sits
                // on the title line and stays reachable whether or not sharing
                // is on — someone who opted out can still read it, and seeing
                // what it produces is the fairest way to present the choice.
                SettingsToggleRow(
                    title: "settings.general.shareSpeedData.title",
                    subtitle: nil,
                    info: "settings.general.shareSpeedData.info",
                    isOn: $settingsManager.shareAnonymousSpeedData,
                    standalone: false,
                    titleLink: URL(string: "https://www.hyperwhisper.com/en/latency").map {
                        SettingsRowLink(url: $0, label: "settings.general.shareSpeedData.link")
                    }
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
