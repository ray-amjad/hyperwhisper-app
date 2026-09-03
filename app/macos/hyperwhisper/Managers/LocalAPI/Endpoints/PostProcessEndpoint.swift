//
//  PostProcessEndpoint.swift
//  hyperwhisper
//
//  Implements `POST /post-process`. Accepts a saved mode (for defaults) plus
//  optional overrides — `preset`/`prompt`/`provider`/`model` — and calls
//  `AIPostProcessor.performAIPostProcessingPreservingBreaks(text:mode:)`, the
//  same break-preserving call the in-app pipeline uses, so dictated paragraph
//  breaks ("new line" / "new paragraph") survive post-processing instead of
//  being silently merged. Streaming output is accumulated and returned as a
//  single response body — the endpoint contract is unchanged.
//

import Foundation
import CoreData
import FlyingFox

enum PostProcessEndpoint {

    /// Takes `body`, not an `HTTPRequest` (issue #375) — already read and
    /// bounded at the shared cap by `LocalAPIServer.bodied`.
    @MainActor
    static func handle(body: Data, transcriptionPipeline: TranscriptionPipeline?) async -> HTTPResponse {
        let req: PostProcessRequest
        do { req = try LocalAPIResponder.decoder.decode(PostProcessRequest.self, from: body) } catch {
            return LocalAPIResponder.badRequest(
                message: "Invalid JSON body",
                hint: "Required: text, plus one of mode_id / preset / prompt. See /modes GET for the field shape."
            )
        }

        let text = req.text.trimmingCharacters(in: .whitespacesAndNewlines)
        if text.isEmpty {
            return LocalAPIResponder.failure(code: .invalidRequest, message: "'text' is required")
        }
        if req.preset != nil && req.prompt != nil {
            return LocalAPIResponder.failure(
                code: .invalidRequest,
                message: "'preset' and 'prompt' are mutually exclusive",
                hint: "Pass one or the other — not both."
            )
        }
        if req.mode_id == nil && req.preset == nil && req.prompt == nil {
            return LocalAPIResponder.failure(
                code: .invalidRequest,
                message: "Provide at least one of mode_id, preset, or prompt"
            )
        }

        guard let pipeline = transcriptionPipeline, let processor = pipeline.aiPostProcessor else {
            return LocalAPIResponder.failure(code: .engineUnavailable, message: "Post-processor not initialized")
        }

        // Build the working Mode: stored mode (if any) provides defaults, then
        // we overlay the per-request overrides onto either the saved mode (if
        // it's safe to mutate) or a transient mode based on it.
        let working: (mode: Mode, isTransient: Bool)
        do { working = try buildWorkingMode(req: req) } catch let inputError as PostProcessInputError {
            return LocalAPIResponder.failure(code: inputError.code, message: inputError.message, hint: inputError.hint)
        } catch {
            let (code, message, hint) = LocalAPIResponder.mapTranscriptionError(error)
            return LocalAPIResponder.failure(code: code, message: message, hint: hint)
        }

        let started = Date()
        // Request-scoped mutation signal: the shared `processor.didMutateLastRun`
        // is unreliable here because concurrent /post-process calls interleave at
        // the awaits inside the processor (it's @MainActor, not serialized). Pass
        // our own signal so the "did an LLM actually run?" answer is tied to THIS
        // request and can't be clobbered by an overlapping one. See MutationSignal.
        let mutationSignal = MutationSignal()
        let result: String
        do {
            // Pin app context to `.none` so the system prompt is byte-identical
            // across consecutive requests — otherwise the contextual-formatting
            // block in the SYSTEM message changes whenever the user's frontmost
            // app changes, busting llama-server's KV prompt cache for the
            // ~2,500-token static prefix. API callers have no meaningful
            // "frontmost app" anyway. The user message still contains the
            // dynamic systemInfo (TIME, vocab) — iter 11 tested omitting that
            // too but broke the 12B reliability gate, see
            // tuning-notes/13-iter11-omit-systeminfo.md.
            result = try await processor.performAIPostProcessingPreservingBreaks(
                text: text,
                mode: working.mode,
                applicationContext: ApplicationContext.none,
                mutationSignal: mutationSignal,
                // The endpoint returns the final accumulated `result` as a single
                // HTTP response body — it never consumes intermediate streaming
                // text. Pass a no-op sink so the per-segment loop never touches
                // the shared `onStreamingTextUpdate` instance property (which is
                // also the in-app live-recording preview callback); see
                // `AIPostProcessor.performAIPostProcessingPreservingBreaks`.
                onSegmentTextUpdate: { _ in }
            )
        } catch {
            if working.isTransient { cleanupTransientMode(working.mode) }
            let (code, message, hint) = LocalAPIResponder.mapTranscriptionError(error)
            return LocalAPIResponder.failure(code: code, message: message, hint: hint)
        }
        let latencyMs = Int(Date().timeIntervalSince(started) * 1000)
        // Did an LLM actually run, or did a failure path return the raw transcript
        // unchanged? AIPostProcessor swallows provider errors (graceful degradation)
        // so `result` alone can't tell us — the request-scoped `mutationSignal` is
        // the honest, concurrency-safe answer.
        let didPostProcess = mutationSignal.didMutate

        // `mutationSignal.anyPartialFailure` covers the multi-segment case where
        // some segments post-processed and at least one fell back to raw text —
        // `didMutate` alone is OR-aggregated across segments and would otherwise
        // report `ok: true, post_processed: true` for a response that's silently
        // a mix of processed and raw/unprocessed segment text.
        if mutationSignal.anyPartialFailure {
            if working.isTransient { cleanupTransientMode(working.mode) }
            return LocalAPIResponder.failure(
                code: .transcriptionFailed,
                message: "Post-processing partially failed: some segments were processed and at least one was not."
            )
        }

        // Report the provider and model that ACTUALLY ran (issue #314), not the
        // ones stored on the Mode — every fallback inside `AIPostProcessor`
        // happens after the Mode was read. `preset` is not remapped anywhere, so
        // it still comes straight off the working Mode.
        let labels = responseLabels(
            storedProvider: working.mode.postProcessingProvider,
            storedModel: working.mode.languageModel,
            storedProcessingMode: working.mode.postProcessingMode,
            resolvedProvider: mutationSignal.resolvedProvider,
            resolvedModel: mutationSignal.resolvedModel
        )
        let providerLabel = labels.provider
        let modelLabel = labels.model
        let presetLabel = working.mode.preset ?? "hyper"

        if working.isTransient { cleanupTransientMode(working.mode) }

        let response = PostProcessResponse(
            ok: true,
            text: result,
            provider: providerLabel,
            model: modelLabel,
            preset: presetLabel,
            latency_ms: latencyMs,
            post_processed: didPostProcess
        )
        return LocalAPIResponder.ok(response)
    }

    // MARK: - Response Labels

    /// Decide the `provider` and `model` fields of the response body: what
    /// actually ran, falling back to the working Mode's stored values only when
    /// nothing ran (issue #314).
    ///
    /// `AIPostProcessor` writes the resolved pair onto the request-scoped
    /// `MutationSignal` at — and only at — the four sites that also set
    /// `didMutate`, so a non-nil resolved value already means "an LLM produced
    /// this text". No separate `didMutate` cross-check is needed here, and a run
    /// that picked a model and then failed cannot leave a stale label behind.
    ///
    /// PROVIDER SPELLING IS PRESERVED. When the caller's stored provider names
    /// the same provider that ran, the caller's own spelling is echoed back
    /// verbatim, so this field changes ONLY in the cases that are the bug — a
    /// genuine provider substitution. (macOS stores the same
    /// `PostProcessingProvider.rawValue` strings it puts on the wire, so today
    /// only case differs; Windows has a real divergence here, which is why the
    /// rule is stated rather than assumed.)
    ///
    /// An empty resolved model is treated as absent: `""` is "no answer", not an
    /// answer. When nothing ran at all, `model` can still be `""` — that is
    /// honest, and `post_processed: false` already says so.
    ///
    /// NOTHING-RAN FALLS BACK THE SAME WAY THE ROUTER DOES. `storedProcessingMode`
    /// is `Mode.postProcessingMode`, and the stored-provider fallback below is the
    /// same three-step resolution as the two existing copies
    /// (`TranscriptionProviderRouter.checkPostProcessingProviderHealth` and
    /// `TranscriptionPipeline+Transcription`): a `.local` mode is `local_llm`
    /// whatever the stored string says, and an unset stored provider takes the
    /// processing mode's own default rather than an unconditional `hyperwhisper`.
    /// Reading `postProcessingProvider` alone answered `hyperwhisper` for a local
    /// mode with no stored provider, which is a provider the same mode would never
    /// have routed to.
    static func responseLabels(
        storedProvider: String?,
        storedModel: String?,
        storedProcessingMode: Int16,
        resolvedProvider: String?,
        resolvedModel: String?
    ) -> (provider: String, model: String) {
        let stored = storedProvider?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        let resolved = resolvedProvider?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        let processingMode = PostProcessingMode(rawValue: storedProcessingMode) ?? .off

        let provider: String
        if resolved.isEmpty {
            if processingMode == .local {
                provider = PostProcessingProvider.localLLM.rawValue
            } else if !stored.isEmpty {
                provider = stored
            } else {
                provider = processingMode.defaultProvider?.rawValue
                    ?? PostProcessingProvider.hyperwhisper.rawValue
            }
        } else if !stored.isEmpty, stored.caseInsensitiveCompare(resolved) == .orderedSame {
            provider = stored
        } else {
            provider = resolved
        }

        let resolvedModelValue = resolvedModel?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        let model = resolvedModelValue.isEmpty ? (storedModel ?? "") : resolvedModelValue

        return (provider, model)
    }

    // MARK: - Working Mode

    /// Build the Mode that drives the post-processing run. If the caller
    /// passes `mode_id` AND no overrides, we just use the stored mode. If
    /// overrides exist (or no mode_id), we build a transient Mode in the
    /// viewContext seeded from the stored mode (when present).
    @MainActor
    private static func buildWorkingMode(req: PostProcessRequest) throws -> (mode: Mode, isTransient: Bool) {
        let context = PersistenceController.shared.container.viewContext

        // Stored mode (optional).
        var baseline: Mode?
        if let modeId = req.mode_id?.trimmingCharacters(in: .whitespacesAndNewlines), !modeId.isEmpty {
            guard let stored = PersistenceController.shared.fetchMode(withId: modeId) else {
                throw TranscriptionError.providerNotAvailable(provider: "mode", reason: "No mode with id '\(modeId)'")
            }
            baseline = stored
        }

        // Are any overrides present?
        let hasOverride = req.preset != nil
            || req.prompt != nil
            || req.provider != nil
            || req.model != nil

        // No overrides + baseline → just use baseline (no transient).
        if let baseline, !hasOverride {
            // The saved Mode is the only signal we have about caller intent.
            // If post-processing is disabled on it, returning processed text
            // would silently override a privacy preference. Require the caller
            // to opt in explicitly via 'provider' or 'preset'.
            if baseline.postProcessingMode == 0 {
                let name = baseline.name ?? "(unnamed)"
                throw PostProcessInputError(
                    code: .invalidRequest,
                    message: "Mode '\(name)' has post-processing disabled. Supply an explicit 'provider' or 'preset' to override."
                )
            }
            return (baseline, false)
        }

        // Build transient — copy baseline fields, then apply overrides.
        let mode = Mode(context: context)
        mode.id = UUID()
        mode.name = "__local_api_postproc_transient__"
        mode.preset = baseline?.preset ?? "hyper"
        mode.language = baseline?.language ?? "auto"
        mode.model = baseline?.model ?? "base"
        mode.punctuation = baseline?.punctuation ?? true
        mode.capitalization = baseline?.capitalization ?? true
        mode.profanityFilter = baseline?.profanityFilter ?? false
        mode.customInstructions = baseline?.customInstructions ?? ""
        mode.userSystemPrompt = baseline?.userSystemPrompt
        mode.languageModel = baseline?.languageModel
        mode.cloudProvider = baseline?.cloudProvider
        mode.cloudTranscriptionModel = baseline?.cloudTranscriptionModel
        mode.postProcessingMode = baseline?.postProcessingMode ?? 1
        mode.postProcessingProvider = baseline?.postProcessingProvider ?? PostProcessingProvider.hyperwhisper.rawValue
        mode.englishSpelling = baseline?.englishSpelling ?? "american"
        mode.useStreamingTranscription = false
        mode.cloudAccuracyTier = baseline?.cloudAccuracyTier
        mode.removeTrailingPeriod = baseline?.removeTrailingPeriod ?? false
        mode.enableScreenOCR = baseline?.enableScreenOCR ?? false
        mode.geminiCustomPrompt = baseline?.geminiCustomPrompt
        mode.cloudPostProcessingModel = baseline?.cloudPostProcessingModel
        mode.cloudTranscriptionDomain = baseline?.cloudTranscriptionDomain
        mode.isDefault = false
        mode.isSystemProvided = false
        mode.sortOrder = Int16.max
        mode.createdDate = Date()
        mode.modifiedDate = Date()

        // Always enable post-processing for the transient mode — the request
        // asked for post-processing by hitting this endpoint.
        if mode.postProcessingMode == 0 { mode.postProcessingMode = 1 }

        // Apply overrides.
        if let p = req.preset?.trimmingCharacters(in: .whitespacesAndNewlines), !p.isEmpty {
            mode.preset = p
        }
        if let p = req.prompt?.trimmingCharacters(in: .whitespacesAndNewlines), !p.isEmpty {
            mode.preset = "custom"
            mode.customInstructions = p
        }
        if let providerId = req.provider?.trimmingCharacters(in: .whitespacesAndNewlines), !providerId.isEmpty {
            mode.postProcessingProvider = providerId
            if providerId == PostProcessingProvider.localLLM.rawValue {
                mode.postProcessingMode = 2 // local
            } else {
                mode.postProcessingMode = 1 // cloud
            }
        }
        if let modelId = req.model?.trimmingCharacters(in: .whitespacesAndNewlines), !modelId.isEmpty {
            mode.languageModel = modelId
        }

        return (mode, true)
    }

    @MainActor
    private static func cleanupTransientMode(_ mode: Mode) {
        let context = PersistenceController.shared.container.viewContext
        context.delete(mode)
    }
}

private struct PostProcessInputError: Error {
    let code: LocalAPIErrorCode
    let message: String
    let hint: String?

    init(code: LocalAPIErrorCode, message: String, hint: String? = nil) {
        self.code = code
        self.message = message
        self.hint = hint
    }
}
