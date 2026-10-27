//
//  TranscribeEndpoint.swift
//  hyperwhisper
//
//  Implements `POST /transcribe`. Accepts either `mode_id` (resolve a saved
//  Mode) or `engine`+`model`+`language` (Mode-less direct invocation).
//
//  `/post-process` is the formatting endpoint, so this route never runs the AI
//  rewrite. The DETERMINISTIC text passes are a different thing: they are the
//  user's own settings and contain no LLM, so the `text` field carries the
//  transcript after them — the user's vocabulary replacements, dictated
//  "new line" / "new paragraph" break commands, and filler-word removal when
//  that setting is on. Windows made the same split for issues #495 / #498; this
//  head returned the engine's raw string with none of the three applied, so a
//  user with the rule `eta` → `estimated time of arrival` got `eta` back from
//  the API and the expansion from dictation of the same audio (issue #530).
//

import Foundation
import CoreData
import FlyingFox
import Darwin

enum TranscribeEndpoint {

    /// Takes `body`, not an `HTTPRequest` (issue #375). The bytes have already
    /// been read and bounded at the shared cap by `LocalAPIServer.bodied`, the
    /// router-level wrapper that sits beside the origin and bearer guards. This
    /// endpoint is handed no request, so it has no unbounded body to read.
    @MainActor
    static func handle(body: Data, transcriptionPipeline: TranscriptionPipeline?) async -> HTTPResponse {
        let req: TranscribeRequest
        do { req = try LocalAPIResponder.decoder.decode(TranscribeRequest.self, from: body) } catch {
            return LocalAPIResponder.badRequest(
                message: "Invalid JSON body",
                hint: "Required: file (absolute path) plus either mode_id, or engine + model."
            )
        }

        // Resolve audio source: file path OR base64 (mutually exclusive).
        let audioResolution: AudioResolution
        do {
            audioResolution = try Self.resolveAudioSource(req: req)
        } catch let err as APIInputError {
            return LocalAPIResponder.failure(code: err.code, message: err.message, hint: err.hint)
        } catch {
            return LocalAPIResponder.failure(code: .invalidRequest, message: error.localizedDescription)
        }
        let fileURL = audioResolution.url
        // Per-request transient file gets cleaned up via `defer` below.
        defer { audioResolution.cleanup() }

        guard let pipeline = transcriptionPipeline else {
            return LocalAPIResponder.failure(code: .engineUnavailable, message: "Transcription pipeline not initialized")
        }

        // Capture before resolution reads a credential. A later key edit makes
        // this request's outcome stale and unable to overwrite the new verdict.
        let healthManager = pipeline.providerCoordinator.providerHealthManager
        let credentialGeneration = healthManager?.captureTranscriptionCredentialGeneration()

        // Determine the Mode (saved) or build a transient one from engine/model/language.
        let resolution: ProviderResolution
        do {
            resolution = try await resolve(req: req, pipeline: pipeline)
        } catch {
            let (code, message, hint) = LocalAPIResponder.mapTranscriptionError(error)
            return LocalAPIResponder.failure(code: code, message: message, hint: hint)
        }
        // Every return below ends the per-request Mode, success or failure
        // (issue #1509). It never touched the store; this only drops it from
        // the in-flight registry the Model Library delete check reads.
        defer { resolution.transientMode?.end() }

        let language = effectiveLanguage(for: resolution, request: req)

        // Opt-in timestamps: parse granularities and arm the provider. Providers
        // that can't produce timestamps ignore this (default no-op). Local
        // Whisper is armed inside its queued pass instead, so a request waiting
        // in the queue cannot arm the pass ahead of it (issue #1465).
        let granularities = TimestampGranularities(wire: req.timestamp_granularities)
        let whisper = resolution.provider as? LibWhisperProvider
        if !granularities.isEmpty, whisper == nil {
            resolution.provider.setTimestampGranularities(granularities)
        }

        // Health feedback (issue #379): the health manager owns the `/health`
        // verdict for this provider, and only the real transcribe call below can
        // tell it whether the provider is actually up. Resolved once here, on the
        // main actor, so both the success and failure arms report synchronously —
        // no `Task { }` hop that could land after the next probe.
        let cloudProviderType = resolution.cloudProviderType

        let started = Date()
        let text: String
        // The language and timestamps the engine reported for THIS run. Both
        // come back in the call's own `TranscriptionOutcome` (#754), not off
        // the provider instance, which the GUI pipeline and overlapping Local
        // API requests share.
        //
        // Local Whisper also queues (issue #1465). Its passes supersede each
        // other, so overlapping requests cancelled one another; they now queue
        // on the one shared provider, and the queued pass returns its own
        // outcome.
        let detectedLanguage: String?
        var whisperTimestamps: TranscriptionTimestamps?
        var providerTimestamps: TranscriptionTimestamps?
        do {
            if let whisper {
                let pass = try await whisper.transcribeQueuedForLocalAPI(
                    model: resolution.whisperModel,
                    audioURL: fileURL,
                    language: language,
                    vocabulary: resolution.vocabulary,
                    granularities: granularities
                )
                text = pass.text
                detectedLanguage = pass.detectedLanguage
                whisperTimestamps = pass.timestamps
            } else {
                let outcome: TranscriptionOutcome
                do {
                    outcome = try await resolution.provider.transcribeDetailed(
                        audioURL: fileURL,
                        language: language,
                        mode: resolution.mode,
                        vocabulary: resolution.vocabulary
                    )
                } catch let failure as TranscriptionAttemptFailure {
                    // The Local API reports no attempt diagnostics. Unwrap here
                    // so the catch below (health feedback, error mapping) sees
                    // the provider's ORIGINAL error, exactly as before.
                    throw failure.underlying
                }
                text = outcome.text
                detectedLanguage = outcome.detectedLanguage
                providerTimestamps = outcome.timestamps
            }
            if let cloudProviderType {
                if let credentialGeneration {
                    healthManager?.recordTranscriptionOutcome(
                        for: cloudProviderType,
                        credentialGeneration: credentialGeneration,
                        error: nil
                    )
                }
            }
        } catch {
            if let cloudProviderType {
                if let credentialGeneration {
                    healthManager?.recordTranscriptionOutcome(
                        for: cloudProviderType,
                        credentialGeneration: credentialGeneration,
                        error: error
                    )
                }
            }
            if whisper != nil, let te = error as? TranscriptionError, case .streamingInterrupted = te {
                let cancelled = Self.whisperPassCancelled
                return LocalAPIResponder.failure(code: cancelled.code, message: cancelled.message, hint: cancelled.hint)
            }
            let (code, message, hint) = LocalAPIResponder.mapTranscriptionError(error)
            return LocalAPIResponder.failure(code: code, message: message, hint: hint)
        }
        let latencyMs = Int(Date().timeIntervalSince(started) * 1000)

        // The local engine's own vocabulary correction (the Parakeet family's
        // phonetic pass), once, exactly where `TranscriptionPipeline` runs it:
        // after the engine's text and before the `\b` replacement pass below.
        // It used to run inside the engine with an unanchored substring pass,
        // so every replacement row was applied twice (issue #1622).
        let correctedText = VocabularyProcessor.applyLocalEngineVocabularyCorrection(
            to: text,
            provider: resolution.provider,
            vocabulary: resolution.vocabulary
        )

        // The deterministic passes, in the order and on the terms
        // `TranscriptionPipeline`'s no-post-processing branch applies them.
        // Filler removal is gated on the language that actually came back
        // rather than on a requested "auto" — "er" and "um" are real words in
        // other languages (issue #278).
        let finalText = Self.applyDeterministicTextPasses(
            to: correctedText,
            language: detectedLanguage ?? language,
            mode: resolution.mode,
            pipeline: pipeline
        )

        // Read timestamps produced by the run (nil unless requested AND the
        // engine could produce them → graceful omission for cloud/other engines).
        let timestamps = granularities.isEmpty
            ? nil
            : (whisper != nil ? whisperTimestamps : providerTimestamps)
        let segments = timestamps.map { ts in
            ts.segments.map { TranscribeSegment(id: $0.id, start: $0.start, end: $0.end, text: $0.text) }
        }
        let words = timestamps?.words.map { ws in
            ws.map { TranscribeWord(word: $0.word, start: $0.start, end: $0.end) }
        }

        let response = TranscribeResponse(
            ok: true,
            text: finalText,
            engine: resolution.engineLabel,
            model: resolution.modelLabel,
            language: language,
            timings: TranscribeTimings(load_ms: 0, decode_ms: latencyMs),
            latency_ms: latencyMs,
            raw_text: timestamps?.rawText,
            segments: segments,
            words: words
        )
        return LocalAPIResponder.ok(response)
    }

    /// The answer for a local Whisper pass that something newer cancelled —
    /// after the queue, that is an in-app dictation (#1425: it wins) or a
    /// cancel of the app's own transcription.
    ///
    /// The provider throws `streamingInterrupted` for it, whose text ("Streaming
    /// interrupted; partial text shown") describes dictation, not an API call:
    /// there is no stream and no partial text. Same closed-enum code the
    /// generic row gave it, honest message (Ray, 2026-10-08, issue #1465).
    static let whisperPassCancelled = APIInputError(
        code: .transcriptionFailed,
        message: "Cancelled by a newer transcription request.",
        hint: "An in-app dictation on Local Whisper takes priority over Local API calls. Send the request again."
    )

    // MARK: - Deterministic text passes

    /// The three passes `/transcribe`'s `text` carries, read off the running
    /// pipeline's settings.
    ///
    /// A thin adapter over the pure form below so `handle` stays one call and
    /// the decision itself is testable without a `TranscriptionPipeline` (which
    /// needs a provider router, a model manager and a settings manager).
    @MainActor
    static func applyDeterministicTextPasses(
        to text: String,
        language: String?,
        mode: Mode?,
        pipeline: TranscriptionPipeline
    ) -> String {
        applyDeterministicTextPasses(
            to: text,
            language: language,
            mode: mode,
            // `== false`, not `?? true` inverted: a pipeline with no settings
            // manager keeps the app's default, exactly as
            // `TranscriptionPipeline+Transcription` reads it.
            removeFillerWords: pipeline.settingsManager?.removeFillerWords != false,
            vocabularyProcessor: pipeline.vocabularyProcessor
        )
    }

    /// Filler-word removal, then dictated break commands, then the user's
    /// vocabulary replacements — the order and the gating of
    /// `TranscriptionPipeline`'s no-post-processing branch, which is the branch
    /// this route is always on because it declines the AI rewrite.
    ///
    /// Vocabulary replacements are NOT gated on the filler-word setting: they
    /// are a separate switch, and Windows' orchestrator runs them
    /// unconditionally in the same arm. Only the first pass reads
    /// `removeFillerWords`.
    ///
    /// The insert-time steps stay out on purpose. Autocapitalize Insert and the
    /// trailing space depend on where the text is being typed, and no API
    /// response has a cursor — the portable head omits them here for the same
    /// reason (it projects `SpeechOutputProcessingResult.transcriptText`, which
    /// is taken before those steps run).
    @MainActor
    static func applyDeterministicTextPasses(
        to text: String,
        language: String?,
        mode: Mode?,
        removeFillerWords: Bool,
        vocabularyProcessor: VocabularyProcessor
    ) -> String {
        let withoutFillers = removeFillerWords
            ? TranscriptionTextProcessing.removeFillerWords(text, language: language)
            : text
        let withCommands = TranscriptionTextProcessing.processVoiceCommands(withoutFillers)
        return vocabularyProcessor.applyVocabularyReplacements(withCommands, mode: mode)
    }

    // MARK: - Audio source (TranscribeAudioSource.swift)

    typealias FileIdentity = TranscribeAudioSource.FileIdentity
    typealias FileStatus = TranscribeAudioSource.FileStatus
    private typealias AudioResolution = TranscribeAudioSource.AudioResolution
    private static func resolveAudioSource(req: TranscribeRequest) throws -> AudioResolution { try TranscribeAudioSource.resolveAudioSource(req: req) }
    static func uploadCapRefusal(forAudioBytes count: UInt64) -> APIInputError? { TranscribeAudioSource.uploadCapRefusal(forAudioBytes: count) }
    static func fileInputRefusal(for access: SourceFileAccess, displayPath: String) -> APIInputError? { TranscribeAudioSource.fileInputRefusal(for: access, displayPath: displayPath) }
    static func fileStatus(forFileDescriptor fd: CInt) throws -> FileStatus { try TranscribeAudioSource.fileStatus(forFileDescriptor: fd) }

    // MARK: - Local API input errors

    /// Not `private`, because `uploadCapRefusal(forAudioBytes:)` returns one and
    /// the tests read its `code`/`message`/`hint` back.
    struct APIInputError: Error {
        let code: LocalAPIErrorCode
        let message: String
        let hint: String?

        /// `engine: whisper` (any alias of `whisperLocal`) with no or a blank
        /// `model` (issue #1493, Ray's decision via inbox #295). A request
        /// mistake, so INVALID_REQUEST with a hint to send `model` — not the
        /// ENGINE_UNAVAILABLE "Try again later" it used to be, which a client
        /// that branches on `code` retries forever. No default model is picked.
        /// The message is the portable head's own wording
        /// (`ApplicationLocalApiBackend.Transcription.cs`), and Windows sends
        /// the same bytes (`TranscribeEndpoints.ApplyEngineModel`).
        ///
        /// Thrown by both `/transcribe` forms: the engine-only path
        /// (`TranscriptionProviderRouter.resolveProvider`) and the mixed
        /// mode_id + engine path (`validateMixedPathEngineModel`, #1466).
        /// Not routed through the shared failure table: its INVALID_REQUEST
        /// row renders "Invalid request: <detail>" and carries no hint.
        static let whisperModelRequired = APIInputError(
            code: .invalidRequest,
            message: "A Whisper model is required.",
            hint: "Send model, for example base.en."
        )

        /// MODEL_NOT_INSTALLED for a Nemotron variant that is not on disk,
        /// naming the model that was tried and, when the other variant IS
        /// installed, suggesting it (issue #1493). The routing is unchanged: an
        /// omitted `model` still tries `nemotron-asr-3.5-multilingual`
        /// (`NemotronModelManager.Constants.modelIdForSelection`); only the
        /// error says which model that was. The shared row's message ignores
        /// the `model` slot, so the head renders it here.
        ///
        /// `isInstalled` is `NemotronModelManager.isVariantInstalled` in the
        /// app and a stub in the tests, which cannot control the disk.
        static func nemotronModelNotInstalled(
            _ triedModelId: String,
            isInstalled: (String) -> Bool
        ) -> APIInputError {
            let installedAlternative = [
                NemotronModelManager.Constants.multilingualModelId,
                NemotronModelManager.Constants.latinModelId,
            ].first { $0 != triedModelId && isInstalled($0) }
            let hint: String
            if let installedAlternative {
                hint = "Send model: \(installedAlternative), or install it."
            } else {
                hint = "Install it from the app's Library, or pick a different engine/model."
            }
            return APIInputError(
                code: .modelNotInstalled,
                message: "\(triedModelId) is not installed.",
                hint: hint
            )
        }

        /// A `mode_id` that names no saved Mode, on `/transcribe` and
        /// `/post-process` (issue #1919). MODE_NOT_FOUND with the exact bytes
        /// `GET /modes/<id>` sends (`ModesEndpoint`), and what Windows sends for
        /// the same body. It used to be ENGINE_UNAVAILABLE "Try again later",
        /// which a client that branches on `code` retried forever.
        static func modeNotFound(_ modeId: String) -> APIInputError {
            APIInputError(code: .modeNotFound, message: "No mode with id '\(modeId)'", hint: nil)
        }

        /// `engine: parakeet` with a `model` that is not a Parakeet variant
        /// (issue #1919). MODEL_NOT_FOUND, not ENGINE_UNAVAILABLE: the request
        /// fails the same way every time. The message keeps the router's old
        /// reason; the hint names the ids that would work.
        static func unknownParakeetModel(_ modelId: String) -> APIInputError {
            APIInputError(
                code: .modelNotFound,
                message: "Unknown Parakeet model '\(modelId)'",
                hint: "Send model: \(ParakeetModelManager.Constants.v3ModelId) or \(ParakeetModelManager.Constants.v2ModelId)."
            )
        }

        /// `engine: nemotron` with a `model` that is not a Nemotron variant
        /// (issue #1919). Same shape as `unknownParakeetModel`.
        static func unknownNemotronModel(_ modelId: String) -> APIInputError {
            APIInputError(
                code: .modelNotFound,
                message: "Unknown Nemotron model '\(modelId)'",
                hint: "Send model: \(NemotronModelManager.Constants.multilingualModelId) or \(NemotronModelManager.Constants.latinModelId)."
            )
        }
    }

    // MARK: - Resolution

    /// Bundle of values produced by resolving the request inputs into a
    /// concrete provider + (optional) Mode that the provider can read.
    private struct ProviderResolution {
        let provider: TranscriptionProvider
        let mode: Mode?
        let vocabulary: [Vocabulary]
        let engineLabel: String
        let modelLabel: String
        /// Non-nil when we synthesized a Mode just for this request. Its
        /// `mode` is the `mode` above. The caller MUST `end()` it once the
        /// request finishes; `handle` does so in a `defer`.
        let transientMode: LocalAPITransientMode?
        /// The cloud provider this request resolved to, straight out of the
        /// router's own resolution (never re-derived here). nil for local
        /// engines. Used to report the transcription outcome back to
        /// `CloudProviderHealthManager` (issue #379).
        let cloudProviderType: CloudProvider?
        /// The local Whisper model this request asked for, nil for every other
        /// engine. Loaded by the queued pass itself; resolution never stages it
        /// on the shared provider (issue #1465).
        let whisperModel: WhisperModel?
    }

    @MainActor
    private static func resolve(
        req: TranscribeRequest,
        pipeline: TranscriptionPipeline
    ) async throws -> ProviderResolution {
        let router = pipeline.providerCoordinator

        // The SAME snapshot `TranscriptionPipeline` takes for a dictation run.
        // Every branch below used to pass `[]`, so a local engine skipped the
        // phonetic and substring matching it does for dictation, and a cloud
        // provider was sent no keyterm / initial_prompt biasing (issue #530).
        // Taken once, before any await, so all branches of one request see one
        // vocabulary.
        let vocabulary = PersistenceController.shared.fetchAllVocabularyItems()

        let trimmedEngine = req.engine?.trimmingCharacters(in: .whitespacesAndNewlines)
        let trimmedModel = req.model?.trimmingCharacters(in: .whitespacesAndNewlines)
        let trimmedLanguage = req.language?.trimmingCharacters(in: .whitespacesAndNewlines)
        let hasOverride = (trimmedEngine?.isEmpty == false)
            || (trimmedModel?.isEmpty == false)
            || (trimmedLanguage?.isEmpty == false)

        if let modeId = req.mode_id?.trimmingCharacters(in: .whitespacesAndNewlines), !modeId.isEmpty {
            let stored = try storedMode(forId: modeId, fetch: PersistenceController.shared.fetchMode(withId:))

            // Pure mode_id call → use saved Mode untouched.
            if !hasOverride {
                let selection = try await router.selectProvider(for: stored, vocabulary: vocabulary, stageWhisperModel: false)
                return ProviderResolution(
                    provider: selection.provider,
                    mode: stored,
                    vocabulary: vocabulary,
                    engineLabel: engineLabel(forMode: stored),
                    modelLabel: modelLabel(forMode: stored),
                    transientMode: nil,
                    cloudProviderType: selection.cloudProviderType,
                    whisperModel: selection.whisperModel
                )
            }

            // Mixed: saved mode supplies defaults, request overrides specific fields.
            //
            // Refuse a blank Whisper model HERE, before a transient Mode exists
            // (issue #1466; Ray's decision 2026-10-08: "be refused, like Windows
            // and Linux"). This path never calls `resolveProvider`, so without
            // this it had its own, softer answer for the same body.
            if let engine = trimmedEngine, !engine.isEmpty {
                try validateMixedPathEngineModel(engine: engine, model: trimmedModel)
            }
            let transient = makeTransientMode(baseline: stored, engine: trimmedEngine, model: trimmedModel, language: trimmedLanguage)
            let selection: TranscriptionProviderRouter.ProviderSelection
            do {
                selection = try await router.selectProvider(for: transient.mode, vocabulary: vocabulary, stageWhisperModel: false)
            } catch {
                // Read before `end()`: the Mode belongs to the transient.
                let triedModel = transient.mode.model
                // `handle` never gets a resolution to end, so end it here.
                transient.end()
                throw mixedPathSelectionError(
                    error,
                    engine: trimmedEngine,
                    modeModel: triedModel,
                    isInstalled: NemotronModelManager.isVariantInstalled
                )
            }
            return ProviderResolution(
                provider: selection.provider,
                mode: transient.mode,
                vocabulary: vocabulary,
                engineLabel: engineLabel(forMode: transient.mode),
                modelLabel: modelLabel(forMode: transient.mode),
                transientMode: transient,
                cloudProviderType: selection.cloudProviderType,
                whisperModel: selection.whisperModel
            )
        }

        // engine + model path: synthesize a transient Mode so cloud providers
        // can still read `cloudTranscriptionModel` etc. off it.
        guard let engine = trimmedEngine, !engine.isEmpty else {
            throw TranscriptionError.invalidRequest
        }

        let selection = try await router.resolveProvider(
            engine: engine,
            model: req.model,
            language: req.language
        )

        let transient = makeTransientMode(baseline: nil, engine: engine, model: req.model, language: req.language)

        // Derive the labels from the synthesized mode rather than echoing the
        // raw request: when the caller omits `model`, makeTransientMode applies
        // the provider's default (e.g. groq → whisper-large-v3-turbo), and the
        // response must report the model that actually ran, not "". This also
        // matches the normalized labels the mode_id paths above already return.
        return ProviderResolution(
            provider: selection.provider,
            mode: transient.mode,
            vocabulary: vocabulary,
            engineLabel: engineLabel(forMode: transient.mode),
            modelLabel: modelLabel(forMode: transient.mode),
            transientMode: transient,
            cloudProviderType: selection.cloudProviderType,
            whisperModel: selection.whisperModel
        )
    }

    /// The saved Mode `modeId` names, or MODE_NOT_FOUND "No mode with id
    /// '<id>'" (issue #1919) — the answer `GET /modes/<id>` gives. Used by
    /// `/transcribe` and `/post-process`, which used to throw
    /// `providerNotAvailable(provider: "mode", …)`, mapped to ENGINE_UNAVAILABLE
    /// "Try again later". `fetch` is `PersistenceController.shared
    /// .fetchMode(withId:)` in the app and an in-memory store in the tests.
    @MainActor
    static func storedMode(forId modeId: String, fetch: (String) -> Mode?) throws -> Mode {
        guard let stored = fetch(modeId) else {
            throw APIInputError.modeNotFound(modeId)
        }
        return stored
    }


    /// Build the per-request Mode. Seeded either from scratch
    /// (`baseline == nil`) or by copying every field off `baseline` so the
    /// request can override only the bits it cares about.
    ///
    /// The Mode lives in a never-saved scratch context, not the shared
    /// `viewContext` (issue #1509): another save of the `viewContext` while
    /// the request runs used to commit it as a real row. See
    /// `LocalAPITransientMode`. The caller MUST `end()` the result once the
    /// request finishes, success or failure.
    ///
    /// `internal` and `parent`-injectable so `LocalAPITransientModeTests`
    /// can build it against an in-memory store exactly as a request does.
    @MainActor
    static func makeTransientMode(
        baseline: Mode?,
        engine: String?,
        model: String?,
        language: String?,
        parent: NSManagedObjectContext = PersistenceController.shared.container.viewContext
    ) -> LocalAPITransientMode {
        let transient = LocalAPITransientMode(
            name: LocalAPITransientModeMarker.transcribeName,
            parent: parent
        )
        let mode = transient.mode

        if let baseline {
            mode.preset = baseline.preset ?? "hyper"
            mode.language = baseline.language ?? "auto"
            mode.model = baseline.model ?? "base"
            mode.punctuation = baseline.punctuation
            mode.capitalization = baseline.capitalization
            mode.profanityFilter = baseline.profanityFilter
            mode.customInstructions = baseline.customInstructions ?? ""
            mode.userSystemPrompt = baseline.userSystemPrompt
            mode.languageModel = baseline.languageModel
            mode.cloudProvider = baseline.cloudProvider
            mode.cloudTranscriptionModel = baseline.cloudTranscriptionModel
            mode.postProcessingMode = baseline.postProcessingMode
            mode.postProcessingProvider = baseline.postProcessingProvider
            mode.englishSpelling = baseline.englishSpelling
            mode.useStreamingTranscription = baseline.useStreamingTranscription
            mode.cloudAccuracyTier = baseline.cloudAccuracyTier
            mode.removeTrailingPeriod = baseline.removeTrailingPeriod
            mode.enableScreenOCR = baseline.enableScreenOCR
            mode.geminiCustomPrompt = baseline.geminiCustomPrompt
            mode.cloudPostProcessingModel = baseline.cloudPostProcessingModel
            mode.cloudTranscriptionDomain = baseline.cloudTranscriptionDomain
        } else {
            mode.preset = "hyper"
            mode.language = "auto"
            mode.punctuation = true
            mode.capitalization = true
            mode.profanityFilter = false
            mode.customInstructions = ""
            mode.postProcessingMode = 0
        }

        if let language, !language.isEmpty {
            mode.language = language
        }

        if let engine, !engine.isEmpty {
            applyEngineModel(to: mode, engine: engine, model: model)
        } else if let model, !model.isEmpty {
            // Engine implied by baseline; just patch the model field.
            let baseModel = (mode.model ?? "").lowercased()
            if baseModel == "cloud" {
                mode.cloudTranscriptionModel = model
            } else {
                mode.model = model
            }
        }
        return transient
    }

    @MainActor
    private static func effectiveLanguage(for resolution: ProviderResolution, request: TranscribeRequest) -> String? {
        let raw = (request.language ?? resolution.mode?.language)?.lowercased()
        guard let raw, raw != "auto", !raw.isEmpty else { return nil }
        return raw
    }

    // MARK: - Engine / model mapping (TranscribeEngineMapping.swift)

    @MainActor
    static func mixedPathSelectionError(_ error: Error, engine: String?, modeModel: String?, isInstalled: (String) -> Bool) -> Error { TranscribeEngineMapping.mixedPathSelectionError(error, engine: engine, modeModel: modeModel, isInstalled: isInstalled) }
    @MainActor
    static func applyEngineModel(to mode: Mode, engine: String, model: String?) { TranscribeEngineMapping.applyEngineModel(to: mode, engine: engine, model: model) }
    @MainActor
    static func validateMixedPathEngineModel(engine: String, model: String?) throws { try TranscribeEngineMapping.validateMixedPathEngineModel(engine: engine, model: model) }
    static func modelRoutesToCloud(_ model: String?) -> Bool { TranscribeEngineMapping.modelRoutesToCloud(model) }
    @MainActor
    static func engineLabel(forMode mode: Mode) -> String { TranscribeEngineMapping.engineLabel(forMode: mode) }
    @MainActor
    static func modelLabel(forMode mode: Mode) -> String { TranscribeEngineMapping.modelLabel(forMode: mode) }
}
