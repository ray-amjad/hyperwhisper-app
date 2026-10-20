//
//  MissingModelNamedTests.swift
//  hyperwhisperTests
//
//  Issue #1518: a mode whose local model was not downloaded failed with
//  "Please download a model first", and History did not say which engine
//  failed. The alert now names the mode and the model ("W15 uses Tiny
//  (English-only), which is not downloaded. Download it in Model Library."),
//  and the failed row stores the engine the way a completed row does.
//

import CoreData
import FluidAudio
import Foundation
import Testing
@testable import HyperWhisper

@MainActor
@Suite("A missing local model names the model, the mode and the engine")
struct MissingModelNamedTests {

    private static let oldKey = "transcription.error.modelNotDownloaded"
    private static let modelAndModeKey = "transcription.error.modelNotDownloaded.modelAndMode"
    private static let modelKey = "transcription.error.modelNotDownloaded.model"

    private static let localizations = "app/macos/hyperwhisper/Localizations"
    private static let providersDir = "app/macos/hyperwhisper/Managers/Transcription/Providers/Local"
    private static let fileFlowPath = "app/macos/hyperwhisper/Managers/Transcription/Flows/FileTranscriptionFlow.swift"
    private static let errorHandlingPath =
        "app/macos/hyperwhisper/Managers/AudioRecording/RecordingFlow/RecordingTranscriptionFlow+ErrorHandling.swift"

    // MARK: - Helpers

    /// One locale's Localizable.strings, one entry per line. Split on "\n"
    /// only, with a trailing "\r" dropped: some files mix CRLF and LF, and
    /// `.newlines` would count a CRLF as two lines.
    nonisolated private static func lines(of locale: String) throws -> [String] {
        let url = ProductionSource.url("\(localizations)/\(locale).lproj/Localizable.strings")
        return try ProductionSource.text(of: url)
            .components(separatedBy: "\n")
            .map { $0.hasSuffix("\r") ? String($0.dropLast()) : $0 }
    }

    /// The value of `key` in one locale's Localizable.strings, read from the
    /// source file. `nil` when the key is missing.
    nonisolated private static func value(of key: String, in locale: String) throws -> String? {
        let prefix = "\"\(key)\" = \""
        let lines = try Self.lines(of: locale)
        guard let line = lines.first(where: { $0.hasPrefix(prefix) }) else { return nil }
        var rest = Substring(line.dropFirst(prefix.count))
        rest = Substring(rest.trimmingCharacters(in: .whitespaces))
        guard rest.hasSuffix("\";") else { return nil }
        return String(rest.dropLast(2))
    }

    /// Key → the English (Base) format string, so the English assertions do not
    /// depend on the test process's language.
    nonisolated private static func english(_ key: String) -> String {
        (try? value(of: key, in: "Base")) ?? key
    }

    /// The format specifiers in a value, in order of appearance.
    nonisolated private static func specifiers(in value: String) -> [String] {
        let regex = try! NSRegularExpression(pattern: "%(?:[0-9]+\\$)?(?:@|l{0,2}[dDuUxXoOfeEgGcCsSp]|%)")
        let range = NSRange(value.startIndex..., in: value)
        return regex.matches(in: value, range: range).compactMap { match in
            Range(match.range, in: value).map { String(value[$0]) }
        }
    }

    private static func providerCode(_ file: String) throws -> String {
        try ProductionSource.code(of: "\(providersDir)/\(file)")
    }

    private func makeFlow(_ persistence: PersistenceController) -> FileTranscriptionFlow {
        FileTranscriptionFlow(
            transcriptionPipeline: nil,
            settingsManager: nil,
            appState: nil,
            licenseManager: nil,
            persistence: persistence
        )
    }

    private func reload(_ id: NSManagedObjectID, in persistence: PersistenceController) throws -> Transcript {
        let context = persistence.container.viewContext
        context.refreshAllObjects()
        return try #require(try context.existingObject(with: id) as? Transcript)
    }

    // MARK: - The English text

    @Test func modelAndModeNameBoth() {
        let text = TranscriptionError.modelNotDownloadedMessage(
            model: "Tiny (English-only)",
            mode: "W15",
            localize: Self.english
        )
        #expect(text == "W15 uses Tiny (English-only), which is not downloaded. Download it in Model Library.")
    }

    @Test func modelOnlyNamesTheModel() {
        let text = TranscriptionError.modelNotDownloadedMessage(
            model: "Parakeet V3 (Multilingual)",
            mode: nil,
            localize: Self.english
        )
        #expect(text == "Parakeet V3 (Multilingual) is not downloaded. Download it in Model Library.")
    }

    /// A mode with no model name says nothing about the mode: the old text.
    @Test func neitherKeepsTheOldText() {
        #expect(TranscriptionError.modelNotDownloadedMessage(model: nil, mode: nil, localize: Self.english)
            == "Please download a model first")
        #expect(TranscriptionError.modelNotDownloadedMessage(model: nil, mode: "W15", localize: Self.english)
            == "Please download a model first")
        #expect(TranscriptionError.modelNotDownloadedMessage(model: "  ", mode: "W15", localize: Self.english)
            == "Please download a model first")
    }

    @Test func anEmptyModeNameIsNoMode() {
        let text = TranscriptionError.modelNotDownloadedMessage(model: "Qwen3 ASR", mode: " ", localize: Self.english)
        #expect(text == "Qwen3 ASR is not downloaded. Download it in Model Library.")
    }

    /// `errorDescription` goes through the same message, in whatever language
    /// the test runs in: both names are in it.
    @Test func errorDescriptionCarriesBothNames() throws {
        let error = TranscriptionError.modelNotDownloaded(model: "Tiny (English-only)", mode: "W15")
        let text = try #require(error.errorDescription)
        #expect(text.contains("W15"))
        #expect(text.contains("Tiny (English-only)"))
        #expect(TranscriptionError.modelNotDownloaded().errorDescription
            == TranscriptionError.modelNotDownloadedMessage(model: nil, mode: nil))
    }

    // MARK: - Every locale

    /// Every locale has both keys, once each, with the specifiers Base has,
    /// right after the old key, which stays as the fallback.
    @Test func everyLocaleHasTheNewKeysRightAfterTheOldOne() throws {
        let root = ProductionSource.url(Self.localizations)
        let locales = try FileManager.default.contentsOfDirectory(at: root, includingPropertiesForKeys: nil)
            .filter { $0.pathExtension == "lproj" }
            .map { $0.deletingPathExtension().lastPathComponent }
        #expect(locales.count == 40)

        for locale in locales {
            let lines = try Self.lines(of: locale)
            let starts = { (key: String) in
                lines.indices.filter { lines[$0].hasPrefix("\"\(key)\" = \"") }
            }
            let old = starts(Self.oldKey)
            let both = starts(Self.modelAndModeKey)
            let model = starts(Self.modelKey)
            #expect(old.count == 1, "\(locale) keeps \(Self.oldKey)")
            #expect(both.count == 1, "\(locale) needs one \(Self.modelAndModeKey)")
            #expect(model.count == 1, "\(locale) needs one \(Self.modelKey)")
            guard let o = old.first, let b = both.first, let m = model.first else { continue }
            #expect(b == o + 1 && m == o + 2, "\(locale): the new keys go right after \(Self.oldKey)")

            let bothValue = try #require(try Self.value(of: Self.modelAndModeKey, in: locale), "\(locale)")
            let modelValue = try #require(try Self.value(of: Self.modelKey, in: locale), "\(locale)")
            #expect(Self.specifiers(in: bothValue).sorted() == ["%1$@", "%2$@"], "\(locale) \(Self.modelAndModeKey)")
            #expect(Self.specifiers(in: modelValue) == ["%1$@"], "\(locale) \(Self.modelKey)")
            #expect(!bothValue.contains("\""), "\(locale): an unescaped quote")
            #expect(!modelValue.contains("\""), "\(locale): an unescaped quote")

            // The format really takes the two names in every language.
            let filled = String(format: bothValue, "W15", "Tiny (English-only)")
            #expect(filled.contains("W15") && filled.contains("Tiny (English-only)"), "\(locale)")
        }
    }

    // MARK: - Which names the engines use

    @Test func whisperUsesTheModelLibraryName() {
        #expect(LibWhisperProvider.modelDisplayName(for: "tiny.en") == WhisperModel.tinyEn.name)
        #expect(LibWhisperProvider.modelDisplayName(for: "large-v3_turbo") == WhisperModel.largeV3Turbo.name)
        // An id no WhisperModel has comes back as it is, never empty.
        #expect(LibWhisperProvider.modelDisplayName(for: "custom-model") == "custom-model")
    }

    @Test func nemotronParakeetAndQwenUseTheModelLibraryNames() {
        #expect(NemotronProvider.modelDisplayName(for: .latin) == NemotronModelManager.Constants.latinDisplayName)
        #expect(NemotronProvider.modelDisplayName(for: .multilingual)
            == NemotronModelManager.Constants.multilingualDisplayName)
        #expect(ParakeetProvider.modelDisplayName(for: .v2) == ParakeetModelManager.Constants.v2DisplayName)
        #expect(ParakeetProvider.modelDisplayName(for: .v3) == ParakeetModelManager.Constants.v3DisplayName)
    }

    /// The static engine names are what `name` returns, so a failed row stores
    /// what a completed row stores.
    @Test func engineNamesMatchTheProvidersNames() {
        #expect(NemotronProvider.engineName == NemotronProvider().name)
        #expect(ParakeetProvider.engineName == ParakeetProvider().name)
        if #available(macOS 15.0, *) {
            #expect(Qwen3AsrProvider.engineName == Qwen3AsrProvider().name)
        }
        if #available(macOS 26.0, *) {
            #expect(AppleSpeechAnalyzerProvider.engineName == AppleSpeechAnalyzerProvider().name)
        }
    }

    /// The real provider, where the multilingual model is not installed: the
    /// error names it as Model Library does.
    @Test(.enabled(if: !NemotronModelManager.isVariantInstalled(NemotronModelManager.Constants.multilingualModelId)))
    func nemotronThrowsWithTheModelName() async {
        let provider = NemotronProvider()
        do {
            try await provider.prepareIfNeeded(language: nil, modelId: NemotronModelManager.Constants.multilingualModelId)
            Issue.record("prepareIfNeeded did not throw for a model that is not installed")
        } catch let error as TranscriptionError {
            #expect(error.missingModelName == NemotronModelManager.Constants.multilingualDisplayName)
        } catch {
            Issue.record("unexpected error \(error)")
        }
    }

    /// Every throw site in the five local engines names its model: no bare
    /// `.modelNotDownloaded` is left, and each file still has its throws.
    @Test(arguments: [
        ("LibWhisperProvider.swift", 1),
        ("NemotronProvider.swift", 2),
        ("ParakeetProvider.swift", 2),
        ("Qwen3AsrProvider.swift", 1),
        ("AppleSpeechAnalyzerProvider.swift", 3),
    ])
    func everyThrowSiteNamesItsModel(file: String, throwCount: Int) throws {
        let code = try Self.providerCode(file)
        let all = code.components(separatedBy: "TranscriptionError.modelNotDownloaded").count - 1
        let named = code.components(separatedBy: "TranscriptionError.modelNotDownloaded(model: ").count - 1
        #expect(all == throwCount, "\(file) has \(all) modelNotDownloaded uses, expected \(throwCount)")
        #expect(named == all, "\(file): every modelNotDownloaded must pass model:")
    }

    // MARK: - Adding the mode

    @Test func withModeFillsTheModeOfANamedMissingModel() {
        let error = TranscriptionError.modelNotDownloaded(model: "Tiny (English-only)").withMode("W15")
        guard case .modelNotDownloaded(let model, let mode) = error else {
            Issue.record("withMode changed the case")
            return
        }
        #expect(model == "Tiny (English-only)")
        #expect(mode == "W15")
    }

    @Test func withModeLeavesEverythingElseAlone() {
        // No model name: the old text stays, with no mode.
        guard case .modelNotDownloaded(nil, nil) = TranscriptionError.modelNotDownloaded().withMode("W15") else {
            Issue.record("a bare modelNotDownloaded gained a mode")
            return
        }
        // A mode already named is kept.
        guard case .modelNotDownloaded(_, "First"?) =
                TranscriptionError.modelNotDownloaded(model: "Tiny", mode: "First").withMode("Second") else {
            Issue.record("withMode replaced a mode")
            return
        }
        // Another error is unchanged.
        guard case .noSpeechDetected = TranscriptionError.noSpeechDetected.withMode("W15") else {
            Issue.record("withMode changed another error")
            return
        }
        // No mode name: unchanged.
        guard case .modelNotDownloaded(_, nil) = TranscriptionError.modelNotDownloaded(model: "Tiny").withMode(" ") else {
            Issue.record("an empty mode name was added")
            return
        }
    }

    @Test func attachingModePassesOtherErrorsThrough() {
        let other = URLError(.timedOut)
        let result = TranscriptionError.attachingMode("W15", to: other)
        #expect((result as? URLError)?.code == .timedOut)

        let named = TranscriptionError.attachingMode("W15", to: TranscriptionError.modelNotDownloaded(model: "Tiny"))
        #expect(named.localizedDescription
            == TranscriptionError.modelNotDownloaded(model: "Tiny", mode: "W15").localizedDescription)
    }

    // MARK: - The failed row's engine

    @Test func localEngineNameIsWhatACompletedRowStores() {
        #expect(FileTranscriptionFlow.localEngineName(forModelId: "tiny.en") == WhisperModel.tinyEn.name)
        #expect(FileTranscriptionFlow.localEngineName(forModelId: " Tiny ") == WhisperModel.tiny.name)
        #expect(FileTranscriptionFlow.localEngineName(forModelId: ParakeetModelManager.Constants.v3ModelId)
            == ParakeetProvider.engineName)
        #expect(FileTranscriptionFlow.localEngineName(forModelId: NemotronModelManager.Constants.latinModelId)
            == NemotronProvider.engineName)
        if #available(macOS 15.0, *) {
            #expect(FileTranscriptionFlow.localEngineName(forModelId: Qwen3AsrModelManager.Constants.modelId)
                == Qwen3AsrProvider.engineName)
        }
        if #available(macOS 26.0, *) {
            #expect(FileTranscriptionFlow.localEngineName(forModelId: "apple-speech-analyzer")
                == AppleSpeechAnalyzerProvider.engineName)
        }
    }

    @Test func aCloudModeOrUnknownIdHasNoLocalEngine() {
        #expect(FileTranscriptionFlow.localEngineName(forModelId: nil) == nil)
        #expect(FileTranscriptionFlow.localEngineName(forModelId: "") == nil)
        #expect(FileTranscriptionFlow.localEngineName(forModelId: "cloud") == nil)
        #expect(FileTranscriptionFlow.localEngineName(forModelId: "something-else") == nil)
    }

    @Test func failedRowProviderNamesOnlyAnEngineFailure() {
        let missing = TranscriptionError.modelNotDownloaded(model: "Tiny (English-only)")
        #expect(FileTranscriptionFlow.failedRowProvider(for: missing, modelId: "tiny.en") == WhisperModel.tinyEn.name)
        // No local engine for the id: the missing model's own name.
        #expect(FileTranscriptionFlow.failedRowProvider(for: missing, modelId: "cloud") == "Tiny (English-only)")
        // A cloud failure with no model name: none.
        #expect(FileTranscriptionFlow.failedRowProvider(for: TranscriptionError.noSpeechDetected, modelId: "cloud") == nil)
        // Not an engine failure: none.
        #expect(FileTranscriptionFlow.failedRowProvider(for: CancellationError(), modelId: "tiny.en") == nil)
        #expect(FileTranscriptionFlow.failedRowProvider(for: FileTranscriptionError.cannotReadFile, modelId: "tiny.en") == nil)
    }

    // MARK: - The History row

    /// The writer stores the provider on a failed row.
    @Test func markTranscriptFailedWritesTheProvider() async throws {
        let persistence = PersistenceController(inMemory: true)
        let row = persistence.createProcessingTranscript(duration: 2, mode: "W15", audioFilePath: "/test/w15.wav")

        let saved = await persistence.markTranscriptFailedInBackground(
            transcriptID: row.objectID,
            failedReason: "reason",
            errorText: "",
            transcriptionProvider: "Tiny (English-only)"
        )
        #expect(saved)
        let reloaded = try reload(row.objectID, in: persistence)
        #expect(reloaded.value(forKey: "status") as? String == "failed")
        #expect(reloaded.value(forKey: "transcriptionProvider") as? String == "Tiny (English-only)")
    }

    /// No provider leaves the column as it was.
    @Test func markTranscriptFailedWithoutAProviderWritesNone() async throws {
        let persistence = PersistenceController(inMemory: true)
        let row = persistence.createProcessingTranscript(duration: 2, mode: "Cloud", audioFilePath: "/test/c.wav")

        await persistence.markTranscriptFailedInBackground(transcriptID: row.objectID, failedReason: "x", errorText: "")
        let reloaded = try reload(row.objectID, in: persistence)
        #expect(reloaded.value(forKey: "transcriptionProvider") as? String == nil)
    }

    /// A file import whose model is missing: the row's reason names the mode
    /// and the model, and the row stores the engine — the same composition the
    /// import's catch makes.
    @Test func fileImportRowNamesTheModeTheModelAndTheEngine() async throws {
        let persistence = PersistenceController(inMemory: true)
        let row = persistence.createProcessingTranscript(duration: 3, mode: "W15", audioFilePath: "/test/w15.wav")
        let flow = makeFlow(persistence)
        flow.processingTranscriptID = row.objectID

        let thrown = TranscriptionError.modelNotDownloaded(model: LibWhisperProvider.modelDisplayName(for: "tiny.en"))
        let failure = TranscriptionError.attachingMode("W15", to: thrown)
        await flow.markProcessingTranscriptFailed(
            after: failure,
            transcriptionProvider: FileTranscriptionFlow.failedRowProvider(for: thrown, modelId: "tiny.en")
        )

        let saved = try reload(row.objectID, in: persistence)
        let reason = try #require(saved.value(forKey: "failedReason") as? String)
        #expect(reason == FileTranscriptionFlow.failureMessage(for: failure, audioDuration: nil))
        #expect(reason.contains("W15"))
        #expect(reason.contains(WhisperModel.tinyEn.name))
        #expect(saved.value(forKey: "transcriptionProvider") as? String == WhisperModel.tinyEn.name)
    }

    // MARK: - The flows attach the mode (production source)

    @Test func theFileImportCatchAttachesTheModeAndTheEngine() throws {
        let catchAll = try ProductionSource.slice(
            of: Self.fileFlowPath,
            from: "} catch let error as FileTranscriptionError {",
            to: "nonisolated static func checkSourceReadable("
        )
        let generic = try #require(catchAll.range(of: "} catch {"))
        let body = catchAll[generic.upperBound...]
        #expect(body.contains("TranscriptionError.attachingMode(mode.name, to: error)"))
        #expect(body.contains("after: failure,"))
        #expect(body.contains("transcriptionProvider: Self.failedRowProvider(for: error, modelId: mode.model)"))
        #expect(body.contains("message: Self.failureMessage(for: failure, audioDuration: importDuration)"))
    }

    @Test func dictationErrorsAttachTheSessionModeAndTheEngine() throws {
        let body = try ProductionSource.slice(
            of: Self.errorHandlingPath,
            from: "func handleTranscriptionError(",
            to: "AppLogger.audio.error(\"❌ Transcription error: \\(error)\")"
        )
        #expect(body.contains("TranscriptionError.attachingMode(appState?.currentSessionModeName, to: error)"))
        #expect(body.contains("FileTranscriptionFlow.failedRowProvider(for: error, modelId: modeIdentity?.localEngine)"))
        #expect(body.contains("appState?.showError(failure.localizedDescription)"))
        #expect(body.contains("appState?.lastTranscription = \"Error: \\(failure.localizedDescription)\""))
        #expect(body.contains("failedReason: failure.localizedDescription"))
        // Both failed-row writes (streaming interrupted and the rest) store it.
        #expect(body.components(separatedBy: "transcriptionProvider: failedRowProvider").count - 1 == 2)
        #expect(!body.contains("showError(error.localizedDescription)"))
    }

    @Test func aPendingRetryFailureAttachesItsMode() throws {
        let body = try ProductionSource.slice(
            of: Self.errorHandlingPath,
            from: "private func retryTranscriptionFromPendingPath(",
            to: "func handleRecordingStartFailure("
        )
        let call = try #require(body.range(of: "transcribeWithDetails("))
        let catchArm = try #require(body.range(of: "} catch {", range: call.upperBound..<body.endIndex))
        let afterCatch = body[catchArm.upperBound...]
        #expect(afterCatch.contains("TranscriptionError.attachingMode(transcriptionMode.name, to: error)"))
        let save = try #require(afterCatch.range(of: "Self.savePendingRetryFailure("))
        let firstArgument = afterCatch[save.upperBound...].trimmingCharacters(in: .whitespacesAndNewlines)
        #expect(firstArgument.hasPrefix("failure,"), "the retry's failed row must get the error with its mode")
        #expect(afterCatch.contains("appState.lastTranscription = \"Error: \\(failure.localizedDescription)\""))
    }
}
