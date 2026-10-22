//
//  RecordingsFolderFallbackFailureTests.swift
//  hyperwhisperTests
//
//  Regression cover for issue #773.
//
//  When Application Support, Downloads and the temporary directory all
//  refused the recordings folder, `useAlternateStorageInstead()` had already
//  set `userChoseAlternateStorage` and closed the alert, and it ignored the
//  failure. The folder stayed unwritable, nothing was recorded, and every
//  later prepare took the "user already chose" branch, failed the same way and
//  returned, so the question was never asked again.
//
//  A `FileManager` that refuses the folders reaches that path without touching
//  the disk. The recovery prompt is a modal NSAlert, so a recorder stands in.
//
//  These tests live in the #1473 suite: both write the recordings-folder
//  settings in the shared UserDefaults, and that suite is `.serialized`.
//

import Foundation
import Testing

@testable import HyperWhisper

/// Refuses every folder `refuses` matches: it cannot be created, and does not
/// exist. Everything else goes to the real file system.
private final class RefusingFileManager: FileManager {
    var refuses: (URL) -> Bool = { _ in true }
    private(set) var refusedCreations: [String] = []

    override func createDirectory(
        at url: URL,
        withIntermediateDirectories createIntermediates: Bool,
        attributes: [FileAttributeKey: Any]? = nil
    ) throws {
        if refuses(url) {
            refusedCreations.append(url.path)
            throw CocoaError(.fileWriteNoPermission, userInfo: [NSFilePathErrorKey: url.path])
        }
        try super.createDirectory(at: url, withIntermediateDirectories: createIntermediates, attributes: attributes)
    }

    override func fileExists(atPath path: String, isDirectory: UnsafeMutablePointer<ObjCBool>?) -> Bool {
        refuses(URL(fileURLWithPath: path)) ? false : super.fileExists(atPath: path, isDirectory: isDirectory)
    }

    override func isWritableFile(atPath path: String) -> Bool {
        refuses(URL(fileURLWithPath: path)) ? false : super.isWritableFile(atPath: path)
    }
}

/// The stored settings these tests change, put back afterwards.
@MainActor
private struct StorageDefaultsSnapshot {
    private static let keys = [
        "recordingsFolder",
        "appFolderPath",
        "documentsPermissionExplained",
        "documentsAccessDenied",
        "userChoseAlternateStorage",
    ]
    private let values: [String: Any?]

    init() {
        values = Dictionary(uniqueKeysWithValues: Self.keys.map { ($0, UserDefaults.standard.object(forKey: $0)) })
    }

    func restore() {
        for (key, value) in values {
            if let value {
                UserDefaults.standard.set(value, forKey: key)
            } else {
                UserDefaults.standard.removeObject(forKey: key)
            }
        }
    }
}

extension RecordingsFolderRefusalTests {

    /// A manager whose folder checks go through `fileManager`, with the
    /// Documents question unanswered and the recovery prompt recorded.
    private func makeStorage(_ fileManager: RefusingFileManager) -> (StorageSettingsManager, () -> Int) {
        let storage = StorageSettingsManager(fileManager: fileManager)
        storage.documentsPermissionExplained = false
        storage.documentsAccessDenied = false
        storage.userChoseAlternateStorage = false
        var prompts = 0
        storage.requestStorageRecoveryPrompt = { prompts += 1 }
        return (storage, { prompts })
    }

    @Test func useAnotherLocationWithEveryFallbackRefusedAsksAgainInsteadOfBurningTheChoice() {
        let snapshot = StorageDefaultsSnapshot()
        defer { snapshot.restore() }
        let fm = RefusingFileManager()
        let (storage, prompts) = makeStorage(fm)
        let before = storage.recordingsFolder
        storage.showDocumentsPermissionAlert = true

        storage.useAlternateStorageInstead()

        #expect(fm.refusedCreations.count == 3, "Application Support, Downloads and temp are each tried once")
        #expect(storage.userChoseAlternateStorage == false)
        #expect(storage.documentsPermissionExplained == false, "the Documents question is asked again next time")
        #expect(prompts() == 1, "the user is asked to choose a folder")
        #expect(storage.recordingsFolder == before)
        #expect(storage.showDocumentsPermissionAlert == false)
    }

    @Test func useAnotherLocationRecordsTheChoiceWhenAFallbackWorks() throws {
        let snapshot = StorageDefaultsSnapshot()
        defer { snapshot.restore() }
        let fm = RefusingFileManager()
        // Only the temporary directory may be created.
        let temp = FileManager.default.temporaryDirectory.standardizedFileURL.path
        fm.refuses = { !$0.standardizedFileURL.path.hasPrefix(temp) }
        let (storage, prompts) = makeStorage(fm)

        storage.useAlternateStorageInstead()

        #expect(fm.refusedCreations.count == 2)
        #expect(storage.userChoseAlternateStorage == true)
        #expect(storage.documentsPermissionExplained == true)
        #expect(prompts() == 0)
        #expect(storage.recordingsFolder.hasSuffix("HyperWhisper/recordings"))
        #expect(URL(fileURLWithPath: storage.recordingsFolder).standardizedFileURL.path.hasPrefix(temp))
    }

    @Test func continueWithDocumentsRefusedAndEveryFallbackRefusedAsksToChooseAFolder() {
        let snapshot = StorageDefaultsSnapshot()
        defer { snapshot.restore() }
        let fm = RefusingFileManager()
        let (storage, prompts) = makeStorage(fm)

        storage.proceedWithDocumentsAccess()

        #expect(fm.refusedCreations.count == 4, "Documents, then the 3 fallbacks")
        #expect(storage.documentsAccessDenied == true)
        #expect(prompts() == 1)
    }

    @Test func aRecordingStartWithEveryFallbackRefusedReportsNotReadyAtOnce() async {
        let snapshot = StorageDefaultsSnapshot()
        defer { snapshot.restore() }
        let fm = RefusingFileManager()
        let (storage, prompts) = makeStorage(fm)
        // Documents was already refused, so nothing is left to ask about.
        storage.documentsPermissionExplained = true
        storage.documentsAccessDenied = true

        let start = Date()
        let ready = await storage.prepareRecordingsFolderIfNeededAsync(timeoutSeconds: 30)

        #expect(ready == false)
        // The Documents create, then the 3 fallbacks, each once: the wait does
        // not run the chain the prepare step just saw fail.
        #expect(fm.refusedCreations.count == 4)
        // It used to retry every 300ms until the timeout; its caller asks now.
        #expect(Date().timeIntervalSince(start) < 5)
        #expect(prompts() == 0, "the caller shows the recovery prompt, not the manager")
    }

    @Test func noFallbackCatchDropsTheErrorUnlogged() throws {
        let source = try ProductionSource.text(
            of: ProductionSource.url("app/macos/hyperwhisper/Managers/Settings/StorageSettingsManager.swift")
        )
        let lines = source.components(separatedBy: .newlines)
        for (index, line) in lines.enumerated() where line.hasSuffix("} catch {") {
            let next = lines[index + 1].trimmingCharacters(in: .whitespaces)
            #expect(next != "return false", "line \(index + 2) drops the error with no log")
        }
    }
}
