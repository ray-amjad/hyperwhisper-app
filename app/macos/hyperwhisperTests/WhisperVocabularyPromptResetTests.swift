//
//  WhisperVocabularyPromptResetTests.swift
//  hyperwhisperTests
//
//  Issue #2048: the Whisper context stays loaded across transcriptions, and
//  `LibWhisperProvider` set its `initial_prompt` only when the mode had
//  vocabulary. So a mode with no prompt words (none at all, or every word
//  with a replacement) ran with the previous mode's prompt. The provider now
//  sets the prompt on every pass, nil when there are no prompt words.
//

import CoreData
import Foundation
import Testing
@testable import HyperWhisper

@MainActor
struct WhisperVocabularyPromptResetTests {

    /// The rows are never saved: the prompt only reads `word` and
    /// `replacement`. Same in-memory approach as `LocalEngineVocabularyOnceTests`.
    private let persistence = PersistenceController(inMemory: true)

    private func row(_ word: String?, _ replacement: String? = nil) -> Vocabulary {
        let row = Vocabulary(context: persistence.container.viewContext)
        row.id = UUID()
        row.word = word
        row.replacement = replacement
        return row
    }

    @Test func modeWithNoVocabularyClearsThePreviousModesPrompt() async {
        let context = WhisperContext()

        await LibWhisperProvider.applyVocabularyPrompt([row("HyprWhispr")], to: context)
        #expect(await context.currentPrompt == "HyprWhispr")

        await LibWhisperProvider.applyVocabularyPrompt([], to: context)
        #expect(await context.currentPrompt == nil)
    }

    @Test func modeWhoseWordsAllHaveReplacementsClearsThePreviousModesPrompt() async {
        let context = WhisperContext()

        await LibWhisperProvider.applyVocabularyPrompt([row("HyprWhispr"), row("Kubernetes")], to: context)
        #expect(await context.currentPrompt == "HyprWhispr Kubernetes")

        await LibWhisperProvider.applyVocabularyPrompt([row("teh", "the")], to: context)
        #expect(await context.currentPrompt == nil)
    }

    @Test func nextModesWordsReplaceThePreviousModesPrompt() async {
        let context = WhisperContext()

        await LibWhisperProvider.applyVocabularyPrompt([row("HyprWhispr")], to: context)
        await LibWhisperProvider.applyVocabularyPrompt([row("Kubernetes"), row("teh", "the")], to: context)
        #expect(await context.currentPrompt == "Kubernetes")
    }

    @Test func promptKeepsOnlyWordsWithNoReplacement() {
        let vocabulary = [row("HyprWhispr"), row("teh", "the"), row("Kubernetes", ""), row(nil), row("  ")]
        #expect(LibWhisperProvider.vocabularyPrompt(for: vocabulary) == "HyprWhispr Kubernetes")
        #expect(LibWhisperProvider.vocabularyPrompt(for: []) == nil)
    }
}
