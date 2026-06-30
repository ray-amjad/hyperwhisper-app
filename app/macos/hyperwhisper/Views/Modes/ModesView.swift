//
//  ModesView.swift
//  hyperwhisper
//
//  Created by Rehman Amjad on 16/08/2025.
//
//  MODES VIEW
//  Interface for managing transcription modes (profiles).
//  Users can create custom modes for different contexts (meetings, notes, etc.)
//
//  Each mode can have:
//  - Custom language settings
//  - Specific AI model
//  - Text formatting preferences
//  - Punctuation and capitalization rules

import SwiftUI
import CoreData
import Combine
#if canImport(Speech)
import Speech
#endif
#if os(macOS)
import AppKit
#endif

// MARK: - Modes View

/// Main view for managing transcription modes
struct ModesView: View {
    @EnvironmentObject var appState: AppState
    @EnvironmentObject var transcriptionPipeline: TranscriptionPipeline
    @EnvironmentObject var whisperModelManager: WhisperModelManager
    @EnvironmentObject var settingsManager: SettingsManager
    @EnvironmentObject var parakeetModelManager: ParakeetModelManager
    @EnvironmentObject var qwen3AsrModelManager: Qwen3AsrModelManager
    @EnvironmentObject var nemotronModelManager: NemotronModelManager
    @Environment(\.managedObjectContext) private var viewContext

    // Fetch modes from Core Data
    @FetchRequest(
        sortDescriptors: [NSSortDescriptor(keyPath: \Mode.sortOrder, ascending: true)],
        animation: .default
    )
    private var modes: FetchedResults<Mode>

    /// Whether to show the create mode sheet
    @State private var showingCreateMode = false

    /// Selected mode for editing
    @State private var selectedMode: Mode?

    /// Deletion flow state
    @State private var modeToDelete: Mode?
    @State private var showingDeleteConfirm = false
    @State private var showingLastModeAlert = false

    private var downloadedLocalModelIds: [String] {
        var ids = whisperModelManager.downloadedModels.map { $0.name }
        // Add ALL downloaded Parakeet models (V2 and V3)
        for parakeet in parakeetModelManager.availableModels where parakeet.isDownloaded {
            ids.append(parakeet.name)
        }
        // Add Qwen3 ASR if downloaded
        if qwen3AsrModelManager.isDownloaded {
            ids.append(Qwen3AsrModelManager.Constants.modelId)
        }
        // Add ALL downloaded Nemotron variants (latin + multilingual download independently)
        if #available(macOS 14.0, *) {
            for nemotron in nemotronModelManager.availableModels where nemotron.isDownloaded {
                ids.append(nemotron.name)
            }
        }
        #if canImport(Speech)
        if #available(macOS 26.0, *) {
            if SpeechTranscriber.isAvailable {
                ids.append("apple-speech-analyzer")
            }
        }
        #endif
        return ids
    }

    private func localModelDisplayName(for id: String) -> String {
        if id == "apple-speech-analyzer" { return "Apple Speech" }
        if id == Qwen3AsrModelManager.Constants.modelId { return Qwen3AsrModelManager.Constants.displayName }
        if let whisper = whisperModelManager.availableModels.first(where: { $0.name == id }) {
            return whisper.displayName
        }
        if let parakeet = parakeetModelManager.availableModels.first(where: { $0.name == id }) {
            return parakeet.displayName
        }
        if #available(macOS 14.0, *),
           let nemotron = nemotronModelManager.availableModels.first(where: { $0.name == id }) {
            return nemotron.displayName
        }
        return id
    }

    private func isLocalModelDownloaded(_ id: String) -> Bool {
        if id == "apple-speech-analyzer" { return true }
        if id == Qwen3AsrModelManager.Constants.modelId { return qwen3AsrModelManager.isDownloaded }
        if whisperModelManager.downloadedModels.contains(where: { $0.name == id }) {
            return true
        }
        if let parakeet = parakeetModelManager.availableModels.first(where: { $0.name == id }) {
            return parakeet.isDownloaded
        }
        if #available(macOS 14.0, *),
           let nemotron = nemotronModelManager.availableModels.first(where: { $0.name == id }) {
            return nemotron.isDownloaded
        }
        return false
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            // Header section
            headerSection

            Divider()

            // Modes grid
            ScrollView {
                modesGrid
                    .padding()
            }
            .background(VisualEffectBackground())
        }
        .navigationTitle("Modes")
        .sheet(isPresented: $showingCreateMode) {
            ModeEditorView(configuration: .create, availableModelIds: downloadedLocalModelIds) { (newModeData: ModeData) in
                // Create new Mode entity in Core Data
                let persistenceController = PersistenceController.shared
                let newMode = persistenceController.createOrUpdateMode(
                    name: newModeData.name,
                    preset: newModeData.preset,
                    language: newModeData.language,
                    model: newModeData.model,
                    punctuation: newModeData.punctuation,
                    capitalization: newModeData.capitalization,
                    profanityFilter: newModeData.profanityFilter,
                    customInstructions: newModeData.customInstructions,
                    languageModel: newModeData.languageModel,
                    cloudProvider: newModeData.cloudProvider,
                    cloudTranscriptionModel: newModeData.cloudTranscriptionModel,
                    postProcessingMode: newModeData.postProcessingMode.rawValue,
                    postProcessingProvider: newModeData.postProcessingProvider,
                    englishSpelling: newModeData.englishSpelling.rawValue,
                    userSystemPrompt: newModeData.userSystemPrompt,
                    useStreamingTranscription: newModeData.useStreamingTranscription,
                    cloudAccuracyTier: newModeData.cloudAccuracyTier.rawValue,
                    removeTrailingPeriod: newModeData.removeTrailingPeriod,
                    enableScreenOCR: newModeData.enableScreenOCR,
                    geminiCustomPrompt: newModeData.geminiCustomPrompt,
                    cloudPostProcessingModel: newModeData.cloudPostProcessingModel.rawValue,
                    cloudTranscriptionDomain: newModeData.cloudTranscriptionDomain
                )

                // Update app state with the new mode
                appState.selectMode(newMode, persist: true)
                showingCreateMode = false
            }
        }
        .sheet(item: $selectedMode) { mode in
            ModeEditorView(
                configuration: .edit(mode: mode, onDelete: {
                    // Handle delete from edit modal
                    initiateDeleteMode(mode)
                }),
                availableModelIds: downloadedLocalModelIds,
                onSave: { updatedModeData in
                    // Update the Core Data entity
                    mode.name = updatedModeData.name
                    mode.preset = updatedModeData.preset
                    mode.language = LanguageData.canonicalLanguageCode(updatedModeData.language)
                    mode.model = updatedModeData.model
                    mode.punctuation = updatedModeData.punctuation
                    mode.capitalization = updatedModeData.capitalization
                    mode.profanityFilter = updatedModeData.profanityFilter
                    mode.customInstructions = updatedModeData.customInstructions
                    let trimmedUserPrompt = updatedModeData.userSystemPrompt.trimmingCharacters(in: .whitespacesAndNewlines)
                    if trimmedUserPrompt.isEmpty {
                        mode.userSystemPrompt = nil
                    } else {
                        mode.userSystemPrompt = String(trimmedUserPrompt.prefix(userSystemPromptCharacterLimit))
                    }
                    mode.languageModel = updatedModeData.languageModel
                    mode.cloudProvider = updatedModeData.cloudProvider
                    mode.cloudTranscriptionModel = updatedModeData.cloudTranscriptionModel
                    mode.postProcessingMode = updatedModeData.postProcessingMode.rawValue
                    mode.postProcessingProvider = updatedModeData.postProcessingProvider
                    mode.englishSpelling = updatedModeData.englishSpelling.rawValue
                    mode.useStreamingTranscription = updatedModeData.useStreamingTranscription
                    mode.cloudAccuracyTier = updatedModeData.cloudAccuracyTier.rawValue
                    mode.removeTrailingPeriod = updatedModeData.removeTrailingPeriod
                    mode.enableScreenOCR = updatedModeData.enableScreenOCR
                    let trimmedGeminiPrompt = updatedModeData.geminiCustomPrompt.trimmingCharacters(in: .whitespacesAndNewlines)
                    if trimmedGeminiPrompt.isEmpty {
                        mode.geminiCustomPrompt = nil
                    } else {
                        mode.geminiCustomPrompt = String(trimmedGeminiPrompt.prefix(geminiCustomPromptCharacterLimit))
                    }
                    mode.cloudPostProcessingModel = updatedModeData.cloudPostProcessingModel.rawValue
                    mode.cloudTranscriptionDomain = updatedModeData.cloudTranscriptionDomain
                    mode.modifiedDate = Date()

                    // Save changes
                    PersistenceController.shared.save()

                    // Update app state if this is the selected mode
                    if appState.selectedModeId == mode.id?.uuidString {
                        // Re-select to ensure name is updated
                        appState.selectMode(mode, persist: false)
                    }

                    selectedMode = nil
                }
            )
        }
        .confirmationDialog("modes.delete.confirm.title".localized, isPresented: $showingDeleteConfirm, titleVisibility: .visible) {
            Button(role: .destructive) {
                if let mode = modeToDelete { deleteMode(mode) }
                modeToDelete = nil
            } label: {
                Text(localized: "common.delete")
            }
            Button(role: .cancel) { modeToDelete = nil } label: {
                Text(localized: "common.cancel")
            }
        } message: {
            Text(localized: "modes.delete.confirm.message")
        }
        .alert("Cannot Delete Last Mode", isPresented: $showingLastModeAlert) {
            Button("OK", role: .cancel) { }
        } message: {
            Text("You cannot delete your last remaining mode. Create a new mode first, then you can delete this one.")
        }
    }

    // MARK: - Header Section

    private var headerSection: some View {
        PageHeader(
            title: "modes.header.title".localized,
            subtitle: "modes.header.subtitle".localized,
            actionLabel: "modes.header.create".localized,
            actionIcon: "plus.circle.fill",
            action: { showingCreateMode = true }
        )
    }

    // MARK: - Modes Grid

    private var modesGrid: some View {
        LazyVGrid(columns: [GridItem(.adaptive(minimum: 280, maximum: 380), spacing: 12)], spacing: 12) {
            ForEach(modes) { mode in
                ModeCard(
                    mode: mode,
                    isSelected: appState.selectedModeId == mode.id?.uuidString,
                    whisperModelManager: whisperModelManager,
                    parakeetModelManager: parakeetModelManager,
                    qwen3AsrModelManager: qwen3AsrModelManager,
                    transcriptionPipeline: transcriptionPipeline
                ) {
                    // Select this mode
                    withAnimation(.spring(response: 0.3, dampingFraction: 0.7)) {
                        appState.selectMode(mode, persist: true)
                    }
                } onEdit: {
                    // Edit mode
                    selectedMode = mode
                } onDelete: {
                    // Delete mode from grid
                    initiateDeleteMode(mode)
                }
            }
        }
        .padding(.horizontal, 4)
    }

    // MARK: - Actions

    /// Initiates the deletion flow for a mode
    /// Shows an alert if it's the last mode, or a confirmation dialog otherwise
    private func initiateDeleteMode(_ mode: Mode) {
        // Check if this is the last remaining mode
        if modes.count <= 1 {
            showingLastModeAlert = true
        } else {
            modeToDelete = mode
            showingDeleteConfirm = true
        }
    }

    /// Performs the actual deletion of a mode
    ///
    /// DELETION FLOW:
    /// 1. Remove the mode from Core Data persistence
    /// 2. Cleanup any per-mode settings in SettingsManager
    /// 3. If the deleted mode was currently selected, automatically switch to the first remaining mode
    ///    (by sort order, which is index 0 in the list)
    /// 4. If no modes remain after deletion, clear the selected mode ID to prevent "mode not found" errors
    private func deleteMode(_ mode: Mode) {
        // STEP 1: Delete from Core Data
        PersistenceController.shared.deleteMode(mode)

        // STEP 2: Cleanup per-mode model mapping in settings
        if let modeId = mode.id?.uuidString {
            settingsManager.defaultModelByMode.removeValue(forKey: modeId)
        }

        // STEP 3: Handle mode selection if the deleted mode was currently selected
        if appState.selectedModeId == mode.id?.uuidString {
            // Fetch all remaining modes AFTER deletion to ensure we get the current state
            let remainingModes = PersistenceController.shared.fetchAllModes()

            if let firstMode = remainingModes.first {
                // Select the first remaining mode (index 0 by sort order)
                appState.selectMode(firstMode, persist: true)
                AppLogger.ui.info("Deleted selected mode, switched to first remaining mode: \(firstMode.name ?? "Unknown")")
            } else {
                // No modes left - clear the selection to prevent errors
                appState.selectedModeId = ""
                appState.selectedModeName = ""
                settingsManager.currentModeId = ""
                settingsManager.currentMode = ""
                AppLogger.ui.warning("Deleted last mode, cleared mode selection")
            }
        }
    }
}

// MARK: - Preview

#Preview {
    ModesView()
        .environmentObject(AppState())
        .environmentObject(TranscriptionPipeline())
        .environmentObject(SettingsManager())
        // NOTE: Preview uses fresh instance for isolation
        .environmentObject(WhisperModelManager())
        .environmentObject(ParakeetModelManager())
        .environmentObject(Qwen3AsrModelManager())
        .environmentObject(NemotronModelManager())
        .frame(width: 900, height: 700)
}
