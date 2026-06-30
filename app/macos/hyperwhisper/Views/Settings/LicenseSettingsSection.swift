//
//  LicenseSettingsSection.swift
//  hyperwhisper
//
//  Presents the licensing UI with a fixed-width column so it never
//  stretches the settings split-view sidebar.
//

import SwiftUI
import AppKit

struct LicenseSettingsSection: View {
    @EnvironmentObject var licenseManager: LicenseManager

    @State private var licenseKeyInput: String = ""
    @State private var showLicenseSuccess = false
    @State private var showLicenseError = false

    var body: some View {
        SettingsSection(title: "settings.section.license", maxWidth: SettingsLayout.cardWidth) {
            licenseStatusCard

            if licenseManager.licenseStatus == .trial {
                trialUsageCard
            }

            if let error = licenseManager.lastError {
                licenseErrorBanner(message: error)
            }
        }
        .alert(licenseManager.licenseStatus == .active ? LocalizedStringKey("alerts.license.activated.title") : LocalizedStringKey("alerts.license.deactivated.title"), isPresented: $showLicenseSuccess) {
            Button(LocalizedStringKey("common.ok")) { }
        } message: {
            if licenseManager.licenseStatus == .active {
                Text(LocalizedStringKey("alerts.license.activated.message"))
            } else {
                Text(LocalizedStringKey("alerts.license.deactivated.message"))
            }
        }
        .alert(LocalizedStringKey("alerts.license.failed.title"), isPresented: $showLicenseError) {
            Button(LocalizedStringKey("common.ok")) { }
        } message: {
            Text(licenseManager.lastError ?? "alerts.license.failed.message".localized)
        }
    }

    // MARK: - Cards

    private var licenseStatusCard: some View {
        SettingsCard(horizontalPadding: 8, maxWidth: SettingsLayout.cardWidth) {
            VStack(spacing: 0) {
                statusRow

                if licenseManager.licenseStatus != .active {
                    Divider()
                    licenseEntryBlock
                    Divider()
                    recoveryRow
                }
            }
        }
    }

    private var trialUsageCard: some View {
        SettingsCard(horizontalPadding: 8, maxWidth: SettingsLayout.cardWidth) {
            VStack(spacing: 0) {
                trialDailyUsageRow
                Divider()
                modelLimitRow
                Divider()
                upgradeRow
            }
        }
    }

    // MARK: - Row Builders

    private var statusRow: some View {
        let statusTitle = licenseManager.licenseStatus.localizedTitle

        return HStack(alignment: .center, spacing: 12) {
            VStack(alignment: .leading, spacing: 4) {
                HStack(spacing: 8) {
                    Circle()
                        .fill(licenseManager.licenseStatus.color)
                        .frame(width: 10, height: 10)
                    Text(String(format: "license.status.label".localized, statusTitle))
                        .font(.headline)
                }
                Text(licenseManager.licenseStatusDescription)
                    .font(.caption)
                    .foregroundColor(.secondary)
            }
            Spacer()
            if licenseManager.licenseStatus == .active {
                HStack(spacing: 8) {
                    Button(LocalizedStringKey(licenseManager.isDeactivating ? "license.button.deactivating" : "license.button.deactivate")) {
                        Task {
                            let success = await licenseManager.deactivateLicense()
                            if success {
                                showLicenseSuccess = true
                            } else {
                                showLicenseError = true
                            }
                        }
                    }
                    .buttonStyle(.bordered)
                    .disabled(licenseManager.isDeactivating)

                    Button(LocalizedStringKey("license.button.manageBilling")) {
                        licenseManager.openCustomerPortal()
                    }
                    .buttonStyle(.bordered)
                }
            }
        }
        .padding(10)
    }

    private var licenseEntryBlock: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text(LocalizedStringKey("settings.license.key.title"))
                .font(.headline)
            licenseKeyField()
                .frame(maxWidth: .infinity)
            HStack(spacing: 12) {
                Button(action: {
                    if licenseKeyInput.isEmpty {
                        // Paste from clipboard when empty
                        if let clipboardContent = NSPasteboard.general.string(forType: .string) {
                            licenseKeyInput = clipboardContent.trimmingCharacters(in: .whitespacesAndNewlines).uppercased()
                        }
                    } else {
                        // Clear when not empty
                        licenseKeyInput = ""
                    }
                }) {
                    if licenseKeyInput.isEmpty {
                        Label("settings.license.pasteFromClipboard", systemImage: "doc.on.clipboard")
                    } else {
                        Label("settings.license.clearInput", systemImage: "xmark.circle")
                    }
                }
                .buttonStyle(.bordered)

                activateLicenseButton
                Spacer()
            }
        }
        .padding(12)
    }

    private var recoveryRow: some View {
        HStack {
            Image(systemName: "questionmark.circle")
                .foregroundColor(.secondary)
            Text(LocalizedStringKey("settings.license.recovery.prompt"))
                .font(.caption)
                .foregroundColor(.secondary)
            Spacer()
            Button(LocalizedStringKey("settings.license.recovery.button")) {
                if let url = URL(string: "https://www.hyperwhisper.com/user") {
                    NSWorkspace.shared.open(url)
                }
            }
            .buttonStyle(.bordered)
        }
        .padding(12)
    }

    private var trialDailyUsageRow: some View {
        HStack(alignment: .center, spacing: 12) {
            VStack(alignment: .leading, spacing: 4) {
                Text(LocalizedStringKey("settings.license.daily.title"))
                    .font(.headline)
                Text(String(format: "settings.license.daily.usage".localized,
                             formatTime(licenseManager.dailyUsageSeconds),
                             formatTime(licenseManager.trialDailyTranscriptionLimit)))
                    .font(.caption)
                    .foregroundColor(.secondary)
            }
            Spacer()
            if licenseManager.isDailyLimitReached {
                Label(LocalizedStringKey("settings.license.limitReached"), systemImage: "exclamationmark.triangle.fill")
                    .foregroundColor(.orange)
                    .font(.caption)
            }
        }
        .padding(10)
    }

    private var modelLimitRow: some View {
        HStack(alignment: .center, spacing: 12) {
            VStack(alignment: .leading, spacing: 4) {
                Text(LocalizedStringKey("settings.license.models.title"))
                    .font(.headline)
                Text(String(format:
                                (licenseManager.trialModelDownloadLimit == 1
                                 ? "settings.license.models.progress.one"
                                 : "settings.license.models.progress").localized,
                                licenseManager.modelsDownloaded,
                                licenseManager.trialModelDownloadLimit))
                    .font(.caption)
                    .foregroundColor(.secondary)
            }
            Spacer()
            if licenseManager.isModelLimitReached {
                Label(LocalizedStringKey("settings.license.limitReached"), systemImage: "exclamationmark.triangle.fill")
                    .foregroundColor(.orange)
                    .font(.caption)
            }
        }
        .padding(10)
    }

    private var upgradeRow: some View {
        HStack {
            Image(systemName: "sparkles")
                .foregroundColor(.accentColor)
            Text(LocalizedStringKey("settings.license.upgrade.text"))
                .font(.caption)
            Spacer()
            Button(LocalizedStringKey("settings.license.upgrade.button")) {
                licenseManager.openPurchasePage()
            }
            .buttonStyle(.borderedProminent)
        }
        .padding(10)
    }

    private func licenseErrorBanner(message: String) -> some View {
        HStack(spacing: 8) {
            Image(systemName: "exclamationmark.triangle")
                .foregroundColor(.red)
                .font(.caption)
            Text(message)
                .font(.caption)
                .foregroundColor(.red)
                .lineLimit(2)
                .fixedSize(horizontal: false, vertical: true)
            Spacer()
        }
        .padding(.horizontal, 4)
        .frame(maxWidth: SettingsLayout.cardWidth, alignment: .leading)
    }

    // MARK: - Helpers

    @ViewBuilder
    private func licenseKeyField(minWidth: CGFloat? = nil, maxWidth: CGFloat? = nil) -> some View {
        TextField(LocalizedStringKey("settings.license.placeholder"), text: $licenseKeyInput)
            .textFieldStyle(.roundedBorder)
            .font(.system(.body, design: .monospaced))
            .frame(minWidth: minWidth, maxWidth: maxWidth)
            .layoutPriority(1)
            .contextMenu {
                Button("Paste") {
                    if let clipboardContent = NSPasteboard.general.string(forType: .string) {
                        licenseKeyInput = clipboardContent
                    }
                }
                .keyboardShortcut("v", modifiers: .command)
            }
    }

    private var activateLicenseButton: some View {
        let titleKey = licenseManager.isValidating ? "license.button.activating" : "license.button.activate"
        return Button(LocalizedStringKey(titleKey)) {
            Task {
                let result = await licenseManager.activateLicense(licenseKeyInput)
                if result.isValid {
                    showLicenseSuccess = true
                    licenseKeyInput = ""
                } else {
                    showLicenseError = true
                }
            }
        }
        .buttonStyle(.borderedProminent)
        .disabled(licenseKeyInput.isEmpty || licenseManager.isValidating)
    }

    private func formatTime(_ seconds: Int) -> String {
        let minutes = seconds / 60
        let remainingSeconds = seconds % 60
        return String(format: "%d:%02d", minutes, remainingSeconds)
    }
}
