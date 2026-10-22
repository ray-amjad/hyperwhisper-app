//
//  VocabularyTransferVectorTests.swift
//  hyperwhisperTests
//
//  Runs `shared-conformance/vocabulary-transfer-vectors.json` (issue #2409)
//  against the macOS CSV/TXT writer and reader, then round-trips the vectors'
//  entries through a real (in-memory) store. The .NET heads run the same file.
//

import CoreData
import Foundation
import Testing
@testable import HyperWhisper

@MainActor
struct VocabularyTransferVectorTests {

    // MARK: - Vector shapes

    struct Document: Decodable {
        let roundTrip: RoundTrip
        let csvImport: [ImportVector]
        let txtImport: [ImportVector]
    }

    struct RoundTrip: Decodable {
        let entries: [EntryVector]
        let csv: String
        let txt: String
    }

    struct ImportVector: Decodable {
        let name: String
        let input: String
        let expected: [EntryVector]
        let skipped: Int
    }

    struct EntryVector: Decodable {
        let word: String
        let replacement: String?
    }

    // MARK: - Loading

    private let document: Document

    init() throws {
        let url = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .appendingPathComponent("shared-conformance/vocabulary-transfer-vectors.json")
        document = try JSONDecoder().decode(Document.self, from: Data(contentsOf: url))
    }

    private func entries(_ vectors: [EntryVector]) -> [VocabularyTransferEntry] {
        vectors.map { VocabularyTransferEntry(word: $0.word, replacement: $0.replacement) }
    }

    private var roundTripEntries: [VocabularyTransferEntry] { entries(document.roundTrip.entries) }

    /// The store's rows in list order (`sortOrder`, the order an import appends).
    private func storedEntries(_ persistence: PersistenceController) -> [VocabularyTransferEntry] {
        persistence.fetchAllVocabularyItems()
            .sorted { $0.sortOrder < $1.sortOrder }
            .map { VocabularyTransferEntry(word: $0.word ?? "", replacement: $0.replacement) }
    }

    // MARK: - The vectors

    @Test("the CSV writer reproduces the round-trip file byte for byte")
    func csvWriterMatchesVector() {
        let written = VocabularyTransfer.writeCSV(roundTripEntries)
        #expect(Array(written.utf8) == Array(document.roundTrip.csv.utf8))
    }

    @Test("the TXT writer reproduces the round-trip file byte for byte")
    func txtWriterMatchesVector() {
        let written = VocabularyTransfer.writeTXT(roundTripEntries)
        #expect(Array(written.utf8) == Array(document.roundTrip.txt.utf8))
    }

    @Test("both readers turn the round-trip files back into the entries")
    func readersRoundTripTheVector() throws {
        for (format, text) in [(VocabularyTransferFormat.csv, document.roundTrip.csv), (.txt, document.roundTrip.txt)] {
            let parsed = try VocabularyTransfer.read(Data(text.utf8), as: format)
            #expect(parsed.entries == roundTripEntries, "\(format)")
            #expect(parsed.skipped == 0, "\(format)")
        }
    }

    @Test("the CSV reader matches every csvImport vector")
    func csvImportVectors() throws {
        #expect(!document.csvImport.isEmpty)
        for vector in document.csvImport {
            let parsed = try VocabularyTransfer.read(Data(vector.input.utf8), as: .csv)
            #expect(parsed.entries == entries(vector.expected), "\(vector.name)")
            #expect(parsed.skipped == vector.skipped, "\(vector.name): skipped")
        }
    }

    @Test("the TXT reader matches every txtImport vector")
    func txtImportVectors() throws {
        #expect(!document.txtImport.isEmpty)
        for vector in document.txtImport {
            let parsed = try VocabularyTransfer.read(Data(vector.input.utf8), as: .txt)
            #expect(parsed.entries == entries(vector.expected), "\(vector.name)")
            #expect(parsed.skipped == vector.skipped, "\(vector.name): skipped")
        }
    }

    @Test("a file that is not UTF-8 is refused")
    func notUTF8IsRefused() {
        let bytes = Data([0x61, 0x6C, 0x70, 0x68, 0x61, 0x0A, 0xC3, 0x28, 0xFF, 0x0A])
        #expect(throws: VocabularyTransferError.notUTF8) {
            _ = try VocabularyTransfer.read(bytes, as: .csv)
        }
        #expect(throws: VocabularyTransferError.notUTF8) {
            _ = try VocabularyTransfer.read(bytes, as: .txt)
        }
    }

    @Test("the format follows the file extension")
    func formatFromExtension() {
        #expect(VocabularyTransferFormat(fileExtension: "csv") == .csv)
        #expect(VocabularyTransferFormat(fileExtension: "CSV") == .csv)
        #expect(VocabularyTransferFormat(fileExtension: "txt") == .txt)
        #expect(VocabularyTransferFormat(fileExtension: "json") == nil)
        #expect(VocabularyTransfer.defaultExportFileName == "HyperWhisper Vocabulary.csv")
    }

    // MARK: - Round trip through the store

    @Test("export then import into an empty store keeps words, replacements and order")
    func roundTripIntoAnEmptyStore() throws {
        for format in [VocabularyTransferFormat.csv, .txt] {
            let file = VocabularyTransfer.write(roundTripEntries, as: format)
            let persistence = PersistenceController(inMemory: true)

            let summary = try persistence.importVocabularyEntries(
                VocabularyTransfer.read(Data(file.utf8), as: format))

            #expect(summary == VocabularyImportSummary(added: roundTripEntries.count, updated: 0, skipped: 0), "\(format)")
            #expect(storedEntries(persistence) == roundTripEntries, "\(format)")
        }
    }

    @Test("export from a store and import into the same store changes nothing")
    func roundTripIntoTheSameStore() throws {
        let persistence = PersistenceController(inMemory: true)
        _ = try persistence.importVocabularyEntries(
            VocabularyTransferParseResult(entries: roundTripEntries, skipped: 0))
        let before = storedEntries(persistence)
        #expect(before == roundTripEntries)

        for format in [VocabularyTransferFormat.csv, .txt] {
            // The store's own export: the page's display order, not file order.
            let exported = persistence.vocabularyEntriesForExport()
            #expect(Set(exported.map(\.word)) == Set(roundTripEntries.map(\.word)))
            let file = VocabularyTransfer.write(exported, as: format)

            let summary = try persistence.importVocabularyEntries(
                VocabularyTransfer.read(Data(file.utf8), as: format))

            #expect(summary == VocabularyImportSummary(added: 0, updated: 0, skipped: 0), "\(format)")
            #expect(storedEntries(persistence) == before, "\(format)")
        }
    }

    @Test("a words-only TXT import keeps an existing replacement")
    func wordsOnlyTXTKeepsReplacement() throws {
        let persistence = PersistenceController(inMemory: true)
        #expect(persistence.addVocabularyItem(word: "Kubernetes", replacement: "K8s"))

        let summary = try persistence.importVocabularyEntries(
            VocabularyTransfer.read(Data("kubernetes\nHelm\n".utf8), as: .txt))

        #expect(summary == VocabularyImportSummary(added: 1, updated: 0, skipped: 0))
        #expect(storedEntries(persistence) == [
            VocabularyTransferEntry(word: "Kubernetes", replacement: "K8s"),
            VocabularyTransferEntry(word: "Helm", replacement: nil),
        ])
    }

    @Test("an existing word keeps its spelling and position and takes a new replacement")
    func existingWordTakesFileReplacement() throws {
        let persistence = PersistenceController(inMemory: true)
        #expect(persistence.addVocabularyItem(word: "alpha", replacement: nil))
        #expect(persistence.addVocabularyItem(word: "Beta", replacement: "old"))

        let csv = "word,replacement\r\ngamma,\r\nBETA,new\r\nALPHA,A\r\n"
        let summary = try persistence.importVocabularyEntries(
            VocabularyTransfer.read(Data(csv.utf8), as: .csv))

        #expect(summary == VocabularyImportSummary(added: 1, updated: 2, skipped: 0))
        #expect(storedEntries(persistence) == [
            VocabularyTransferEntry(word: "alpha", replacement: "A"),
            VocabularyTransferEntry(word: "Beta", replacement: "new"),
            VocabularyTransferEntry(word: "gamma", replacement: nil),
        ])
    }

    @Test("a word repeated in the file keeps its first position; a later replacement wins")
    func duplicatesInsideTheFile() throws {
        let persistence = PersistenceController(inMemory: true)
        let txt = "one\ttwo\nOne\nzwei\nONE\tuno\n"
        let summary = try persistence.importVocabularyEntries(
            VocabularyTransfer.read(Data(txt.utf8), as: .txt))

        #expect(summary == VocabularyImportSummary(added: 2, updated: 0, skipped: 0))
        #expect(storedEntries(persistence) == [
            VocabularyTransferEntry(word: "one", replacement: "uno"),
            VocabularyTransferEntry(word: "zwei", replacement: nil),
        ])
    }

    @Test("over-long words are counted in the summary and never stored")
    func overLongWordsAreSkipped() throws {
        let persistence = PersistenceController(inMemory: true)
        let long = String(repeating: "x", count: PersistenceController.vocabularyMaxWordChars + 1)
        let exact = String(repeating: "y", count: PersistenceController.vocabularyMaxWordChars)
        let summary = try persistence.importVocabularyEntries(
            VocabularyTransfer.read(Data("\(long)\n\(exact)\n".utf8), as: .txt))

        #expect(summary == VocabularyImportSummary(added: 1, updated: 0, skipped: 1))
        #expect(storedEntries(persistence).map(\.word) == [exact])
    }
}
