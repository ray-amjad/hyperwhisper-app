//
//  ModeEditorView+UnsavedChanges.swift
//  HyperWhisper
//
//  The editor's side of the page guard (issue #1525): the values Save would
//  write now, whether they differ from the settled baseline, and the discard
//  prompt's presentation. The state they read and the monitors stay in
//  ModeEditorView.swift.
//

import SwiftUI

extension ModeEditorView {

    // MARK: - Unsaved-changes tracking (issue #1525)

    /// Every value Save writes, as the editor holds it now.
    var currentSnapshot: ModeEditorSnapshot {
        ModeEditorSnapshot(
            name: name,
            preset: preset,
            language: language,
            model: model,
            provider: provider,
            punctuation: punctuation,
            capitalization: capitalization,
            profanityFilter: profanityFilter,
            customInstructions: customInstructions,
            languageModel: languageModel,
            postProcessingMode: postProcessingMode,
            postProcessingProvider: postProcessingProvider,
            cloudProvider: cloudProvider,
            cloudAccuracyTier: cloudAccuracyTier,
            cloudPostProcessingModel: cloudPostProcessingModel,
            cloudTranscriptionModel: cloudTranscriptionModel,
            cloudTranscriptionDomain: cloudTranscriptionDomain,
            englishSpelling: englishSpelling,
            userSystemPrompt: userSystemPrompt,
            removeTrailingPeriod: removeTrailingPeriod,
            enableScreenOCR: enableScreenOCR,
            geminiCustomPrompt: geminiCustomPrompt
        )
    }

    var hasUnsavedChanges: Bool {
        ModeEditorSnapshot.hasUnsavedChanges(
            settled: settledSnapshot,
            current: currentSnapshot,
            userHasInteracted: userHasInteracted
        )
    }

    /// Shown while AppState holds a menu-bar page for THIS editor's prompt.
    /// The buttons clear it; the setter has nothing to do.
    var discardPromptIsPresented: Binding<Bool> {
        Binding(
            get: {
                appState.pendingModeEditorNavigation != nil
                    && appState.openModeEditorSession == editorSession
            },
            set: { _ in }
        )
    }
}
