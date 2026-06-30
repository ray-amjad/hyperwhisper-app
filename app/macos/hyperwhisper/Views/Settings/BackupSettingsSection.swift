//
//  BackupSettingsSection.swift
//  hyperwhisper
//
//  BACKUP SETTINGS SECTION
//  UI for exporting and importing app settings.
//  Allows users to backup settings to JSON and restore on another device.
//
//  FEATURES:
//  - Section-selectable export (Settings / Modes / Vocabulary + optional API keys / license key).
//    A vocabulary-only export is written in the cross-platform universal v2 .hwbackup.json format.
//  - Auto-detecting import: the chosen file is pre-parsed and only the sections it actually
//    contains can be restored; absent sections are disabled.
//  - Pre-merge summary for vocabulary (new vs. conflicting words) with skip/replace, shown before
//    anything changes. Vocabulary restore is merge-only — it never deletes an existing word.
//

import SwiftUI

struct BackupSettingsSection: View {
    @ObservedObject private var backupManager = BackupManager.shared
    @EnvironmentObject private var localModelManager: LocalModelManager

    // MARK: - Export State

    /// Section toggles (all ON by default → preserves the prior whole-backup behavior)
    @State private var includeSettings = true
    @State private var includeModes = true
    @State private var includeVocabulary = true
    /// Include API keys in export (OFF by default for security)
    @State private var includeAPIKeys = false
    /// Include license key in export (OFF by default)
    @State private var includeLicenseKey = false

    // MARK: - Import State

    /// The file the user picked to import, and what it contains (drives the options sheet).
    @State private var importURL: URL?
    @State private var importContents: BackupContents?
    @State private var showImportSheet = false

    // MARK: - Result State

    /// Show result alert
    @State private var showResultAlert = false
    /// Result message to display
    @State private var resultMessage = ""
    /// Whether result is success or error
    @State private var resultIsSuccess = false

    /// Local-LLM model ids referenced by restored `.local` modes that aren't
    /// downloaded yet (capable hardware only) — drives the re-download prompt.
    @State private var pendingLocalDownloadIds: Set<String> = []
    /// Show the "download local models" prompt after a restore.
    @State private var showLocalDownloadPrompt = false

    private var hasAnyExportSelection: Bool {
        includeSettings || includeModes || includeVocabulary || includeAPIKeys || includeLicenseKey
    }

    var body: some View {
        SettingsSection(title: "settings.section.backup") {
            exportCard

            // Security warning when sensitive data will be included
            if includeAPIKeys || includeLicenseKey {
                securityWarningView
            }

            importCard

            infoNoteView
        }
        .sheet(isPresented: $showImportSheet) {
            if let url = importURL, let contents = importContents {
                BackupImportOptionsSheet(
                    fileName: url.lastPathComponent,
                    contents: contents,
                    preview: backupManager.vocabularyMergePreview(at: url),
                    onCancel: { showImportSheet = false },
                    onImport: { options in
                        showImportSheet = false
                        Task { await performImport(from: url, options: options) }
                    }
                )
            }
        }
        .alert(resultIsSuccess ? "settings.backup.result.success.title" : "settings.backup.result.error.title",
               isPresented: $showResultAlert) {
            Button("common.ok", role: .cancel) {
                // Chain the local-model re-download prompt after the result alert so
                // two alerts never contend for presentation.
                if !pendingLocalDownloadIds.isEmpty {
                    showLocalDownloadPrompt = true
                }
            }
        } message: {
            Text(resultMessage)
        }
        .alert("settings.backup.localDownload.title", isPresented: $showLocalDownloadPrompt) {
            Button("settings.backup.localDownload.downloadAll") {
                for id in pendingLocalDownloadIds {
                    localModelManager.downloadModel(id)
                }
                pendingLocalDownloadIds = []
            }
            Button("settings.backup.localDownload.dismiss", role: .cancel) {
                pendingLocalDownloadIds = []
            }
        } message: {
            Text(String(
                format: NSLocalizedString(
                    "settings.backup.localDownload.message",
                    value: "%d of your restored modes use on-device AI models that aren't downloaded yet.",
                    comment: ""
                ),
                pendingLocalDownloadIds.count
            ))
        }
    }

    // MARK: - Export Card

    private var exportCard: some View {
        SettingsCard(horizontalPadding: 8) {
            VStack(spacing: 0) {
                // Export header + button row
                HStack(alignment: .center, spacing: 12) {
                    VStack(alignment: .leading, spacing: 2) {
                        Text("settings.backup.export.title")
                            .font(.headline)
                        Text("settings.backup.export.choose.subtitle")
                            .font(.caption)
                            .foregroundColor(.secondary)
                    }

                    Spacer(minLength: 12)

                    Button("settings.backup.export.button") {
                        Task { await exportSettings() }
                    }
                    .buttonStyle(.borderedProminent)
                    .controlSize(.regular)
                    .disabled(backupManager.isExporting || !hasAnyExportSelection)
                }
                .padding(DesignConstants.Spacing.rowPadding)

                Divider()

                // Section selection
                SettingsToggleRow(
                    title: "settings.backup.export.section.settings",
                    subtitle: nil,
                    isOn: $includeSettings,
                    standalone: false
                )
                Divider()
                SettingsToggleRow(
                    title: "settings.backup.export.section.modes",
                    subtitle: nil,
                    isOn: $includeModes,
                    standalone: false
                )
                Divider()
                SettingsToggleRow(
                    title: "settings.backup.export.section.vocabulary",
                    subtitle: nil,
                    isOn: $includeVocabulary,
                    standalone: false
                )

                Divider()

                // Include API Keys toggle
                SettingsToggleRow(
                    title: "settings.backup.includeAPIKeys.title",
                    subtitle: "settings.backup.includeAPIKeys.subtitle",
                    isOn: $includeAPIKeys,
                    standalone: false
                )

                Divider()

                // Include License Key toggle
                SettingsToggleRow(
                    title: "settings.backup.includeLicense.title",
                    subtitle: nil,
                    isOn: $includeLicenseKey,
                    standalone: false
                )
            }
        }
    }

    // MARK: - Import Card

    private var importCard: some View {
        SettingsCard(horizontalPadding: 8) {
            VStack(spacing: 0) {
                SettingsActionRow(
                    title: "settings.backup.import.title",
                    subtitle: "settings.backup.import.subtitle",
                    buttonTitle: "settings.backup.import.button",
                    standalone: false,
                    action: { Task { await selectImportFile() } }
                )
            }
        }
    }

    // MARK: - Security Warning

    private var securityWarningView: some View {
        HStack(spacing: 8) {
            Image(systemName: "exclamationmark.triangle.fill")
                .foregroundColor(.orange)
                .font(.caption)
            Text("settings.backup.security.warning")
                .font(.caption)
                .foregroundColor(.orange)
            Spacer()
        }
        .padding(.horizontal, 4)
        .frame(maxWidth: SettingsLayout.contentWidth, alignment: .leading)
    }

    // MARK: - Info Note

    private var infoNoteView: some View {
        HStack(spacing: 8) {
            Image(systemName: "info.circle")
                .foregroundColor(.secondary)
                .font(.caption)
            Text("settings.backup.info.note")
                .font(.caption)
                .foregroundColor(.secondary)
            Spacer()
        }
        .padding(.horizontal, 4)
        .frame(maxWidth: SettingsLayout.contentWidth, alignment: .leading)
    }

    // MARK: - Actions

    private func exportSettings() async {
        let options = ExportOptions(
            includeSettings: includeSettings,
            includeModes: includeModes,
            includeVocabulary: includeVocabulary,
            includeAPIKeys: includeAPIKeys,
            includeLicenseKey: includeLicenseKey
        )

        let success = await backupManager.exportWithDialog(options: options)

        if success {
            resultIsSuccess = true
            resultMessage = NSLocalizedString("settings.backup.export.success", value: "Settings exported successfully", comment: "")
            showResultAlert = true
        } else if let error = backupManager.lastError {
            resultIsSuccess = false
            resultMessage = error
            showResultAlert = true
        }
    }

    private func selectImportFile() async {
        // Present open dialog
        let panel = NSOpenPanel()
        panel.title = NSLocalizedString("settings.backup.import.panel.title", value: "Import Settings", comment: "")
        panel.allowedContentTypes = [.json]
        panel.allowsMultipleSelection = false
        panel.canChooseDirectories = false

        guard let window = NSApp.keyWindow ?? NSApp.mainWindow else { return }
        let response = await panel.beginSheetModal(for: window)

        guard response == .OK, let url = panel.url else { return }

        // Pre-parse the file so the options sheet can auto-detect what it contains.
        guard let contents = backupManager.inspectBackupFile(at: url) else {
            resultIsSuccess = false
            resultMessage = backupManager.lastError ?? NSLocalizedString("settings.backup.import.error.unknown", value: "Import failed", comment: "")
            showResultAlert = true
            return
        }

        importURL = url
        importContents = contents
        showImportSheet = true
    }

    private func performImport(from url: URL, options: ImportOptions) async {
        let result = await backupManager.importSettings(from: url, options: options)

        if result.success {
            resultIsSuccess = true
            pendingLocalDownloadIds = result.pendingLocalDownloadModelIds
            resultMessage = String(
                format: NSLocalizedString("settings.backup.import.success", value: "Import complete: %d modes, %d vocabulary items imported", comment: ""),
                result.modesImported,
                result.vocabularyImported
            )
        } else {
            resultIsSuccess = false
            resultMessage = result.errorMessage ?? NSLocalizedString("settings.backup.import.error.unknown", value: "Import failed", comment: "")
        }

        showResultAlert = true
    }
}

// MARK: - Import Options Sheet

/// Auto-detecting import dialog: shows which sections the chosen file contains, lets the user pick
/// any subset (absent sections are disabled), and — for vocabulary — shows a pre-merge summary plus
/// a skip/replace choice. The "Import" button is the confirmation; nothing changes until it's tapped.
private struct BackupImportOptionsSheet: View {
    let fileName: String
    let contents: BackupContents
    /// (new, conflict) word counts, or nil if not computable.
    let preview: (newCount: Int, conflictCount: Int)?
    let onCancel: () -> Void
    let onImport: (ImportOptions) -> Void

    @State private var importSettings: Bool
    @State private var importModes: Bool
    @State private var importVocabulary: Bool
    @State private var importAPIKeys: Bool
    @State private var importLicense: Bool
    @State private var vocabularyConflict: VocabularyConflictResolution = .skip

    init(fileName: String,
         contents: BackupContents,
         preview: (newCount: Int, conflictCount: Int)?,
         onCancel: @escaping () -> Void,
         onImport: @escaping (ImportOptions) -> Void) {
        self.fileName = fileName
        self.contents = contents
        self.preview = preview
        self.onCancel = onCancel
        self.onImport = onImport
        // Default each section ON when present in the file, OFF (and disabled) when absent.
        _importSettings = State(initialValue: contents.hasSettings)
        _importModes = State(initialValue: contents.hasModes)
        _importVocabulary = State(initialValue: contents.hasVocabulary)
        _importAPIKeys = State(initialValue: contents.hasAPIKeys)
        _importLicense = State(initialValue: contents.hasLicense)
    }

    private var hasAnySelection: Bool {
        (importSettings && contents.hasSettings)
            || (importModes && contents.hasModes)
            || (importVocabulary && contents.hasVocabulary)
            || (importAPIKeys && contents.hasAPIKeys)
            || (importLicense && contents.hasLicense)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            VStack(alignment: .leading, spacing: 4) {
                Text("settings.backup.import.title")
                    .font(.headline)
                Text(String(
                    format: NSLocalizedString("settings.backup.import.importing", value: "Importing %@", comment: ""),
                    fileName
                ))
                .font(.caption)
                .foregroundColor(.secondary)
            }

            Text(containsSummary)
                .font(.callout)
                .foregroundColor(.secondary)
                .padding(.horizontal, 10)
                .padding(.vertical, 6)
                .background(Color.secondary.opacity(0.1))
                .clipShape(RoundedRectangle(cornerRadius: 6))

            VStack(alignment: .leading, spacing: 10) {
                sectionRow("settings.backup.import.section.settings", isOn: $importSettings, present: contents.hasSettings)
                sectionRow("settings.backup.import.section.modes", isOn: $importModes, present: contents.hasModes)
                sectionRow("settings.backup.import.section.vocabulary", isOn: $importVocabulary, present: contents.hasVocabulary)
                sectionRow("settings.backup.import.section.apiKeys", isOn: $importAPIKeys, present: contents.hasAPIKeys)
                sectionRow("settings.backup.import.section.license", isOn: $importLicense, present: contents.hasLicense)
            }

            // Pre-merge summary + conflict resolution for vocabulary.
            if contents.hasVocabulary, importVocabulary, let preview = preview {
                VStack(alignment: .leading, spacing: 8) {
                    Divider()
                    Text(String(
                        format: NSLocalizedString("settings.backup.import.premerge.summary", value: "Vocabulary: add %d new, %d already exist", comment: ""),
                        preview.newCount,
                        preview.conflictCount
                    ))
                    .font(.caption)
                    .foregroundColor(.secondary)

                    if preview.conflictCount > 0 {
                        Picker(selection: $vocabularyConflict) {
                            Text("settings.backup.conflict.skip").tag(VocabularyConflictResolution.skip)
                            Text("settings.backup.conflict.replace").tag(VocabularyConflictResolution.replace)
                        } label: {
                            Text("settings.backup.import.premerge.conflictLabel")
                        }
                        .pickerStyle(.radioGroup)
                    }
                }
            }

            HStack {
                Spacer()
                Button("common.cancel", role: .cancel) { onCancel() }
                    .keyboardShortcut(.cancelAction)
                Button("settings.backup.import.button") {
                    onImport(buildOptions())
                }
                .buttonStyle(.borderedProminent)
                .keyboardShortcut(.defaultAction)
                .disabled(!hasAnySelection)
            }
        }
        .padding(20)
        .frame(width: 420)
    }

    private func sectionRow(_ titleKey: LocalizedStringKey, isOn: Binding<Bool>, present: Bool) -> some View {
        HStack(spacing: 8) {
            Toggle(isOn: present ? isOn : .constant(false)) {
                Text(titleKey)
            }
            .toggleStyle(.checkbox)
            .disabled(!present)
            if !present {
                Text("settings.backup.import.section.absent")
                    .font(.caption)
                    .foregroundColor(.secondary)
            }
            Spacer()
        }
        .opacity(present ? 1 : 0.5)
    }

    private var containsSummary: String {
        var parts: [String] = []
        if contents.hasSettings { parts.append(NSLocalizedString("settings.backup.import.section.settings", value: "Settings", comment: "")) }
        if contents.hasModes { parts.append(NSLocalizedString("settings.backup.import.section.modes", value: "Modes", comment: "")) }
        if contents.hasVocabulary {
            parts.append(String(
                format: NSLocalizedString("settings.backup.import.contains.vocabulary", value: "Vocabulary (%d words)", comment: ""),
                contents.vocabularyCount
            ))
        }
        if contents.hasAPIKeys { parts.append(NSLocalizedString("settings.backup.import.section.apiKeys", value: "API keys", comment: "")) }
        if contents.hasLicense { parts.append(NSLocalizedString("settings.backup.import.section.license", value: "License key", comment: "")) }

        let list = parts.isEmpty ? NSLocalizedString("settings.backup.import.contains.none", value: "nothing", comment: "") : parts.joined(separator: ", ")
        return String(
            format: NSLocalizedString("settings.backup.import.contains", value: "This file contains: %@", comment: ""),
            list
        )
    }

    private func buildOptions() -> ImportOptions {
        ImportOptions(
            importSettings: importSettings && contents.hasSettings,
            importModes: importModes && contents.hasModes,
            importVocabulary: importVocabulary && contents.hasVocabulary,
            modeConflict: .replace,
            vocabularyConflict: vocabularyConflict,
            importAPIKeys: importAPIKeys && contents.hasAPIKeys,
            importLicenseKey: importLicense && contents.hasLicense
        )
    }
}

#Preview {
    BackupSettingsSection()
        .frame(width: 600, height: 500)
        .padding()
}
