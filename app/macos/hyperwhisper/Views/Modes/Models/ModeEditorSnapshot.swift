//
//  ModeEditorSnapshot.swift
//  HyperWhisper
//
//  What the mode editor's Save writes, for unsaved-changes tracking
//  (issue #1525). Moved out of ModeEditorView.swift unchanged (issue #730).
//

import Foundation

// MARK: - Unsaved-changes snapshot (issue #1525)

/// Every value the mode editor's Save writes, so the editor can tell whether
/// it holds an unsaved edit. View-only state (the "Show all models" box, the
/// info popover, the Source toggle's memory) is left out: changing it changes
/// nothing Save writes.
struct ModeEditorSnapshot: Equatable {
    var name: String
    var preset: String
    var language: String
    var model: String
    var provider: ProviderType
    var punctuation: Bool
    var capitalization: Bool
    var profanityFilter: Bool
    var customInstructions: String
    var languageModel: String
    var postProcessingMode: PostProcessingMode
    var postProcessingProvider: String
    var cloudProvider: String
    var cloudAccuracyTier: String
    var cloudPostProcessingModel: String
    var cloudTranscriptionModel: String
    var cloudTranscriptionDomain: String?
    var englishSpelling: EnglishSpelling
    var userSystemPrompt: String
    var removeTrailingPeriod: Bool
    var enableScreenOCR: Bool
    var geminiCustomPrompt: String

    /// True when the editor's values differ from the ones it settled on. Before
    /// it has settled there is nothing the user could have changed yet.
    static func hasUnsavedChanges(settled: ModeEditorSnapshot?, current: ModeEditorSnapshot) -> Bool {
        guard let settled else { return false }
        return settled != current
    }

    /// Before the user's first input every change is the editor's own, so
    /// there is no edit (and no prompt) even in the update pass before
    /// `settled(previous:current:userHasInteracted:)` catches up.
    static func hasUnsavedChanges(
        settled: ModeEditorSnapshot?,
        current: ModeEditorSnapshot,
        userHasInteracted: Bool
    ) -> Bool {
        userHasInteracted && hasUnsavedChanges(settled: settled, current: current)
    }

    /// The baseline once the editor holds `current`. Until the user's first
    /// key press, click or menu, a change can only be the editor's own repair
    /// (on-open clamps and their onChange cascades, a model download
    /// finishing), so the baseline follows it. After that it never moves:
    /// a user's edit is never absorbed, and a later repair reads as an edit.
    static func settled(
        previous: ModeEditorSnapshot?,
        current: ModeEditorSnapshot,
        userHasInteracted: Bool
    ) -> ModeEditorSnapshot? {
        userHasInteracted ? previous : current
    }
}
