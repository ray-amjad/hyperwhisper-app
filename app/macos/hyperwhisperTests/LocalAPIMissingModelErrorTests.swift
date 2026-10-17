//
//  LocalAPIMissingModelErrorTests.swift
//  hyperwhisperTests
//
//  Issue #1493: `POST /transcribe` engine-only form (no `mode_id`).
//
//  1. `engine: whisper` / `whisperLocal` with no `model` answered
//     ENGINE_UNAVAILABLE "Whisper is unavailable: Missing 'model' for
//     whisperLocal engine" with the hint "Try again later, or pick a different
//     engine." It is a request mistake that fails the same way every time, so
//     a client that branches on `code` retried it forever. Ray (inbox #295):
//     INVALID_REQUEST, "A Whisper model is required.", a hint to send `model`,
//     and no default model.
//  2. `engine: nemotron` with no `model` keeps its default
//     (`nemotron-asr-3.5-multilingual`), but when that model is not installed
//     the MODEL_NOT_INSTALLED error now names it and suggests the installed
//     variant, instead of "Required model is not installed."
//
//  The seam is the one `TranscribeEndpoint.handle` uses for the engine-only
//  form: `TranscriptionProviderRouter.resolveProvider` throws, and
//  `LocalAPIResponder.mapTranscriptionError` turns the throw into the
//  `(code, message, hint)` the envelope carries. Both are the real production
//  functions; `handle` itself needs a live `TranscriptionPipeline`, which no
//  test in this target stands up.
//

import Foundation
import Testing
@testable import HyperWhisper

@MainActor
struct LocalAPIMissingModelErrorTests {

    private static let whisperSpellings = ["whisper", "whisperLocal", "libwhisper", "WhisperLocal", " whisper "]

    /// What the endpoint would put on the wire for `engine` + `model`, or nil
    /// when `resolveProvider` did not throw.
    private static func wireFailure(
        router: TranscriptionProviderRouter,
        engine: String,
        model: String?
    ) async -> (code: LocalAPIErrorCode, message: String, hint: String?)? {
        do {
            _ = try await router.resolveProvider(engine: engine, model: model, language: nil)
            return nil
        } catch {
            let (code, message, hint) = LocalAPIResponder.mapTranscriptionError(error)
            return (code, message, hint)
        }
    }

    // MARK: - Whisper with no model

    /// Issue steps 2 and 3: no `model` (and a blank one) with every Whisper
    /// spelling is INVALID_REQUEST with the portable head's message and a
    /// send-`model` hint. Never ENGINE_UNAVAILABLE, never "Try again later".
    @Test func whisperWithNoModelIsInvalidRequestWithASendModelHint() async {
        let router = TranscriptionProviderRouter()
        for engine in Self.whisperSpellings {
            for model in [nil, "", "  ", "\t\n"] as [String?] {
                let label = "engine '\(engine)' model \(model.map { "'\($0)'" } ?? "nil")"
                let failure = await Self.wireFailure(router: router, engine: engine, model: model)
                #expect(failure != nil, "\(label) was not refused")
                #expect(failure?.code == .invalidRequest, "\(label) answered \(String(describing: failure?.code))")
                #expect(failure?.message == "A Whisper model is required.", "\(label)")
                #expect(failure?.hint == "Send model, for example base.en.", "\(label)")
                #expect(failure?.hint?.contains("Try again later") != true, "\(label) still says to retry")
            }
        }
    }

    /// The same error on both `/transcribe` forms: the mixed mode_id + engine
    /// validator (#1466) throws the identical value, so a caller cannot get
    /// two answers for one omission.
    @Test func bothFormsRefuseABlankWhisperModelWithTheSameBytes() async throws {
        let router = TranscriptionProviderRouter()
        let engineOnlyFailure = await Self.wireFailure(router: router, engine: "whisper", model: "")
        let engineOnly = try #require(engineOnlyFailure)

        var mixed: (LocalAPIErrorCode, String, String?)?
        do {
            try TranscribeEndpoint.validateMixedPathEngineModel(engine: "whisper", model: "")
        } catch {
            mixed = LocalAPIResponder.mapTranscriptionError(error)
        }
        let mixedFailure = try #require(mixed)
        #expect(mixedFailure.0 == engineOnly.code)
        #expect(mixedFailure.1 == engineOnly.message)
        #expect(mixedFailure.2 == engineOnly.hint)
    }

    // MARK: - Nemotron, default model not installed

    /// The named error itself, with the disk stubbed: the default that was
    /// tried is named, and the installed variant is suggested.
    @Test func nemotronNotInstalledNamesTheTriedModelAndSuggestsTheInstalledOne() {
        let multilingual = NemotronModelManager.Constants.multilingualModelId
        let latin = NemotronModelManager.Constants.latinModelId

        let onlyLatin = TranscribeEndpoint.APIInputError.nemotronModelNotInstalled(
            multilingual,
            isInstalled: { $0 == latin }
        )
        let (code, message, hint) = LocalAPIResponder.mapTranscriptionError(onlyLatin)
        #expect(code == .modelNotInstalled)
        #expect(message == "nemotron-asr-3.5-multilingual is not installed.")
        #expect(hint == "Send model: nemotron-asr-3.5-latin, or install it.")

        // The other direction: an explicit Latin request with only
        // multilingual installed.
        let onlyMultilingual = TranscribeEndpoint.APIInputError.nemotronModelNotInstalled(
            latin,
            isInstalled: { $0 == multilingual }
        )
        #expect(onlyMultilingual.message == "nemotron-asr-3.5-latin is not installed.")
        #expect(onlyMultilingual.hint == "Send model: nemotron-asr-3.5-multilingual, or install it.")

        // Nothing installed: still names the model, suggests no variant, and
        // never suggests the model that was just found missing.
        let none = TranscribeEndpoint.APIInputError.nemotronModelNotInstalled(multilingual, isInstalled: { _ in false })
        #expect(none.code == .modelNotInstalled)
        #expect(none.message == "nemotron-asr-3.5-multilingual is not installed.")
        #expect(none.hint == "Install it from the app's Library, or pick a different engine/model.")
        let allInstalled = TranscribeEndpoint.APIInputError.nemotronModelNotInstalled(multilingual, isInstalled: { _ in true })
        #expect(allInstalled.hint == "Send model: nemotron-asr-3.5-latin, or install it.")
    }

    /// The real router with a real `NemotronProvider`: `engine: nemotron` and
    /// no `model` still tries the multilingual default (routing unchanged), and
    /// its absence now comes back as MODEL_NOT_INSTALLED naming that model.
    /// Runs only where the multilingual model is NOT installed — the router
    /// reads the real disk, and where it is installed the request would load
    /// it instead of failing.
    @Test(.enabled(if: !NemotronModelManager.isVariantInstalled(NemotronModelManager.Constants.multilingualModelId)))
    func nemotronWithNoModelNamesTheMissingDefaultThroughTheRouter() async throws {
        let whisperModels = WhisperModelManager()
        let whisper = LibWhisperProvider(modelManager: whisperModels)
        let nemotron = NemotronProvider()
        let router = TranscriptionProviderRouter()
        router.setProviders(
            localProvider: whisper,
            parakeetProvider: nil,
            parakeetModelManager: nil,
            speechAnalyzerProvider: nil,
            qwen3AsrProvider: nil,
            qwen3AsrModelManager: nil,
            nemotronProvider: nemotron,
            nemotronModelManager: nil
        )

        for engine in ["nemotron", "nemotron-asr", " Nemotron "] {
            let resolved = await Self.wireFailure(router: router, engine: engine, model: nil)
            let failure = try #require(
                resolved,
                "engine '\(engine)' with no model resolved, but the default is not installed"
            )
            #expect(failure.code == .modelNotInstalled)
            #expect(failure.message == "nemotron-asr-3.5-multilingual is not installed.")
            let latinInstalled = NemotronModelManager.isVariantInstalled(NemotronModelManager.Constants.latinModelId)
            #expect(
                failure.hint == (latinInstalled
                    ? "Send model: nemotron-asr-3.5-latin, or install it."
                    : "Install it from the app's Library, or pick a different engine/model.")
            )
        }
        withExtendedLifetime(whisper) {}
    }

    /// Only the router's Local API rewrite changed: a bare
    /// `.modelNotDownloaded` from any other caller still maps to the shared
    /// row, unchanged.
    @Test func aBareModelNotDownloadedStillMapsToTheSharedRow() {
        let (code, message, hint) = LocalAPIResponder.mapTranscriptionError(TranscriptionError.modelNotDownloaded)
        #expect(code == .modelNotInstalled)
        #expect(message == "Required model is not installed.")
        #expect(hint == "Install the model from the app's Library, or pick a different engine/model.")
    }
}
