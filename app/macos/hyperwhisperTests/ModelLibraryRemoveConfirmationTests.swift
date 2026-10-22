//
//  ModelLibraryRemoveConfirmationTests.swift
//  hyperwhisperTests
//
//  Regression cover for issue #1527.
//
//  The trash icon on an installed local model in the Model Library deleted the
//  model file at once, with no prompt. A re-download is 78 MB for Whisper Tiny
//  and several GB for the large models. The click now only stages the model in
//  `pendingRemoval`; a confirmation ("Remove <name>? You will need to download
//  it again (<size>).") presents from it, and only its Remove button deletes.
//
//  The text comes from pure `LibraryModel` helpers, tested by calling them. The
//  wiring lives in `private` view methods that need live managers, so it is
//  pinned by reading the source, as `DeleteActiveModeTests` does for Modes.
//

import Foundation
import Testing

@testable import HyperWhisper

@Suite("Model Library remove confirmation (#1527)")
struct ModelLibraryRemoveConfirmationTests {

    private static let viewPath = "app/macos/hyperwhisper/Views/ModelLibrary/ModelLibraryView.swift"

    private static let keys = [
        "modelLibrary.remove.confirm.title",
        "modelLibrary.remove.confirm.message",
        "modelLibrary.remove.confirm.messageNoSize",
        "modelLibrary.remove.confirm.button",
    ]

    private func model(
        _ displayName: String = "Tiny (Multilingual)",
        location: LibraryModelLocation
    ) -> LibraryModel {
        LibraryModel(
            id: "whisper-tiny",
            displayName: displayName,
            providerKey: .localWhisper,
            kind: .voice,
            location: location,
            speed: 5,
            accuracy: 2,
            tag: nil,
            status: .enabled,
            supportsCustomVocabulary: false,
            availableViaHyperWhisperCloud: false
        )
    }

    /// The English Base values, so the format is pinned without depending on
    /// the language the test process happens to run in.
    private func english(_ key: String) -> String {
        switch key {
        case "modelLibrary.remove.confirm.title": return "Remove %@?"
        case "modelLibrary.remove.confirm.message": return "You will need to download it again (%@)."
        case "modelLibrary.remove.confirm.messageNoSize": return "You will need to download it again."
        default: return key
        }
    }

    // MARK: - Text

    @Test func theTitleNamesTheModel() {
        let row = model(location: .offline(sizeDescription: "78 MB", installed: true, downloadProgress: nil))
        #expect(row.removalConfirmationTitle(localize: english) == "Remove Tiny (Multilingual)?")
    }

    @Test func theMessageQuotesTheRowsDownloadSize() {
        let row = model(location: .offline(sizeDescription: "78 MB", installed: true, downloadProgress: nil))
        #expect(row.removalSizeDescription == "78 MB")
        #expect(row.removalConfirmationMessage(localize: english) == "You will need to download it again (78 MB).")
    }

    @Test func aRowWithNoSizeGetsTheNoSizeSentence() {
        for size in [nil, "", "   "] as [String?] {
            let row = model(location: .offline(sizeDescription: size, installed: true, downloadProgress: nil))
            #expect(row.removalSizeDescription == nil)
            #expect(row.removalConfirmationMessage(localize: english) == "You will need to download it again.")
        }
        let cloud = model(location: .cloud)
        #expect(cloud.removalSizeDescription == nil)
    }

    @Test func everyLocaleHasTheKeysWithValidFormatSpecifiers() throws {
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
                // The title and the sized message take one %@; the others none.
                let wanted = (key.hasSuffix(".title") || key.hasSuffix(".message")) ? 1 : 0
                let specifiers = line.components(separatedBy: "%").count - 1
                #expect(specifiers == wanted, "\(locale.lastPathComponent) \(key) has \(specifiers) %")
                #expect(line.components(separatedBy: "%@").count - 1 == wanted, "\(locale.lastPathComponent) \(key)")
            }
        }
    }

    // MARK: - Wiring

    @Test func theTrashClickStagesTheModelAndDeletesNothing() throws {
        let body = try ProductionSource.slice(
            of: Self.viewPath,
            from: "private func triggerDelete(for model: LibraryModel) {",
            to: "private func confirmRemoval(of model: LibraryModel) {"
        )
        // The in-use alert still comes first.
        #expect(body.contains("showCannotDeleteAlertIfNeeded("))
        #expect(body.contains("pendingRemoval = model"))
        #expect(!body.contains("performDelete("))
        #expect(!body.contains("deleteModel("))
    }

    @Test func onlyTheConfirmationDeletesTheFile() throws {
        let source = try ProductionSource.code(of: Self.viewPath)
        // Exactly one call site of the delete, and it is the Remove path.
        #expect(source.components(separatedBy: "performDelete(for: model)").count - 1 == 1)

        let confirm = try ProductionSource.slice(
            of: Self.viewPath,
            from: "private func confirmRemoval(of model: LibraryModel) {",
            to: "private func removalBlocker(for model: LibraryModel)"
        )
        #expect(confirm.contains("performDelete(for: model)"))

        // Every manager delete lives inside performDelete, nowhere else.
        let perform = try ProductionSource.slice(
            of: Self.viewPath,
            from: "private func performDelete(for model: LibraryModel) {",
            to: "private func modesUsingVoiceModel("
        )
        let managerDeletes = source.components(separatedBy: ".deleteModel(").count - 1
        #expect(managerDeletes == 5)
        #expect(perform.components(separatedBy: ".deleteModel(").count - 1 == managerDeletes)
    }

    @Test func theDialogPresentsFromThePendingModel() throws {
        let body = try ProductionSource.slice(
            of: Self.viewPath,
            from: "var body: some View {",
            to: "private var searchBar: some View {"
        )
        #expect(body.contains(".confirmationDialog("))
        #expect(body.contains("presenting: pendingRemoval"))
        #expect(body.contains("get: { pendingRemoval != nil }"))
        #expect(body.contains("role: .destructive"))
        #expect(body.contains("confirmRemoval(of: model)"))
        #expect(body.contains("\"common.cancel\""))
    }
}
