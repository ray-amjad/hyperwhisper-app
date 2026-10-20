//
//  FileTranscriptionOpenModelLibraryTests.swift
//  hyperwhisperTests
//
//  Regression cover for issue #2035.
//
//  When a file transcription failed because the mode's local model was not
//  downloaded, the "Transcription Error" alert offered only OK, and the user
//  had to find Model Library on their own. The alert now also offers "Open
//  Model Library", which brings the main window forward on that page. Every
//  other error keeps its single OK button.
//
//  The button list and the click handling are static seams, tested by calling
//  them. The alert itself is wired in a `private` method, so that wiring is
//  pinned by reading the source, as `ModelessAlertTests` does.
//

import AppKit
import Foundation
import Testing

@testable import HyperWhisper

@MainActor
@Suite("File transcription error alert: Open Model Library (#2035)")
struct FileTranscriptionOpenModelLibraryTests {

    private static let fileFlowPath = "app/macos/hyperwhisper/Managers/Transcription/Flows/FileTranscriptionFlow.swift"
    private static let key = "transcription.error.openModelLibrary"

    // MARK: - Buttons

    @Test func aMissingModelOffersOpenModelLibraryThenOK() {
        let buttons = FileTranscriptionFlow.errorAlertButtons(for: TranscriptionError.modelNotDownloaded())
        #expect(buttons == [.openModelLibrary, .ok])
    }

    @Test func aNamedMissingModelAlsoOffersOpenModelLibrary() {
        let named = TranscriptionError.modelNotDownloaded(model: "Tiny (Multilingual)", mode: "Dictation")
        #expect(FileTranscriptionFlow.errorAlertButtons(for: named) == [.openModelLibrary, .ok])
    }

    @Test func everyOtherErrorKeepsOKAlone() {
        let others: [Error?] = [
            nil,
            TranscriptionError.modelProtected,
            TranscriptionError.audioFileNotFound,
            TranscriptionError.apiKeyMissing(provider: "OpenAI"),
            TranscriptionError.providerNotAvailable(provider: "Parakeet", reason: nil),
            TranscriptionError.transientNetwork(details: nil),
            TranscriptionError.noSpeechDetected,
            FileImportFailure.pipelineUnavailable,
            FileTranscriptionError.cannotReadFile,
            CancellationError(),
            NSError(domain: "test", code: 1),
        ]
        for error in others {
            #expect(FileTranscriptionFlow.errorAlertButtons(for: error) == [.ok], "\(String(describing: error))")
        }
    }

    // MARK: - Clicks

    @Test func openModelLibraryRunsTheActionAndOKDoesNot() {
        let buttons = FileTranscriptionFlow.errorAlertButtons(for: TranscriptionError.modelNotDownloaded())
        var opened = 0

        FileTranscriptionFlow.performErrorAlertAction(for: .alertSecondButtonReturn, buttons: buttons) { opened += 1 }
        #expect(opened == 0)

        FileTranscriptionFlow.performErrorAlertAction(for: .alertFirstButtonReturn, buttons: buttons) { opened += 1 }
        #expect(opened == 1)
    }

    @Test func aOneButtonAlertNeverOpensModelLibrary() {
        let buttons = FileTranscriptionFlow.errorAlertButtons(for: TranscriptionError.modelProtected)
        var opened = false
        for response in [NSApplication.ModalResponse.alertFirstButtonReturn, .alertSecondButtonReturn, .abort] {
            FileTranscriptionFlow.performErrorAlertAction(for: response, buttons: buttons) { opened = true }
        }
        #expect(!opened)
    }

    // MARK: - Navigation

    @Test func openModelLibraryShowsTheWindowAndModelLibrary() {
        let appState = AppState()
        appState.selectedNavigationItem = .history
        var windowShown = false

        FileTranscriptionFlow.openModelLibrary(appState: appState) { windowShown = true }

        #expect(windowShown)
        #expect(appState.selectedNavigationItem == .modelLibrary)
    }

    @Test func aModeEditorWithUnsavedChangesAsksFirstLikeASidebarPick() {
        let appState = AppState()
        appState.selectedNavigationItem = .modes
        let session = UUID()
        appState.modeEditorDidOpen(session: session)
        appState.modeEditor(session: session, hasUnsavedChanges: true)

        FileTranscriptionFlow.openModelLibrary(appState: appState) {}

        // A user-chosen jump: staged for the editor's discard prompt, not
        // dropped (automatic) and not forced through.
        #expect(appState.selectedNavigationItem == .modes)
        #expect(appState.pendingModeEditorNavigation == .modelLibrary)
    }

    // MARK: - Wiring

    @Test func theCatchAllAlertPassesTheErrorAndHandlesTheClick() throws {
        // The catch-all arm, where a provider's `.modelNotDownloaded` lands.
        let catchAll = try ProductionSource.slice(
            of: Self.fileFlowPath,
            from: "❌ File transcription failed:",
            to: "releaseRecordingState()"
        )
        #expect(catchAll.contains("showErrorAlert("))
        #expect(catchAll.contains("error: failure"))

        let source = try ProductionSource.code(of: Self.fileFlowPath)
        #expect(source.contains("Self.errorAlertButtons(for: error)"))
        #expect(source.contains("Self.performErrorAlertAction(for: response, buttons: buttons)"))
        #expect(source.contains("requestNavigation(to: .modelLibrary, trigger: .userChosen)"))
    }

    // MARK: - Copy

    @Test func everyLocaleHasTheButtonRightAfterTheMissingModelText() throws {
        let localizations = ProductionSource.url("app/macos/hyperwhisper/Localizations")
        let locales = try FileManager.default.contentsOfDirectory(
            at: localizations,
            includingPropertiesForKeys: nil
        ).filter { $0.pathExtension == "lproj" }
        #expect(locales.count == 40)

        for locale in locales {
            let lines = try ProductionSource.text(of: locale.appendingPathComponent("Localizable.strings"))
                .components(separatedBy: .newlines)
            let matches = lines.indices.filter { lines[$0].hasPrefix("\"\(Self.key)\" = \"") }
            #expect(matches.count == 1, "\(locale.lastPathComponent) needs exactly one \(Self.key)")
            guard let index = matches.first else { continue }
            let line = lines[index]
            #expect(line.trimmingCharacters(in: .whitespaces).hasSuffix("\";"), "\(locale.lastPathComponent)")
            #expect(!line.contains("%"), "\(locale.lastPathComponent) takes no format arguments")
            #expect(line != "\"\(Self.key)\" = \"\";", "\(locale.lastPathComponent) is empty")
            #expect(index > 0 && lines[index - 1].hasPrefix("\"transcription.error.modelNotDownloaded"),
                    "\(locale.lastPathComponent) places it after transcription.error.modelNotDownloaded and its variants")
        }
    }
}
