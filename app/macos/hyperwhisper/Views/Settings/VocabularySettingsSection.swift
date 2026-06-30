//
//  VocabularySettingsSection.swift
//  hyperwhisper
//
//  Settings section for vocabulary-related preferences. Currently hosts the
//  iCloud vocabulary sync toggle (defaulting OFF — privacy-first). The toggle
//  changes take effect only on the next launch because swapping
//  `cloudKitContainerOptions` on a live NSPersistentCloudKitContainer would
//  invalidate active @FetchRequest bindings and in-flight saves.
//

import SwiftUI
import AppKit

struct VocabularySettingsSection: View {
    /// User-facing iCloud vocabulary sync toggle. Defaults OFF.
    /// Must share its key with `PersistenceController.vocabularyCloudSyncEnabledDefaultsKey`.
    @AppStorage("vocabularyCloudSyncEnabled") private var iCloudSyncEnabled = false

    /// Controls the "Quit HyperWhisper to apply" alert shown after toggling sync.
    @State private var showRestartAlert = false

    var body: some View {
        SettingsSection(title: "settings.section.vocabulary") {
            iCloudSyncCard
        }
        .alert("vocabulary.icloudSync.restart.title".localized, isPresented: $showRestartAlert) {
            Button("vocabulary.icloudSync.restart.quit".localized, role: .destructive) {
                NSApplication.shared.terminate(nil)
            }
            Button("vocabulary.icloudSync.restart.later".localized, role: .cancel) { }
        } message: {
            Text("vocabulary.icloudSync.restart.message".localized)
        }
    }

    // MARK: - Cards

    private var iCloudSyncCard: some View {
        SettingsCard(horizontalPadding: 8) {
            // A Binding wrapper runs the restart-alert side-effect on every
            // change, so the user is prompted to relaunch the moment they flip
            // the switch.
            SettingsToggleRow(
                title: "settings.vocabulary.icloudSync.title",
                subtitle: "settings.vocabulary.icloudSync.subtitle",
                info: "settings.vocabulary.icloudSync.info",
                isOn: Binding(
                    get: { iCloudSyncEnabled },
                    set: { newValue in
                        iCloudSyncEnabled = newValue
                        showRestartAlert = true
                    }
                ),
                standalone: false
            )
        }
    }
}

#Preview {
    ScrollView {
        VocabularySettingsSection()
            .padding()
    }
    .frame(width: 700, height: 400)
}
