//
//  TruncatedLocalModelTests.swift
//  hyperwhisperTests
//
//  Issue #1632 (the macOS side of #1598): a damaged local model must not count
//  as installed. A Whisper ggml file has to be its exact catalog size, and a
//  FluidAudio Parakeet version whose load failed is flagged broken so its
//  Library row offers Re-download.
//

import Foundation
import Testing
@testable import HyperWhisper

@MainActor
struct TruncatedLocalModelTests {

    private static var base: WhisperCppModel {
        WhisperModelManager.allModels.first { $0.name == "base" }!
    }

    @Test func whisperBaseAtItsExactSizeIsComplete() {
        #expect(Self.base.sizeInBytes == 147_951_465)
        #expect(WhisperModelManager.isCompleteModelFile(byteCount: 147_951_465, for: Self.base))
    }

    @Test func whisperBaseCutTo96PercentIsNotComplete() {
        // The #1598 repro: Base cut to 96%, 142,033,406 of 147,951,465 bytes.
        #expect(!WhisperModelManager.isCompleteModelFile(byteCount: 142_033_406, for: Self.base))
        #expect(!WhisperModelManager.isCompleteModelFile(byteCount: 147_951_464, for: Self.base))
        #expect(!WhisperModelManager.isCompleteModelFile(byteCount: 147_951_466, for: Self.base))
        #expect(!WhisperModelManager.isCompleteModelFile(byteCount: 0, for: Self.base))
        #expect(!WhisperModelManager.isCompleteModelFile(byteCount: nil, for: Self.base))
    }

    @Test func everyWhisperModelHasAnExactSize() {
        for model in WhisperModelManager.allModels {
            #expect(model.sizeInBytes > 0, "\(model.name) has no catalog byte size")
            #expect(WhisperModelManager.isCompleteModelFile(byteCount: model.sizeInBytes, for: model))
        }
    }

    @Test func parakeetBrokenFlagIsSetAndClearedByCanonicalId() {
        let manager = ParakeetModelManager()
        let v2 = ParakeetModelManager.Constants.v2ModelId
        let v3 = ParakeetModelManager.Constants.v3ModelId
        #expect(!manager.isModelBroken(v2))

        manager.markModelBroken(v2)
        #expect(manager.isModelBroken(v2))
        #expect(!manager.isModelBroken(v3))

        // The persisted V3 alias resolves to the same flag.
        manager.markModelBroken("parakeet-tdt-v3-multilingual")
        #expect(manager.isModelBroken(v3))

        manager.clearModelBroken(v2)
        manager.clearModelBroken(v3)
        #expect(manager.brokenModels.isEmpty)

        // An unknown id never becomes a flag.
        manager.markModelBroken("parakeet-tdt-0.6b-v9")
        #expect(manager.brokenModels.isEmpty)
    }

    /// The #1632 verify: a broken install whose folders were read-only survived
    /// Re-download, which then fetched nothing and showed Installed.
    @Test func aReadOnlyBrokenInstallIsStillRemoved() throws {
        let fileManager = FileManager.default
        let root: URL = fileManager.temporaryDirectory
            .appendingPathComponent("parakeet-broken-\(UUID().uuidString)", isDirectory: true)
        let encoder: URL = root.appendingPathComponent("Encoder.mlmodelc", isDirectory: true)
        let weights: URL = encoder.appendingPathComponent("weights", isDirectory: true)
        try fileManager.createDirectory(at: weights, withIntermediateDirectories: true)
        let weight: URL = weights.appendingPathComponent("weight.bin")
        try Data(repeating: 1, count: 64).write(to: weight)
        let folders: [URL] = [weights, encoder, root]
        try fileManager.setAttributes([.posixPermissions: 0o400], ofItemAtPath: weight.path)
        for folder in folders {
            try fileManager.setAttributes([.posixPermissions: 0o555], ofItemAtPath: folder.path)
        }
        defer {
            for folder in folders.reversed() {
                try? fileManager.setAttributes([.posixPermissions: 0o755], ofItemAtPath: folder.path)
            }
            try? fileManager.removeItem(at: root)
        }
        let plainRemoveFailed: Bool = (try? fileManager.removeItem(at: root)) == nil
        #expect(plainRemoveFailed)

        try ParakeetModelManager.removeDamagedInstall(at: root)
        let stillThere: Bool = fileManager.fileExists(atPath: root.path)
        #expect(!stillThere)
        // A folder that is already gone is not an error.
        try ParakeetModelManager.removeDamagedInstall(at: root)
    }

    private static func row(installed: Bool, status: LibraryModelStatus) -> LibraryModel {
        LibraryModel(
            id: "parakeet-parakeet-tdt-0.6b-v2",
            displayName: "Parakeet V2",
            providerKey: .parakeet,
            kind: .voice,
            location: .offline(sizeDescription: nil, installed: installed, downloadProgress: nil),
            speed: 5,
            accuracy: 3,
            tag: nil,
            status: status,
            supportsCustomVocabulary: false,
            availableViaHyperWhisperCloud: false
        )
    }

    /// The Library row of a broken install offers Re-download, not Remove (#1632 verify).
    @Test func onlyAnInstalledBrokenRowNeedsRedownload() {
        #expect(Self.row(installed: true, status: .downloadable(progress: nil)).needsRedownload)
        #expect(!Self.row(installed: true, status: .enabled).needsRedownload)
        #expect(!Self.row(installed: false, status: .downloadable(progress: nil)).needsRedownload)
    }
}
