//
//  TranscriptionPipeline+Transcription.swift
//  hyperwhisper
//
//  Core transcription flow, state handling, and instrumentation.
//

import Foundation

extension TranscriptionPipeline {

    /// Convenience wrapper returning only the final text.
    func transcribe(audioURL: URL, mode: Mode?, recordingSession: RecordingSession? = nil) async throws -> String {
        let result = try await transcribeWithDetails(audioURL: audioURL, mode: mode, recordingSession: recordingSession)
        return result.text
    }

    /// Full transcription pipeline returning raw and processed text.
    ///
    /// **Flow:**
    /// 1. Ensure manager is idle (cancel previous if needed)
    /// 2. Select provider and transcribe
    /// 3. Apply vocabulary and post-processing
    /// 4. Cache and return the result
    ///
    /// - Parameters:
    ///   - audioURL: URL of the audio file to transcribe
    ///   - mode: Transcription mode with settings
    ///   - recordingSession: Optional recording session for tracking
    ///   - applicationContext: Optional app context captured at recording start
    /// - Returns: Full transcription result with both raw and processed text
    func transcribeWithDetails(
        audioURL: URL,
        mode: Mode?,
        recordingSession: RecordingSession? = nil,
        applicationContext: ApplicationContext? = nil
    ) async throws -> TranscriptionResult {
        // If a transcription is already running, cancel it so the latest request wins.
        // This guards against rapid hotkey presses and intentional re-records.
        if !state_isReadyForTranscription() {
            AppLogger.transcription.info("Cancelling previous transcription to start new one (was in state: \(self.state))")

            currentTask?.cancel()
            localProvider.cancelTranscription()

            // Brief wait for cancellation to propagate through async boundaries.
            try await Task.sleep(nanoseconds: 100_000_000) // 100ms

            await MainActor.run { state = .idle }
            currentTask = nil
        }

        // Cancel any lingering previous task (safety net).
        currentTask?.cancel()

        await MainActor.run { state = .transcribing(provider: "Starting...", progress: 0.0) }

        // Capture metadata for error reporting.
        var capturedProviderName: String = "Unknown"
        var capturedModelString: String = mode?.model ?? "base"
        var capturedUseCloud: Bool = (mode?.model ?? "base").lowercased() == "cloud"
        var capturedLanguage: String = "auto"
        var capturedPostProcessingMode: String = "off"
        var capturedPostProcessingProvider: String = "unknown"
        var capturedShouldRunPostProcessing: Bool = false
        var capturedIsHyperwhisperTranscription: Bool = false
        let slowTranscriptionThresholdMs = 8_000
        let slowTranscriptionWithPostProcessingThresholdMs = 15_000
        let slowTranscriptionWithLocalLLMThresholdMs = 45_000

        let transcriptionStart = Date()
        var stage = "start"
        var stageStart = transcriptionStart
        var stageTimeline: [String] = []

        // Track stage timings for diagnostics and Sentry breadcrumbs.
        func markStage(_ newStage: String) {
            let now = Date()
            let elapsedMs = Int(now.timeIntervalSince(stageStart) * 1000)
            let totalMs = Int(now.timeIntervalSince(transcriptionStart) * 1000)
            let completedStage = stage
            let stageSummary = "\(completedStage)=\(elapsedMs)ms@\(totalMs)ms"
            stageTimeline.append(stageSummary)
            AppLogger.transcription.debug(
                "Transcription stage completed · stage=\(completedStage, privacy: .public) · elapsedMs=\(elapsedMs, privacy: .public) · totalMs=\(totalMs, privacy: .public)"
            )
            stage = newStage
            stageStart = now

            if AppLogger.isErrorLoggingEnabled {
                SentryService.addBreadcrumb(
                    message: "transcription_stage",
                    category: "transcription",
                    data: ["stage": newStage, "totalMs": totalMs]
                )
            }
        }

        let task: Task<TranscriptionResult, Error> = Task { () -> TranscriptionResult in
            // Snapshot vocabulary for this run.
            markStage("fetch_vocabulary")
            let vocabulary = PersistenceController.shared.fetchAllVocabularyItems()

            // Select provider using the coordinator (local/cloud routing + health checks).
            markStage("select_provider")
            let provider = try await providerCoordinator.selectProvider(for: mode, vocabulary: vocabulary)

            capturedProviderName = provider.name
            capturedModelString = mode?.model ?? "base"
            capturedUseCloud = (mode?.model ?? "base").lowercased() == "cloud"

            await MainActor.run { state = .transcribing(provider: provider.name, progress: 0.1) }

            // Convert "auto" to nil for provider auto-detection.
            let languageArg: String? = {
                guard let raw = mode?.language?.lowercased() else { return nil }
                return raw == "auto" ? nil : raw
            }()
            capturedLanguage = languageArg ?? "auto"

            // Thread pre-captured application context to HyperWhisper Cloud provider
            // so server-side post-processing sees the user's actual app, not HyperWhisper.
            if let hwProvider = provider as? HyperWhisperCloudProvider {
                hwProvider.applicationContext = applicationContext
            }

            // Resolve post-processing mode and provider before transcription so we can
            // pre-flight the health check concurrently with the transcribe call.
            let processingMode = mode.flatMap { PostProcessingMode(rawValue: $0.postProcessingMode) } ?? .off
            let needsPostProcessing = processingMode != .off
            capturedPostProcessingMode = String(describing: processingMode)

            let resolvedPostProcessingProviderId: String = {
                if processingMode == .local {
                    return PostProcessingProvider.localLLM.rawValue
                }
                return mode?.postProcessingProvider ?? processingMode.defaultProvider?.rawValue ?? PostProcessingProvider.hyperwhisper.rawValue
            }()
            let resolvedPostProcessingProvider = PostProcessingProvider(rawValue: resolvedPostProcessingProviderId)
            capturedPostProcessingProvider = resolvedPostProcessingProviderId

            // Pre-flight the health check during transcription for providers that need it.
            // For non-HyperWhisper PP providers, shouldRunPostProcessing is guaranteed true
            // regardless of transcription result, so the check will always be needed.
            let preflightHealthCheck: Task<Void, Error>?
            if needsPostProcessing,
               let postProvider = resolvedPostProcessingProvider,
               postProvider != .hyperwhisper,
               postProvider.requiresHealthCheck {
                preflightHealthCheck = Task {
                    try await self.providerCoordinator.checkPostProcessingProviderHealth(for: mode)
                }
            } else {
                preflightHealthCheck = nil
            }

            // Auto-capture timestamps (gated by setting). Whisper produces them;
            // every other provider ignores this no-op and pays nothing.
            if self.settingsManager?.storeWordTimestamps == true {
                provider.setTimestampGranularities([.segment, .word])
            }

            markStage("transcribe")
            let text = try await provider.transcribe(
                audioURL: audioURL,
                language: languageArg,
                mode: mode,
                vocabulary: vocabulary
            )

            // Read timestamps produced by the run (nil unless the engine produced
            // them). Same ordering guarantee as `detectedLanguage` below.
            let timestamps = provider.lastTimestamps

            // Provider-detected language (nil unless the provider surfaces one,
            // e.g. HyperWhisper Cloud). Read immediately after the awaited
            // transcribe(...) — transcriptions are serialized per session so this
            // is ordered with respect to the call that produced it. Used to gate
            // filler-word removal when the requested language was "auto".
            let detectedLanguage = provider.detectedLanguage

            // Use server-provided AI-enhanced text when HyperWhisper Cloud returns it.
            let hyperwhisperCloudAIText: String?
            if let hwProvider = provider as? HyperWhisperCloudProvider,
               let aiText = hwProvider.aiEnhancedText {
                hyperwhisperCloudAIText = aiText
                AppLogger.transcription.info("🔄 HyperWhisper Cloud returned AI-enhanced text · chars=\(aiText.count, privacy: .public)")
            } else {
                hyperwhisperCloudAIText = nil
            }

            // Decide whether to run client-side AI post-processing.
            // We run it when post-processing is enabled AND:
            // - Provider is not HyperWhisper Cloud, or
            // - HyperWhisper Cloud is only used for post-processing, or
            // - HyperWhisper Cloud transcription returned no AI text.
            let isHyperwhisperTranscription = provider is HyperWhisperCloudProvider
            capturedIsHyperwhisperTranscription = isHyperwhisperTranscription
            let shouldRunPostProcessing = needsPostProcessing && (
                resolvedPostProcessingProvider != .hyperwhisper ||
                !isHyperwhisperTranscription ||
                hyperwhisperCloudAIText == nil
            )
            capturedShouldRunPostProcessing = shouldRunPostProcessing

            if shouldRunPostProcessing {
                markStage("post_processing_health_check")
                if let preflightHealthCheck {
                    try await preflightHealthCheck.value
                } else {
                    try await providerCoordinator.checkPostProcessingProviderHealth(for: mode)
                }
            }

            AppLogger.transcription.info("🔍 Post-processing check:")
            AppLogger.transcription.info("  - Mode name: \(mode?.name ?? "nil")")
            AppLogger.transcription.info("  - Preset: \(mode?.preset ?? "nil")")
            AppLogger.transcription.info("  - postProcessingMode value: \(processingMode.rawValue) (0=off, 1=cloud, 2=local)")
            AppLogger.transcription.info("  - needsPostProcessing: \(needsPostProcessing)")
            AppLogger.transcription.info("  - aiPostProcessor exists: \(self.aiPostProcessor != nil)")
            AppLogger.transcription.info("  - resolved postProcessingProvider: \(resolvedPostProcessingProviderId)")
            AppLogger.transcription.info("  - shouldRunPostProcessing: \(shouldRunPostProcessing)")

            let finalText: String
            if let aiText = hyperwhisperCloudAIText {
                // Branch: server-side AI already applied.
                finalText = vocabularyProcessor.applyVocabularyReplacements(aiText, mode: mode)
                AppLogger.transcription.info("✅ Using HyperWhisper Cloud AI-enhanced text with vocabulary replacements applied")
            } else if shouldRunPostProcessing {
                // Branch: client-side AI post-processing.
                await MainActor.run { state = .postProcessing }
                await MainActor.run { [weak self] in
                    self?.appState?.recordingState = .postProcessing
                }
                markStage("post_processing")

                let aiProcessedText: String
                if let processor = self.aiPostProcessor {
                    let providerName = resolvedPostProcessingProvider?.displayName ?? resolvedPostProcessingProviderId
                    AppLogger.transcription.info("✅ Starting AI post-processing with provider: \(providerName, privacy: .public)")
                    aiProcessedText = try await processor.performAIPostProcessingStreaming(
                        text: text,
                        mode: mode,
                        applicationContext: applicationContext
                    )
                } else {
                    AppLogger.transcription.warning("⚠️ Post-processing needed but aiPostProcessor is nil! Check initialization.")
                    aiProcessedText = text
                }

                // Apply vocabulary replacements after AI processing.
                finalText = vocabularyProcessor.applyVocabularyReplacements(aiProcessedText, mode: mode)
            } else {
                // Branch: no post-processing.
                if needsPostProcessing {
                    AppLogger.transcription.info("ℹ️ Post-processing handled by HyperWhisper Cloud; skipping client-side step")
                } else {
                    AppLogger.transcription.info("ℹ️ Post-processing skipped (mode setting = \(mode?.postProcessingMode ?? -1) or nil mode)")
                }
                finalText = settingsManager?.removeFillerWords == false
                    ? text
                    : TranscriptionTextProcessing.removeFillerWords(text, language: detectedLanguage ?? languageArg)
            }

            markStage("cache_result")
            cache.cacheTranscription(finalText, for: audioURL)
            markStage("finalize")

            // Determine if post-processing occurred and by which provider.
            let wasPostProcessed: Bool
            let postProcessingProvider: String?
            let postProcessingSkipped: Bool

            if hyperwhisperCloudAIText != nil {
                wasPostProcessed = true
                postProcessingProvider = "hyperwhisper"
                postProcessingSkipped = false
            } else if shouldRunPostProcessing {
                // HONEST SIGNAL: only mark wasPostProcessed if AIPostProcessor actually ran an
                // LLM and returned mutated text. When the local runtime is dead or a catch
                // swallowed the error and returned the raw transcript, didMutateLastRun stays
                // false and we report `postProcessingSkipped=true` so the log line reflects reality.
                let didMutate = self.aiPostProcessor?.didMutateLastRun ?? false
                wasPostProcessed = didMutate
                postProcessingProvider = didMutate ? resolvedPostProcessingProviderId : nil
                postProcessingSkipped = !didMutate
            } else {
                wasPostProcessed = false
                postProcessingProvider = nil
                postProcessingSkipped = false
            }

            let result = TranscriptionResult(
                text: finalText,
                rawText: text,
                timestamp: Date(),
                duration: 0,
                mode: mode,
                provider: provider.name,
                wasPostProcessed: wasPostProcessed,
                postProcessingProvider: postProcessingProvider,
                postProcessingSkipped: postProcessingSkipped,
                timestamps: timestamps
            )

            await MainActor.run {
                self.lastTranscription = result
            }

            return result
        }

        currentTask = task

        do {
            let result = try await task.value
            await MainActor.run { state = .idle }

            let now = Date()
            let totalElapsedMs = Int(now.timeIntervalSince(transcriptionStart) * 1000)
            let currentStageElapsedMs = Int(now.timeIntervalSince(stageStart) * 1000)
            let stageTimelineSnapshot = stageTimeline + ["\(stage)=\(currentStageElapsedMs)ms@\(totalElapsedMs)ms (completed)"]
            let stageTimelineSummary = stageTimelineSnapshot.joined(separator: " | ")
            let skippedSuffix = result.postProcessingSkipped ? " · postProcessingSkipped=true" : ""
            let logMessage =
                "Transcription completed · provider=\(capturedProviderName) · mode=\(mode?.name ?? "nil") · totalMs=\(totalElapsedMs) · postProcessed=\(result.wasPostProcessed)\(skippedSuffix) · stageTimeline=\(stageTimelineSummary)"

            let wasPostProcessed = result.wasPostProcessed
            let isLocalLLM = result.postProcessingProvider == PostProcessingProvider.localLLM.rawValue
            let effectiveThreshold: Int
            if isLocalLLM {
                effectiveThreshold = slowTranscriptionWithLocalLLMThresholdMs
            } else if wasPostProcessed {
                effectiveThreshold = slowTranscriptionWithPostProcessingThresholdMs
            } else {
                effectiveThreshold = slowTranscriptionThresholdMs
            }
            if totalElapsedMs >= effectiveThreshold {
                AppLogger.transcription.warning("\(logMessage, privacy: .public)")
                if AppLogger.isErrorLoggingEnabled {
                    // Successful slow transcriptions are expected for longer audio,
                    // cloud providers, and post-processing. Keep local diagnostics
                    // without creating high-volume non-actionable Sentry issues.
                    SentryService.addBreadcrumb(
                        message: "slow_transcription_completed",
                        category: "transcription.performance",
                        level: .warning,
                        data: [
                            "actualProvider": capturedProviderName,
                            "modeName": mode?.name ?? "nil",
                            "language": capturedLanguage,
                            "postProcessingMode": capturedPostProcessingMode,
                            "postProcessingProvider": capturedPostProcessingProvider,
                            "shouldRunPostProcessing": capturedShouldRunPostProcessing,
                            "wasPostProcessed": wasPostProcessed,
                            "isHyperwhisperTranscription": capturedIsHyperwhisperTranscription,
                            "totalElapsedMs": totalElapsedMs,
                            "effectiveThresholdMs": effectiveThreshold,
                            "finalStage": stage,
                            "stageTimeline": stageTimelineSnapshot,
                            "recordingSessionID": recordingSession?.id?.uuidString ?? "nil"
                        ]
                    )
                }
            } else {
                AppLogger.transcription.info("\(logMessage, privacy: .public)")
            }

            return result
        } catch {
            if error is CancellationError {
                await MainActor.run { state = .idle }
            } else {
                await MainActor.run { state = .error(message: error.localizedDescription) }
                if AppLogger.isErrorLoggingEnabled {
                    // Include audio + provider context for diagnostics.
                    let fileExists = FileManager.default.fileExists(atPath: audioURL.path)
                    let fileReadable = FileManager.default.isReadableFile(atPath: audioURL.path)
                    let fileSize = (try? FileManager.default.attributesOfItem(atPath: audioURL.path)[.size] as? Int64) ?? -1
                    let classification = classifyTranscriptionError(error)
                    let now = Date()
                    let totalElapsedMs = Int(now.timeIntervalSince(transcriptionStart) * 1000)
                    let currentStageElapsedMs = Int(now.timeIntervalSince(stageStart) * 1000)
                    let stageTimelineSnapshot = stageTimeline + ["\(stage)=\(currentStageElapsedMs)ms@\(totalElapsedMs)ms (failed)"]

                    var extras: [String: Any] = [
                        "actualProvider": capturedProviderName,
                        "modelString": capturedModelString,
                        "useCloud": capturedUseCloud,
                        "modeName": mode?.name ?? "nil",
                        "language": capturedLanguage,
                        "postProcessingMode": capturedPostProcessingMode,
                        "postProcessingProvider": capturedPostProcessingProvider,
                        "shouldRunPostProcessing": capturedShouldRunPostProcessing,
                        "isHyperwhisperTranscription": capturedIsHyperwhisperTranscription,
                        "audioFilePath": audioURL.path,
                        "fileExists": fileExists,
                        "fileReadable": fileReadable,
                        "fileSizeBytes": fileSize,
                        "fileExtension": audioURL.pathExtension,
                        "usedCAFFallback": audioURL.pathExtension.lowercased() == "caf",
                        "errorType": String(describing: type(of: error)),
                        "errorDomain": (error as NSError).domain,
                        "errorCode": (error as NSError).code,
                        "errorCategory": classification.category,
                        "errorKind": classification.kind,
                        "errorRetryable": classification.retryable,
                        "errorStage": stage,
                        "stageElapsedMs": currentStageElapsedMs,
                        "totalElapsedMs": totalElapsedMs,
                        "stageTimeline": stageTimelineSnapshot,
                        "transcriptionState": String(describing: state),
                        "recordingSessionID": recordingSession?.id?.uuidString ?? "nil"
                    ]
                    if let httpStatus = classification.httpStatus {
                        extras["errorHttpStatus"] = httpStatus
                    }

                    if shouldCaptureTranscriptionErrorInSentry(error) {
                        SentryService.capture(
                            error: error,
                            message: "TranscriptionPipeline.transcribeWithDetails failed",
                            extras: extras,
                            tags: [
                                "component": "transcription",
                                "error_class": classification.category,
                                "error_stage": stage
                            ],
                            fingerprint: Self.sentryFingerprintForTranscriptionFailure(
                                classification: classification,
                                stage: stage
                            )
                        )
                    }
                }
            }
            throw error
        }
    }

    /// True when the manager can accept a new transcription request.
    func state_isReadyForTranscription() -> Bool {
        switch state {
        case .idle, .error:
            return true
        default:
            return false
        }
    }
}
