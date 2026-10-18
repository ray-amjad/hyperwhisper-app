//
//  LocalAPIRequestMistakeErrorTests.swift
//  hyperwhisperTests
//
//  Issue #1919: three Local API request mistakes answered ENGINE_UNAVAILABLE
//  ("… is unavailable", hint "Try again later, or pick a different engine."),
//  so a client that branches on `code` retried a request that fails the same
//  way every time. Each now answers a code from the closed enum that says what
//  to fix:
//
//  1. A deleted or unknown `mode_id` on `/transcribe` and `/post-process`:
//     MODE_NOT_FOUND "No mode with id '<id>'", the bytes `GET /modes/<id>`
//     already sends.
//  2. `engine: parakeet` / `nemotron` with an unknown `model`: MODEL_NOT_FOUND.
//  3. `mode_id` + `engine: nemotron` with the default Nemotron model not
//     installed: the same NAMED MODEL_NOT_INSTALLED the engine-only form gives
//     since #1493, not the bare "Required model is not installed.".
//
//  The seams are the production functions the endpoints call, rendered through
//  `LocalAPIResponder.mapTranscriptionError`, which turns a throw into the
//  `(code, message, hint)` the envelope carries. `TranscribeEndpoint.handle`
//  needs a live `TranscriptionPipeline`, which no test in this target stands
//  up, so its `resolve` wiring is pinned by reading the source back.
//

import CoreData
import Foundation
import Testing
@testable import HyperWhisper

@MainActor
struct LocalAPIRequestMistakeErrorTests {

    private static let transcribeEndpointPath =
        "app/macos/hyperwhisper/Managers/LocalAPI/Endpoints/TranscribeEndpoint.swift"
    private static let postProcessEndpointPath =
        "app/macos/hyperwhisper/Managers/LocalAPI/Endpoints/PostProcessEndpoint.swift"

    private static func wire(_ error: Error) -> (code: LocalAPIErrorCode, message: String, hint: String?) {
        let (code, message, hint) = LocalAPIResponder.mapTranscriptionError(error)
        return (code, message, hint)
    }

    // MARK: - Row 1: a deleted or unknown mode_id

    /// A Mode that existed and was deleted, and an id that never existed, both
    /// answer MODE_NOT_FOUND with `GET /modes/<id>`'s message and no hint.
    /// The real store is not touched: the lookup runs against an in-memory one.
    @Test func aDeletedModeIdIsModeNotFound() throws {
        let persistence = PersistenceController(inMemory: true)
        let context = persistence.container.viewContext
        let mode = Mode(context: context)
        let modeUUID = UUID()
        mode.id = modeUUID
        mode.name = "Deleted by #1919"
        mode.sortOrder = 0
        mode.createdDate = Date()
        mode.modifiedDate = Date()
        persistence.save()
        try #require(!context.hasChanges, "the fixture save failed")
        let modeId = modeUUID.uuidString

        // Before the delete the lookup returns the saved Mode.
        let found = try TranscribeEndpoint.storedMode(forId: modeId, fetch: persistence.fetchMode(withId:))
        #expect(found.id == modeUUID)

        context.delete(mode)
        persistence.save()
        try #require(!context.hasChanges, "the delete did not commit")

        for id in [modeId, "does-not-exist", "00000000-0000-0000-0000-000000000000"] {
            var thrown: Error?
            do {
                _ = try TranscribeEndpoint.storedMode(forId: id, fetch: persistence.fetchMode(withId:))
            } catch {
                thrown = error
            }
            let error = try #require(thrown, "mode_id '\(id)' resolved to a Mode")
            let failure = Self.wire(error)
            #expect(failure.code == .modeNotFound, "mode_id '\(id)' answered \(failure.code)")
            #expect(failure.message == "No mode with id '\(id)'")
            #expect(failure.hint == nil)
        }
    }

    /// `/post-process` through its real `buildWorkingMode`, which reads the
    /// shared store: an id no Mode has answers MODE_NOT_FOUND, with or without
    /// overrides, instead of ENGINE_UNAVAILABLE "mode is unavailable".
    @Test func postProcessWithAnUnknownModeIdIsModeNotFound() throws {
        let unknownIds = ["does-not-exist", UUID().uuidString]
        for id in unknownIds {
            for preset in [nil, "email"] as [String?] {
                let request = PostProcessRequest(
                    text: "hello",
                    mode_id: id,
                    preset: preset,
                    prompt: nil,
                    provider: nil,
                    model: nil
                )
                var thrown: Error?
                do {
                    let working = try PostProcessEndpoint.buildWorkingMode(req: request)
                    working.transient?.end()
                } catch {
                    thrown = error
                }
                let error = try #require(thrown, "mode_id '\(id)' built a working Mode")
                let failure = Self.wire(error)
                #expect(failure.code == .modeNotFound, "mode_id '\(id)' preset \(preset ?? "nil") answered \(failure.code)")
                #expect(failure.message == "No mode with id '\(id)'")
                #expect(failure.hint == nil)
            }
        }
    }

    /// `/transcribe`'s `resolve` looks the Mode up through `storedMode`, and
    /// neither endpoint still throws the old `providerNotAvailable(provider:
    /// "mode", …)`, which mapped to ENGINE_UNAVAILABLE.
    @Test func bothEndpointsLookTheModeUpThroughStoredMode() throws {
        let resolveBody = try ProductionSource.slice(
            of: Self.transcribeEndpointPath,
            from: "private static func resolve(",
            to: "static func storedMode("
        )
        #expect(resolveBody.contains("try storedMode(forId: modeId"))
        #expect(!resolveBody.contains("provider: \"mode\""))

        let postProcess = try ProductionSource.code(of: Self.postProcessEndpointPath)
        #expect(postProcess.contains("TranscribeEndpoint.storedMode("))
        #expect(!postProcess.contains("provider: \"mode\""))
    }

    // MARK: - Row 2: an unknown Parakeet or Nemotron model

    /// The engine-only form through the real router: an unknown model is
    /// MODEL_NOT_FOUND with the router's old reason as the message and a hint
    /// naming the ids that work. Never ENGINE_UNAVAILABLE or "Try again later".
    @Test func anUnknownParakeetOrNemotronModelIsModelNotFound() async throws {
        let router = TranscriptionProviderRouter()
        let cases: [(engines: [String], label: String, hint: String)] = [
            (
                ["parakeet", " Parakeet "],
                "Parakeet",
                "Send model: \(ParakeetModelManager.Constants.v3ModelId) or \(ParakeetModelManager.Constants.v2ModelId)."
            ),
            (
                ["nemotron", "nemotron-asr", " Nemotron "],
                "Nemotron",
                "Send model: \(NemotronModelManager.Constants.multilingualModelId) or \(NemotronModelManager.Constants.latinModelId)."
            ),
        ]
        for entry in cases {
            for engine in entry.engines {
                for model in ["nope", "base"] {
                    var thrown: Error?
                    do {
                        _ = try await router.resolveProvider(engine: engine, model: model, language: nil)
                    } catch {
                        thrown = error
                    }
                    let error = try #require(thrown, "engine '\(engine)' model '\(model)' resolved")
                    let failure = Self.wire(error)
                    #expect(failure.code == .modelNotFound, "engine '\(engine)' model '\(model)' answered \(failure.code)")
                    #expect(failure.message == "Unknown \(entry.label) model '\(model)'")
                    #expect(failure.hint == entry.hint)
                    #expect(failure.hint?.contains("Try again later") != true)
                }
            }
        }
    }

    /// The mixed form's Parakeet validator throws the identical value, so one
    /// mistake has one answer on both forms.
    @Test func bothFormsRefuseAnUnknownParakeetModelWithTheSameBytes() async throws {
        let router = TranscriptionProviderRouter()
        var engineOnlyError: Error?
        do {
            _ = try await router.resolveProvider(engine: "parakeet", model: "nope", language: nil)
        } catch {
            engineOnlyError = error
        }
        var mixedError: Error?
        do {
            try TranscribeEndpoint.validateMixedPathEngineModel(engine: "parakeet", model: "nope")
        } catch {
            mixedError = error
        }
        let engineOnlyThrown = try #require(engineOnlyError)
        let mixedThrown = try #require(mixedError)
        let engineOnly = Self.wire(engineOnlyThrown)
        let mixed = Self.wire(mixedThrown)
        #expect(mixed.code == .modelNotFound)
        #expect(mixed.code == engineOnly.code)
        #expect(mixed.message == engineOnly.message)
        #expect(mixed.hint == engineOnly.hint)
    }

    // MARK: - Row 3: mode_id + engine nemotron, default not installed

    /// The mixed form's rewrite, with the disk stubbed: a `.modelNotDownloaded`
    /// for `engine: nemotron` becomes the named error, byte for byte what the
    /// engine-only form sends for the same variant.
    @Test func mixedFormNemotronNotInstalledNamesTheModel() {
        let multilingual = NemotronModelManager.Constants.multilingualModelId
        let latin = NemotronModelManager.Constants.latinModelId

        for engine in ["nemotron", " Nemotron ", "nemotron-asr"] {
            let rewritten = TranscribeEndpoint.mixedPathSelectionError(
                TranscriptionError.modelNotDownloaded,
                engine: engine,
                modeModel: multilingual,
                isInstalled: { $0 == latin }
            )
            let failure = Self.wire(rewritten)
            #expect(failure.code == .modelNotInstalled)
            #expect(failure.message == "nemotron-asr-3.5-multilingual is not installed.")
            #expect(failure.hint == "Send model: nemotron-asr-3.5-latin, or install it.")

            let engineOnly = Self.wire(
                TranscribeEndpoint.APIInputError.nemotronModelNotInstalled(multilingual, isInstalled: { $0 == latin })
            )
            #expect(failure.code == engineOnly.code)
            #expect(failure.message == engineOnly.message)
            #expect(failure.hint == engineOnly.hint)
        }

        // A saved Latin variant kept by the mixed form is the one named.
        let latinMissing = Self.wire(TranscribeEndpoint.mixedPathSelectionError(
            TranscriptionError.modelNotDownloaded,
            engine: "nemotron",
            modeModel: latin,
            isInstalled: { _ in false }
        ))
        #expect(latinMissing.code == .modelNotInstalled)
        #expect(latinMissing.message == "nemotron-asr-3.5-latin is not installed.")
        #expect(latinMissing.hint == "Install it from the app's Library, or pick a different engine/model.")
    }

    /// Only Nemotron's not-installed error is rewritten. Another engine's
    /// `.modelNotDownloaded`, and any other error, pass through unchanged.
    @Test func mixedFormRewritesNothingElse() {
        let parakeet = Self.wire(TranscribeEndpoint.mixedPathSelectionError(
            TranscriptionError.modelNotDownloaded,
            engine: "parakeet",
            modeModel: ParakeetModelManager.Constants.v3ModelId,
            isInstalled: { _ in false }
        ))
        #expect(parakeet.code == .modelNotInstalled)
        #expect(parakeet.message == "Required model is not installed.")

        let noEngine = Self.wire(TranscribeEndpoint.mixedPathSelectionError(
            TranscriptionError.modelNotDownloaded,
            engine: nil,
            modeModel: NemotronModelManager.Constants.multilingualModelId,
            isInstalled: { _ in false }
        ))
        #expect(noEngine.message == "Required model is not installed.")

        let other = TranscribeEndpoint.mixedPathSelectionError(
            TranscriptionError.invalidRequest,
            engine: "nemotron",
            modeModel: NemotronModelManager.Constants.multilingualModelId,
            isInstalled: { _ in false }
        )
        #expect(!(other is TranscribeEndpoint.APIInputError))
    }

    /// `resolve`'s mixed form sends a failed `selectProvider` through the
    /// rewrite, reading the transient Mode's model before ending it.
    @Test func resolveSendsTheMixedFormThroughTheRewrite() throws {
        let resolveBody = try ProductionSource.slice(
            of: Self.transcribeEndpointPath,
            from: "private static func resolve(",
            to: "static func storedMode("
        )
        let mixedCatch = try ProductionSource.slice(
            of: Self.transcribeEndpointPath,
            from: "selection = try await router.selectProvider(for: transient.mode",
            to: "return ProviderResolution("
        )
        #expect(resolveBody.contains(mixedCatch))
        let readModel = try #require(mixedCatch.range(of: "let triedModel = transient.mode.model"))
        let end = try #require(mixedCatch.range(of: "transient.end()"))
        #expect(readModel.lowerBound < end.lowerBound, "the model must be read before the transient Mode ends")
        #expect(mixedCatch.contains("throw mixedPathSelectionError("))
        #expect(mixedCatch.contains("modeModel: triedModel"))
    }

    /// The real router end to end, the way `resolve`'s mixed form runs it: a
    /// saved Mode plus `engine: nemotron` and no `model` builds a transient
    /// Mode on the multilingual default, `selectProvider` fails because it is
    /// not installed, and the rewrite names it. Runs only where the
    /// multilingual model is NOT installed — the router reads the real disk.
    @Test(.enabled(if: !NemotronModelManager.isVariantInstalled(NemotronModelManager.Constants.multilingualModelId)))
    func mixedFormNemotronWithNoModelNamesTheMissingDefaultThroughTheRouter() async throws {
        let persistence = PersistenceController(inMemory: true)
        let context = persistence.container.viewContext
        let saved = Mode(context: context)
        saved.id = UUID()
        saved.name = "Saved for #1919"
        saved.model = "base"
        saved.sortOrder = 0
        saved.createdDate = Date()
        saved.modifiedDate = Date()
        persistence.save()
        try #require(!context.hasChanges, "the fixture save failed")

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

        let transient = TranscribeEndpoint.makeTransientMode(
            baseline: saved,
            engine: "nemotron",
            model: nil,
            language: nil,
            parent: context
        )
        defer { transient.end() }
        let triedModel = transient.mode.model
        #expect(triedModel == NemotronModelManager.Constants.multilingualModelId)

        var thrown: Error?
        do {
            _ = try await router.selectProvider(for: transient.mode, vocabulary: [], stageWhisperModel: false)
        } catch {
            thrown = error
        }
        let error = try #require(thrown, "the multilingual default resolved, but it is not installed")
        let failure = Self.wire(TranscribeEndpoint.mixedPathSelectionError(
            error,
            engine: "nemotron",
            modeModel: triedModel,
            isInstalled: NemotronModelManager.isVariantInstalled
        ))
        #expect(failure.code == .modelNotInstalled)
        #expect(failure.message == "nemotron-asr-3.5-multilingual is not installed.")
        withExtendedLifetime(whisper) {}
    }
}
