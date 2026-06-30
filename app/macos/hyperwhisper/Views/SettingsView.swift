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

            Label("settings.section.license".localized, systemImage: "checkmark.seal")
                .tag("license")

            // CONDITIONAL RENDERING: Credits section only visible for licensed users
            // Trial users (using device_id) don't have access to HyperWhisper Cloud credits
            // Only licensed users (using license_key) get the $5 credit allocation
            //
            // Why this matters:
            // 1. Trial users use device_id for identification → no persistent credit pool
            // 2. Licensed users use license_key → get $5 credit allocation
            // 3. Showing credits tab to trial users would be confusing (nothing to display)
            // 4. When user activates license, this section appears automatically
            // 5. When user deactivates license, this section disappears and redirects to General
            if licenseManager.licenseStatus == .active {
                Label("settings.section.credits".localized, systemImage: "creditcard.fill")
                    .tag("credits")
            }

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
        .onChange(of: licenseManager.licenseStatus) { oldStatus, newStatus in
            // AUTO-NAVIGATION ON LICENSE CHANGE
            // If the user is currently viewing Credits and they deactivate their license,
            // we need to redirect them to another section since Credits will disappear
            //
            // Flow:
            // 1. User is viewing Credits section (selectedSettingsSection == "credits")
            // 2. User clicks "Deactivate Device" in License section
            // 3. License status changes from .active → .trial
            // 4. Credits section disappears from sidebar (conditional rendering)
            // 5. This onChange detects the status change
            // 6. If currently viewing credits, redirect to General settings
            // 7. Prevents "blank screen" UX issue
            if oldStatus == .active && newStatus != .active && appState.selectedSettingsSection == "credits" {
                appState.selectedSettingsSection = "general"
                AppLogger.ui.info("License deactivated while viewing Credits · redirecting to General settings")
            }
        }
    }

    @ViewBuilder
    private func sectionView(for identifier: String?) -> some View {
        switch identifier {
        case "sound":
            SoundSettingsSection()
        case "license":
            LicenseSettingsSection()
        case "credits":
            CreditsSettingsSection()
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
