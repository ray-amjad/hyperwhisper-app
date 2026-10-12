//
//  VocabularyMultiLineWordTests.swift
//  hyperwhisperTests
//
//  Regression cover for issue #1475: a pasted multi-line text in the
//  Vocabulary add field was saved as ONE word with newlines inside it.
//  It now becomes one word per non-empty line, and a snippet's replacement
//  keeps its line breaks.
//

import CoreData
import Foundation
import Testing

@testable import HyperWhisper

@Suite("A multi-line vocabulary paste is one word per line (#1475)")
struct VocabularyMultiLineWordTests {

    // MARK: - Splitting

    @Test func aPastedListSplitsIntoOneTrimmedWordPerLine() {
        #expect(PersistenceController.vocabularyWordLines(from: "line1\nline2") == ["line1", "line2"])
        #expect(PersistenceController.vocabularyWordLines(from: "alpha\nbeta\ngamma") == ["alpha", "beta", "gamma"])
    }

    @Test func blankLinesAndEdgeWhitespaceAreDropped() {
        #expect(PersistenceController.vocabularyWordLines(from: "  alpha \n\n \t\nbeta  \n") == ["alpha", "beta"])
    }

    @Test func everyLineBreakKindSplits() {
        #expect(PersistenceController.vocabularyWordLines(from: "a\r\nb\rc\u{2028}d\u{2029}e") == ["a", "b", "c", "d", "e"])
    }

    @Test func aSingleWordStaysOneWord() {
        #expect(PersistenceController.vocabularyWordLines(from: "  Hyper Whisper  ") == ["Hyper Whisper"])
        #expect(PersistenceController.vocabularyWordLines(from: " \n \n").isEmpty)
    }

    // MARK: - The store

    @MainActor
    @Test func aWordWithANewlineInsideIsRefused() {
        let persistence = PersistenceController(inMemory: true)
        #expect(!persistence.addVocabularyItem(word: "line1\nline2", replacement: nil))
        #expect(persistence.fetchAllVocabularyItems().isEmpty)
    }

    @MainActor
    @Test func eachLineIsSavedWithTheUsualDuplicateCheck() {
        let persistence = PersistenceController(inMemory: true)
        #expect(persistence.addVocabularyItem(word: "beta", replacement: nil))

        let results = PersistenceController.vocabularyWordLines(from: "alpha\nBETA\ngamma")
            .map { persistence.addVocabularyItem(word: $0, replacement: nil) }

        #expect(results == [true, false, true])
        let words = persistence.fetchAllVocabularyItems().compactMap(\.word).sorted()
        #expect(words == ["alpha", "beta", "gamma"])
        #expect(words.allSatisfy { $0.rangeOfCharacter(from: .newlines) == nil })
    }

    @MainActor
    @Test func aSnippetReplacementKeepsItsLineBreaks() {
        let persistence = PersistenceController(inMemory: true)
        #expect(persistence.addVocabularyItem(word: "sig", replacement: "Best,\nRay"))
        let item = persistence.fetchAllVocabularyItems().first
        #expect(item?.word == "sig")
        #expect(item?.replacement == "Best,\nRay")
    }

    // MARK: - Backup import (#1777)

    /// A backup made before #1475 can carry a word with line breaks inside it.
    /// It imports as one word per non-empty line, like the add field.
    @MainActor
    @Test func aBackupWordWithLineBreaksImportsAsOneWordPerLine() throws {
        let persistence = PersistenceController(inMemory: true)
        #expect(persistence.addVocabularyItem(word: "y", replacement: nil))

        let result = try persistence.importBackupStore(
            modes: nil,
            modeResolution: .skip,
            vocabulary: [
                BackupVocabularyItem(id: UUID(), word: " x \ny\n\nz", replacement: "Best,\nRay", sortOrder: 0, source: "manual"),
            ],
            vocabularyResolution: .skip
        )

        #expect(result.vocabularyImported == 2)
        #expect(result.vocabularySkipped == 1)
        let items = persistence.fetchAllVocabularyItems()
        #expect(items.compactMap(\.word).sorted() == ["x", "y", "z"])
        #expect(items.allSatisfy { ($0.word ?? "").rangeOfCharacter(from: .newlines) == nil })
        #expect(items.first { $0.word == "x" }?.replacement == "Best,\nRay")
        #expect(items.first { $0.word == "y" }?.replacement == nil)
    }
}
