//
//  RecordingsFolderRefusalTests.swift
//  hyperwhisperTests
//
//  Regression cover for issue #1473.
//
//  Choosing a read-only folder in Settings > Storage > Recordings Folder was
//  refused with no message. `changeRecordingsFolder(to:)` set a
//  `validationError` that no view read, so the chooser closed and the field
//  kept the old path. It now returns whether it accepted the folder, and both
//  choosers show `presentRecordingsFolderRefusedAlert()` on a refusal.
//
//  The refusal itself is tested by calling it on a real read-only folder. The
//  alert is AppKit and modal, so the two call sites are pinned by reading the
//  source, as `ModelLibraryRemoveConfirmationTests` does.
//

import Foundation
import Testing

@testable import HyperWhisper

@Suite("Recordings folder refusal is shown (#1473)", .serialized)
@MainActor
struct RecordingsFolderRefusalTests {

    private static let managerPath = "app/macos/hyperwhisper/Managers/Settings/StorageSettingsManager.swift"
    private static let viewPath = "app/macos/hyperwhisper/Views/Settings/StorageSettingsSection.swift"

    private static let keys = [
        "settings.storage.folderRefused.title",
        "settings.storage.folderRefused.notWritable",
        "settings.storage.folderRefused.createFailed",
    ]

    private func english(_ key: String) -> String {
        switch key {
        case "settings.storage.folderRefused.notWritable":
            return "You don't have permission to write to “%@”, so HyperWhisper can't save recordings there. The Recordings Folder was not changed."
        case "settings.storage.folderRefused.createFailed":
            return "HyperWhisper couldn't create this folder: %@ The Recordings Folder was not changed."
        default: return key
        }
    }

    // MARK: - Text

    @Test func theNotWritableMessageNamesTheFolder() {
        let message = StorageSettingsManager.folderNotWritableMessage(folderName: "ro", localize: english)
        #expect(message.hasPrefix("You don't have permission to write to “ro”"))
        #expect(message.hasSuffix("The Recordings Folder was not changed."))
    }

    @Test func theCreateFailedMessageCarriesTheSystemError() {
        let message = StorageSettingsManager.folderCreateFailedMessage(
            systemError: "You don't have permission.",
            localize: english
        )
        #expect(message == "HyperWhisper couldn't create this folder: You don't have permission. The Recordings Folder was not changed.")
    }

    // MARK: - Refusal

    @Test func aReadOnlyFolderIsRefusedAndTheSettingKept() throws {
        let fm = FileManager.default
        let ro = fm.temporaryDirectory.appendingPathComponent("hw-1473-ro-\(UUID().uuidString)")
        try fm.createDirectory(at: ro, withIntermediateDirectories: true)
        try fm.setAttributes([.posixPermissions: 0o555], ofItemAtPath: ro.path)
        defer {
            try? fm.setAttributes([.posixPermissions: 0o755], ofItemAtPath: ro.path)
            try? fm.removeItem(at: ro)
        }
        // Root can write anywhere; the refusal is then unreachable.
        try #require(!fm.isWritableFile(atPath: ro.path))

        let storage = StorageSettingsManager()
        let before = storage.recordingsFolder
        storage.validationError = nil

        #expect(storage.changeRecordingsFolder(to: ro) == false)
        #expect(storage.recordingsFolder == before)
        let error = try #require(storage.validationError)
        #expect(error.contains(ro.lastPathComponent))
    }

    // MARK: - Wiring

    @Test func theSettingsChooserShowsTheRefusal() throws {
        let body = try ProductionSource.slice(
            of: Self.viewPath,
            from: "onFolderChange: { url in",
            to: "Divider()"
        )
        #expect(body.contains("if !settingsManager.changeRecordingsFolder(to: url)"))
        #expect(body.contains("settingsManager.presentRecordingsFolderRefusedAlert()"))
    }

    @Test func theRecoveryChooserShowsTheRefusalAndReportsIt() throws {
        let body = try ProductionSource.slice(
            of: Self.managerPath,
            from: "func offerManualFolderSelection() -> Bool {",
            to: "func presentStorageRecoveryPrompt()"
        )
        #expect(body.contains("guard changeRecordingsFolder(to: url) else {"))
        #expect(body.contains("presentRecordingsFolderRefusedAlert()"))
    }

    @Test func everyLocaleHasTheKeysWithOneFormatSpecifierAtMost() throws {
        let localizations = ProductionSource.url("app/macos/hyperwhisper/Localizations")
        let locales = try FileManager.default.contentsOfDirectory(
            at: localizations,
            includingPropertiesForKeys: nil
        ).filter { $0.pathExtension == "lproj" }
        #expect(locales.count == 40)

        for locale in locales {
            let lines = try ProductionSource.text(of: locale.appendingPathComponent("Localizable.strings"))
                .components(separatedBy: .newlines)
            for key in Self.keys {
                let matches = lines.filter { $0.hasPrefix("\"\(key)\" = \"") }
                #expect(matches.count == 1, "\(locale.lastPathComponent) needs exactly one \(key)")
                guard let line = matches.first else { continue }
                #expect(line.trimmingCharacters(in: .whitespaces).hasSuffix("\";"), "\(locale.lastPathComponent) \(key)")
                // The title takes no argument; the two messages take one %@.
                let wanted = key.hasSuffix(".title") ? 0 : 1
                #expect(line.components(separatedBy: "%").count - 1 == wanted, "\(locale.lastPathComponent) \(key)")
                #expect(line.components(separatedBy: "%@").count - 1 == wanted, "\(locale.lastPathComponent) \(key)")
            }
        }
    }
}
