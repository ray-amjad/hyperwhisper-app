//
//  SettingsView.swift
//  hyperwhisper
//

import SwiftUI

struct SettingsView: View {
    @EnvironmentObject var appState: AppState
    @EnvironmentObject var licenseManager: LicenseManager

    var body: some View {
        HStack(spacing: 0) {
            // Fixed-width sidebar
            sectionsList
                .frame(width: 200)
                .background(Color(NSColor.controlBackgroundColor))

            Divider()

            // Main content area
            ScrollView {
                VStack(alignment: .leading, spacing: 24) {
                    sectionView(for: appState.selectedSettingsSection)
                }
                .frame(maxWidth: SettingsLayout.contentWidth, alignment: .leading)
                .padding(.vertical, 32)
                .padding(.horizontal, 24)
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            .frame(maxWidth: .infinity)
        }
        .background(Color(NSColor.windowBackgroundColor))
        .navigationTitle("common.settings".localized)
    }

    private var sectionsList: some View {
        List(selection: $appState.selectedSettingsSection) {
            Label("settings.section.general".localized, systemImage: "gearshape")
                .tag("general")

            Label("settings.section.sound".localized, systemImage: "speaker.wave.2")
                .tag("sound")

            // COMBINED LICENSE + CLOUD CREDITS
            // The license key is the wallet, so license activation and the credit
            // balance now live in one always-visible section (CloudAccountSettingsSection).
            // Tagged "license" so existing deep links (e.g. MainAppView "Enter License Key")
            // still resolve here.
            Label("settings.section.cloud".localized, systemImage: "cloud")
                .tag("license")

            Label("settings.section.storage".localized, systemImage: "folder")
                .tag("storage")

            Label("settings.section.output".localized, systemImage: "text.cursor")
                .tag("output")

            Label("settings.section.shortcuts".localized, systemImage: "keyboard")
                .tag("shortcuts")

            Label("settings.section.vocabulary".localized, systemImage: "character.book.closed")
                .tag("vocabulary")

            Label("settings.section.backup".localized, systemImage: "externaldrive")
                .tag("backup")

            Label("API Server", systemImage: "network")
                .tag("apiserver")
        }
        .listStyle(.sidebar)
    }

    @ViewBuilder
    private func sectionView(for identifier: String?) -> some View {
        switch identifier {
        case "sound":
            SoundSettingsSection()
        case "license", "credits":
            CloudAccountSettingsSection()
        case "storage":
            StorageSettingsSection()
        case "shortcuts":
            ShortcutsSettingsSection()
        case "output":
            OutputSettingsSection()
        case "vocabulary":
            VocabularySettingsSection()
        case "backup":
            BackupSettingsSection()
        case "apiserver":
            APIServerSettingsSection()
        case "general", .none:
            GeneralSettingsSection()
        default:
            GeneralSettingsSection()
        }
    }
}

#Preview {
    let licenseManager = LicenseManager()
    return SettingsView()
        .environmentObject(SettingsManager())
        .environmentObject(AppState())
        .environmentObject(ParakeetModelManager())
        .environmentObject(TranscriptionPipeline())
        .environmentObject(WhisperModelManager())
        .environmentObject(LocalModelManager())
        .environmentObject(licenseManager)
        .environmentObject(HyperWhisperCloudManager(licenseManager: licenseManager))
        .environmentObject(CloudProviderHealthManager())
        .frame(width: 900, height: 600)
}
