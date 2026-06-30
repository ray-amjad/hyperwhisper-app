//
//  RecordingTranscriptionFlow+Streaming.swift
//  hyperwhisper
//
//  Created by modularization refactoring
//

import Foundation
import FluidAudio
import KeyboardShortcuts

extension RecordingTranscriptionFlow {

    // MARK: - Streaming Transcription Helpers

    private func normalizedStreamingLanguage(
        _ language: String,
        provider: String,
        model: String?
    ) -> String {
        switch StreamingTranscriptionProvider(rawValue: provider) {
        case .parakeetLocal:
            return Self.parakeetAllowedLanguage(language, model: model)
        default:
            // Nemotron flows through here — its language was already snapped
            // client-side via `normalizeStreamingLanguageForCurrentProvider`.
            // Callers always pass either a real code or "auto" (empty strings
            // are normalized to "auto" at the SettingsManager boundary via
            // `streamingLanguageEffective`), so just pass through.
            return language
        }
    }

    /// Parakeet has a hard allow-list: V2 only speaks English, V3 only speaks
    /// the 25 codes baked into the multilingual ship. Everything else snaps
    /// back to "en" — passing an unsupported code to the WordAgreementEngine
    /// regresses output silently.
    private static func parakeetAllowedLanguage(_ language: String, model: String?) -> String {
        let versionId = model ?? ParakeetModelManager.Constants.v3ModelId
        if versionId == ParakeetModelManager.Constants.v2ModelId { return "en" }
        return ParakeetModelManager.Constants.v3Languages.keys.contains(language) ? language : "en"
    }

    func resetStreamingSessionState(
        cancelService: Bool,
        resetRecordingUI: Bool = false,
        resetLastTranscription: Bool = false
    ) async {
        let serviceToCancel = streamingService

        recordingMaxDurationTimer?.invalidate()
        recordingMaxDurationTimer = nil
        streamingMaxDurationTimer?.invalidate()
        streamingMaxDurationTimer = nil
        streamingStartTime = nil
        isStreamingActive = false
        streamingService = nil
        streamingAccumulatedText = ""
        streamingPreviewTextSnapshot = ""
        streamingDeliveryMode = .directInsert
        streamingTargetBundleId = nil
        recordingLifecycle.audioLevel = 0

        if cancelService {
            await serviceToCancel?.cancel()
        }

        await MainActor.run {
            if resetRecordingUI {
                appState?.recordingState = .idle
                appState?.showRecordingDialog = false
            }
            appState?.streamingConnectionState = .idle
            appState?.streamingText = ""
            appState?.showStreamingPreview = false
            if resetLastTranscription {
                appState?.lastTranscription = ""
            }
            appState?.isStreamingShortcutTriggered = false
            KeyboardShortcuts.disable(.cancelRecording)
            StreamingPreviewWindowManager.shared.close()
        }
    }

    /// Start streaming transcription session.
    ///
    /// **STREAMING TRANSCRIPTION FLOW:**
    /// 1. Determine the streaming provider strategy based on settings
    /// 2. Create StreamingTranscriptionClient with the chosen strategy
    /// 3. Get authentication credentials (license key, device ID, or API key)
    /// 4. Build vocabulary terms from global vocabulary (if provider supports it)
    /// 5. Set up callbacks for transcript updates, completion, and errors
    /// 6. Start the streaming session (connects WebSocket and starts audio capture)
    ///
    /// **Provider Routing:**
    /// The provider parameter determines which strategy to use:
    /// - "hyperwhisperCloud" → HyperWhisperCloudStrategy (default, uses license/device auth)
    /// - "deepgram" → DeepgramStreamingStrategy (requires Deepgram API key)
    /// - "elevenLabs" → ElevenLabsStreamingStrategy (requires ElevenLabs API key)
    /// - "xai" → XAIStreamingStrategy (requires Grok/xAI API key)
    ///
    /// **Real-time Typing:**
    /// When `is_final` transcript updates arrive, they are typed directly into
    /// the focused application using AccessibilityHelper.typeTextAsync().
    ///
    /// **Parameters:**
    /// - `language`: The language code for transcription (e.g., "en", "auto")
    /// - `provider`: The streaming provider raw value (e.g., "hyperwhisperCloud", "deepgram", "elevenLabs", "xai")
    /// - `model`: The Deepgram model ID (e.g., "nova-3-general"), only used for Deepgram provider
    /// - `fastFormatting`: Whether to enable Deepgram's no_delay fast formatting
    func startStreamingTranscription(
        language streamingLanguageParam: String,
        provider: String = "hyperwhisperCloud",
        model: String? = nil,
        fastFormatting: Bool = true
    ) async {
        let normalizedLanguage = normalizedStreamingLanguage(streamingLanguageParam, provider: provider, model: model)

        AppLogger.audio.info("📡 Starting streaming transcription with language: \(normalizedLanguage, privacy: .public), provider: \(provider, privacy: .public)")
        SentryService.addBreadcrumb(
            message: "Streaming start requested",
            category: "audio.streaming",
            data: [
                "language": normalizedLanguage,
                "provider": provider,
                "model": model ?? "default",
                "attemptId": currentRecordingAttemptId ?? "none",
                "trigger": currentRecordingTriggerSource.rawValue
            ]
        )

        // RACE CONDITION FIX:
        // Clean up any existing streaming session before starting a new one.
        // This prevents orphaned WebSockets, audio taps, and receive loops when
        // the user rapidly toggles recording (e.g., pressing shortcut twice quickly).
        if let existingService = streamingService, isStreamingActive {
            AppLogger.audio.warning("⚠️ Cleaning up existing streaming session before starting new one")
            streamingMaxDurationTimer?.invalidate()
            streamingMaxDurationTimer = nil
            streamingStartTime = nil
            await existingService.cancel()
            streamingService = nil
        }

        // Reset streaming state
        isStreamingActive = true
        streamingAccumulatedText = ""
        streamingPreviewTextSnapshot = ""
        recordingLifecycle.audioLevel = 0

        // DELIVERY MODE CLASSIFICATION:
        // Decide up-front whether this session will type each chunk into the focused
        // app (reliable targets) or accumulate in a preview bubble and paste once at
        // the end (preview-only targets like terminals). Locked for the session — a
        // mid-session focus switch does not split text between two delivery paths.
        //
        // Use the bundle ID captured by the recording flow before the recording
        // panel was opened. A fresh frontmostApplication lookup here can race with
        // the panel's appearance and silently misclassify the target as nil/self,
        // which would route terminals into the live-insertion path and skip the
        // preview bubble.
        let targetBundleId = previousFrontmostBundleID
        let requiresPreview = await AccessibilityHelper.shared.requiresStreamingPreviewFallback(bundleId: targetBundleId)
        streamingTargetBundleId = targetBundleId
        streamingDeliveryMode = requiresPreview ? .previewOnly : .directInsert

        AppLogger.audio.info(
            "🎯 Streaming delivery mode=\(self.streamingDeliveryMode.rawValue, privacy: .public) target=\(targetBundleId ?? "unknown", privacy: .public) provider=\(provider, privacy: .public)"
        )
        SentryService.addBreadcrumb(
            message: "Streaming delivery mode selected",
            category: "audio.streaming",
            data: [
                "deliveryMode": streamingDeliveryMode.rawValue,
                "target": targetBundleId ?? "unknown",
                "provider": provider,
                "attemptId": currentRecordingAttemptId ?? "none"
            ]
        )

        if streamingDeliveryMode == .previewOnly {
            await MainActor.run {
                appState?.showStreamingPreview = true
                if let appState = appState {
                    StreamingPreviewWindowManager.shared.open(appState: appState)
                }
            }
        }

        // PROVIDER ROUTING:
        // Create the appropriate client based on the selected provider.
        // Remote paths wrap a strategy inside StreamingTranscriptionClient.
        // The local Parakeet path has its own dedicated client.
        let service: any StreamingClientProtocol
        var apiKey: String?

        switch StreamingTranscriptionProvider(rawValue: provider) {
        case .deepgram:
            // Deepgram direct streaming - requires user's Deepgram API key
            let deepgramKey = KeychainManager.shared.getAPIKey(for: .deepgram)
            guard !deepgramKey.isEmpty else {
                AppLogger.audio.error("❌ Streaming failed: Deepgram API key not configured")
                await cancelRecordingWithError("Deepgram API key not configured")
                return
            }
            apiKey = deepgramKey
            service = StreamingTranscriptionClient(strategy: DeepgramStreamingStrategy())

        case .elevenLabs:
            // ElevenLabs direct streaming - requires user's ElevenLabs API key
            let elevenLabsKey = KeychainManager.shared.getAPIKey(for: .elevenLabs)
            guard !elevenLabsKey.isEmpty else {
                AppLogger.audio.error("❌ Streaming failed: ElevenLabs API key not configured")
                await cancelRecordingWithError("ElevenLabs API key not configured")
                return
            }
            apiKey = elevenLabsKey
            service = StreamingTranscriptionClient(strategy: ElevenLabsStreamingStrategy())

        case .openAI:
            // OpenAI Realtime direct streaming - requires user's OpenAI API key
            let openAIKey = KeychainManager.shared.getAPIKey(for: .openAI)
            guard !openAIKey.isEmpty else {
                AppLogger.audio.error("❌ Streaming failed: OpenAI API key not configured")
                await cancelRecordingWithError("OpenAI API key not configured")
                return
            }
            apiKey = openAIKey
            service = StreamingTranscriptionClient(strategy: OpenAIStreamingStrategy())

        case .xai:
            // xAI direct streaming - requires user's Grok/xAI API key
            let xaiKey = KeychainManager.shared.getAPIKey(for: .grok)
            guard !xaiKey.isEmpty else {
                AppLogger.audio.error("❌ Streaming failed: xAI API key not configured")
                await cancelRecordingWithError("xAI API key not configured")
                return
            }
            apiKey = xaiKey
            service = StreamingTranscriptionClient(strategy: XAIStreamingStrategy())

        case .parakeetLocal:
            // On-device Parakeet streaming. The model id in `model` is the
            // Parakeet version identifier from settings (v2/v3).
            if #available(macOS 13.0, *) {
                let versionId = model ?? ParakeetModelManager.Constants.v3ModelId
                let asrVersion: AsrModelVersion = versionId.lowercased().contains("v2") ? .v2 : .v3

                // Model must already be downloaded — the UI layer surfaces
                // the download flow. Bail with a clear error otherwise.
                let directory = AsrModels.defaultCacheDirectory(for: asrVersion)
                guard AsrModels.modelsExist(at: directory) else {
                    AppLogger.audio.error("❌ Streaming failed: Parakeet model not downloaded")
                    await cancelRecordingWithError("Parakeet model not downloaded. Open Settings → Models to install it.")
                    return
                }

                service = LocalParakeetStreamingClient(version: asrVersion)
            } else {
                AppLogger.audio.error("❌ Streaming failed: Parakeet requires macOS 13+")
                await cancelRecordingWithError("Parakeet streaming requires macOS 13 or later.")
                return
            }

        case .nemotronLocal:
            // On-device Nemotron 3.5 streaming. The model id (`nemotron-asr-3.5-latin` or
            // `nemotron-asr-3.5-multilingual`) picks the on-disk variant. The current
            // mode language flows in as the per-session prompt_id via setLanguage(_:).
            if #available(macOS 14.0, *) {
                let requestedId = model ?? NemotronModelManager.Constants.multilingualModelId
                guard NemotronModelManager.variant(forModelId: requestedId) != nil else {
                    AppLogger.audio.error("❌ Streaming failed: unknown Nemotron model id \(requestedId)")
                    await cancelRecordingWithError("Unknown Nemotron model. Open Settings → Models to reconfigure.")
                    return
                }

                // Variant fallback: if the requested variant isn't installed but
                // the OTHER variant is, transparently use it so the user isn't
                // hard-blocked by a stale AppStorage value. AppStorage is updated
                // only AFTER the session starts successfully (see below) so a
                // mid-startup failure doesn't silently rewrite the user's pinned
                // preference.
                let resolvedId: String
                var pendingVariantPreferenceUpdate: String? = nil
                if NemotronModelManager.isVariantInstalled(requestedId) {
                    resolvedId = requestedId
                } else {
                    let other = requestedId == NemotronModelManager.Constants.multilingualModelId
                        ? NemotronModelManager.Constants.latinModelId
                        : NemotronModelManager.Constants.multilingualModelId
                    guard NemotronModelManager.isVariantInstalled(other) else {
                        AppLogger.audio.error("❌ Streaming failed: no Nemotron variant downloaded")
                        await cancelRecordingWithError("Nemotron model not downloaded. Open Settings → Models to install it.")
                        return
                    }
                    AppLogger.audio.warning("⚠️ Nemotron variant \(requestedId) not installed; falling back to \(other)")
                    resolvedId = other
                    pendingVariantPreferenceUpdate = other
                }
                guard let variant = NemotronModelManager.variant(forModelId: resolvedId) else {
                    await cancelRecordingWithError("Unknown Nemotron variant after fallback.")
                    return
                }

                // `normalizedLanguage` already carries the mode language (or "auto").
                // Re-snap the language against the RESOLVED variant's allow-list:
                // if we fell back from multilingual→latin and the user had "zh"
                // pinned, feeding "zh" to the Latin model would produce garbage,
                // so drop to auto-detect instead.
                var langHint: String? = (normalizedLanguage == "auto" || normalizedLanguage.isEmpty) ? nil : normalizedLanguage
                if let candidate = langHint,
                   let supported = NemotronModelManager.supportedLanguages(forModelId: resolvedId),
                   supported[candidate] == nil {
                    AppLogger.audio.warning("⚠️ Nemotron language \(candidate) not supported by \(resolvedId); falling back to auto-detect")
                    langHint = nil
                }

                self.pendingNemotronVariantPreferenceUpdate = pendingVariantPreferenceUpdate
                let provider = transcriptionPipeline?.nemotronProviderForStreaming
                service = LocalNemotronStreamingClient(variant: variant, language: langHint, provider: provider)
            } else {
                AppLogger.audio.error("❌ Streaming failed: Nemotron requires macOS 14+")
                await cancelRecordingWithError("Nemotron streaming requires macOS 14 or later.")
                return
            }

        default:
            // HyperWhisper Cloud (default) - uses license key or device ID
            service = StreamingTranscriptionClient(strategy: HyperWhisperCloudStrategy())
        }

        // Log provider selection for analytics
        SentryService.addBreadcrumb(
            message: "Streaming provider selected",
            category: "audio.streaming",
            data: [
                "provider": provider,
                "model": model ?? "default"
            ]
        )

        streamingService = service

        // Get authentication credentials
        // HyperWhisper Cloud uses license key or device ID
        // Direct providers use API key (already retrieved above)
        var licenseKey: String?
        var deviceId: String?

        if StreamingTranscriptionProvider(rawValue: provider) == .hyperwhisperCloud || provider == "hyperwhisperCloud" {
            guard let licenseManager = licenseManager else {
                AppLogger.audio.error("❌ Streaming failed: LicenseManager not available")
                await cancelRecordingWithError("Streaming transcription unavailable")
                return
            }

            let (identifier, isLicensed) = licenseManager.getTranscriptionIdentifier()
            licenseKey = isLicensed ? identifier : nil
            deviceId = isLicensed ? nil : identifier
        }

        // Get language for streaming (nil for auto-detect)
        let language: String? = (normalizedLanguage == "auto" || normalizedLanguage.isEmpty) ? nil : normalizedLanguage

        // Build vocabulary string from global vocabulary.
        // Remote strategies decide whether to consume it based on their
        // `supportsVocabulary` flag; the local Parakeet path ignores it
        // (vocab is applied to confirmed deltas at typing time instead).
        // Fetched on a background context — this runs on the recording-start
        // hot path and must not block the main thread on Core Data.
        let vocabulary = await PersistenceController.shared.fetchVocabularyEntriesInBackground()
        let vocabularyString: String? = buildVocabularyString(from: vocabulary)
        if language == nil && !vocabulary.isEmpty && vocabularyString != nil {
            AppLogger.audio.info("📝 Vocabulary provided but auto-detect language selected - vocabulary boosting may be inactive for Deepgram")
            SentryService.addBreadcrumb(
                message: "Vocabulary skipped (auto-detect language)",
                category: "audio.streaming",
                data: [
                    "reason": "autoDetectLanguage",
                    "vocabCount": vocabulary.count
                ]
            )
        }

        // STREAMING CALLBACKS:
        // Set up handlers for transcript updates, session completion, and errors

        // ON CONNECTION STATE CHANGE:
        // Update AppState with real-time connection status for UI feedback
        service.onConnectionStateChange = { [weak self] state in
            Task { @MainActor in
                self?.appState?.streamingConnectionState = state
                if AppLogger.isErrorLoggingEnabled {
                    SentryService.addBreadcrumb(
                        message: "Streaming connection state changed",
                        category: "audio.streaming",
                        data: [
                            "state": self?.streamingConnectionStateLabel(state) ?? "unknown",
                            "provider": provider,
                            "attemptId": self?.currentRecordingAttemptId ?? "none"
                        ]
                    )
                }
            }
        }

        // ON AUDIO LEVEL:
        // Streaming uses AVAudioEngine instead of SimpleRecorder, so feed its
        // metered levels into the same lifecycle metric the waveform already uses.
        service.onAudioLevel = { [weak self] level in
            self?.recordingLifecycle.audioLevel = level
        }

        // ON TRANSCRIPT UPDATE:
        // Route final deltas by delivery mode:
        //   - directInsert: type each chunk into the focused app as it arrives
        //   - previewOnly:  append to accumulated text; the preview bubble shows
        //                   everything so far, and a single paste happens at stop
        //
        // CJK OPTIMIZATION:
        // Capture language for the callback so we can use paste for CJK languages
        // instead of slow character-by-character typing. See TextInputService.typeSegment().
        let streamingLanguage = language
        let isLocalProvider = StreamingTranscriptionProvider(rawValue: provider)?.isLocal ?? false
        // Exact-vocab substitutions on the local path. Cloud providers
        // already receive vocabulary hints server-side; re-applying
        // substitutions there would fight the server's own normalization.
        let localVocabulary = isLocalProvider ? vocabulary : []
        service.onTranscriptUpdate = { [weak self] text, isFinal in
            guard let self = self else { return }

            if isFinal {
                AppLogger.audio.info(
                    "🧩 Streaming final delta received: chars=\(text.count, privacy: .public) spaces=\(Self.whitespaceCount(text), privacy: .public) text=\(Self.diagnosticExcerpt(text), privacy: .public)"
                )

                // VOICE COMMAND PROCESSING:
                // Detect and replace voice commands (e.g., "new line" → actual newlines)
                // This must happen before accumulation so both history and display reflect it.
                var processedText = TranscriptionTextProcessing.processVoiceCommands(text)

                // Local streaming gets exact-vocab parity with batch: fast,
                // deterministic substitutions on each confirmed delta.
                // Phonetic matching and AI post-processing are deliberately
                // skipped here (too slow / stream-incompatible).
                if !localVocabulary.isEmpty {
                    processedText = Self.applyStreamingVocabulary(processedText, vocabulary: localVocabulary)
                }

                // Accumulate for history + final paste
                if !self.streamingAccumulatedText.isEmpty && !processedText.isEmpty {
                    if !processedText.hasPrefix("\n") {
                        self.streamingAccumulatedText += " "
                    }
                }
                self.streamingAccumulatedText += processedText

                switch self.streamingDeliveryMode {
                case .directInsert:
                    // Clear volatile preview — this chunk just got committed into the app.
                    self.streamingPreviewTextSnapshot = ""
                    Task { @MainActor in
                        self.appState?.streamingText = ""
                    }

                    // Apply smart spacing - CJK languages don't use word-separating spaces
                    let textToType = SmartSpacing.appendTrailingSpace(
                        processedText,
                        modeLanguage: streamingLanguage ?? LanguageData.automaticCode
                    )
                    AppLogger.audio.info(
                        "⌨️ Streaming final delta after processing: chars=\(textToType.count, privacy: .public) spaces=\(Self.whitespaceCount(textToType), privacy: .public) text=\(Self.diagnosticExcerpt(textToType), privacy: .public)"
                    )
                    Task {
                        let success = await TextInputService.shared.typeSegment(textToType, language: streamingLanguage)
                        if success {
                            AppLogger.audio.debug("⌨️ Typed streaming segment: \(textToType.count, privacy: .public) chars")
                        } else {
                            AppLogger.audio.warning("⚠️ Failed to type streaming segment")
                        }
                    }

                case .previewOnly:
                    // No live typing — the full accumulated transcript stays visible
                    // in the preview bubble until we paste it at session end.
                    let snapshot = self.streamingAccumulatedText
                    self.streamingPreviewTextSnapshot = snapshot
                    Task { @MainActor in
                        self.appState?.streamingText = snapshot
                    }
                }
            } else {
                // Interim results drive the preview surface.
                // - directInsert: shown transiently in the notch until the next final chunk
                // - previewOnly: shown appended to accumulated text in the floating bubble
                switch self.streamingDeliveryMode {
                case .directInsert:
                    self.streamingPreviewTextSnapshot = text
                    Task { @MainActor in
                        self.appState?.streamingText = text
                    }
                case .previewOnly:
                    let base = self.streamingAccumulatedText
                    let joined = Self.composeStreamingPreviewText(committed: base, interim: text)
                    self.streamingPreviewTextSnapshot = joined
                    Task { @MainActor in
                        self.appState?.streamingText = joined
                    }
                }
                AppLogger.audio.debug("📝 Interim transcript: \(text.prefix(50), privacy: .public)...")
            }
        }

        // ON SESSION COMPLETE:
        // Session ended normally, update credits and save transcript
        service.onSessionComplete = { [weak self] durationSeconds, creditsUsed in
            guard let self = self else { return }
            AppLogger.audio.info("✅ Streaming session complete: \(durationSeconds, privacy: .public)s, \(creditsUsed, privacy: .public) credits")

            // Credits are already deducted on server side
            // TODO: Update local credit display if needed
        }

        // ON ERROR:
        // Handle streaming errors - show alert and cleanup
        service.onError = { [weak self] error in
            guard let self = self else { return }
            AppLogger.audio.error("❌ Streaming error: \(error.localizedDescription, privacy: .public)")
            SentryService.capture(
                error: error,
                message: "Streaming WebSocket error",
                extras: [
                    "attemptId": self.currentRecordingAttemptId ?? "none"
                ],
                tags: [
                    "component": "StreamingTranscription",
                    "provider": provider,
                    "operation": "onError"
                ]
            )

            // Full cleanup to prevent zombie sessions:
            // Without this, isStreamingActive stays true, recordingState stays .recording,
            // and the next toggle tries stopSession() on a dead WebSocket → app hangs.
            self.streamingMaxDurationTimer?.invalidate()
            self.streamingMaxDurationTimer = nil
            self.streamingStartTime = nil
            self.isStreamingActive = false
            self.streamingService = nil
            self.streamingAccumulatedText = ""
            self.streamingPreviewTextSnapshot = ""
            self.streamingDeliveryMode = .directInsert
            self.streamingTargetBundleId = nil
            self.recordingLifecycle.audioLevel = 0

            self.powerActivityManager.endPowerActivity()
            AccessibilityHelper.shared.endRecordingSession()

            Task { @MainActor in
                self.appState?.recordingState = .idle
                self.appState?.streamingConnectionState = .idle
                self.appState?.streamingText = ""
                self.appState?.showRecordingDialog = false
                self.appState?.showStreamingPreview = false
                self.appState?.isStreamingShortcutTriggered = false
                KeyboardShortcuts.disable(.cancelRecording)
                StreamingPreviewWindowManager.shared.close()
                self.appState?.showError("Streaming error: \(error.localizedDescription)")
            }
        }

        // BUILD SESSION CONFIG:
        // Superset of all fields needed by any provider. Each strategy uses only its relevant fields.
        let config = StreamingSessionConfig(
            licenseKey: licenseKey,
            deviceId: deviceId,
            language: language,
            vocabulary: vocabularyString,
            apiKey: apiKey,
            model: model,
            fastFormatting: fastFormatting
        )

        // Start the streaming session
        do {
            try await service.startSession(config: config)

            // Now that startup succeeded, commit any pending Nemotron variant
            // preference rewrite. Doing this after startSession means a failure
            // during warmup leaves the user's stored preference untouched.
            if let pendingVariant = pendingNemotronVariantPreferenceUpdate,
               let settings = settingsManager {
                await MainActor.run {
                    settings.streamingLocalNemotronVariant = pendingVariant
                }
            }
            pendingNemotronVariantPreferenceUpdate = nil

            AppLogger.audio.info("📡 Streaming session started successfully with provider: \(provider, privacy: .public)")
            SentryService.addBreadcrumb(
                message: "Streaming session started",
                category: "audio.streaming",
                data: [
                    "attemptId": currentRecordingAttemptId ?? "none",
                    "language": normalizedLanguage,
                    "provider": provider,
                    "model": model ?? "default"
                ]
            )

            // Start max duration safety timer
            streamingStartTime = Date()
            streamingMaxDurationTimer?.invalidate()
            streamingMaxDurationTimer = Timer.scheduledTimer(withTimeInterval: Self.maxRecordingDuration, repeats: false) { [weak self] _ in
                Task { @MainActor in
                    guard let self = self, self.isStreamingActive, !self.isStopInProgress else { return }
                    self.isStopInProgress = true
                    defer { self.isStopInProgress = false }

                    let modeName = self.appState?.selectedModeName ?? "Unknown"
                    AppLogger.audio.warning("⏱️ Streaming max duration (20 minutes) reached — auto-stopping")
                    self.appState?.showWarning("Streaming stopped — 20-minute safety limit reached")
                    self.currentRecordingTriggerSource = .autoStop
                    await self.stopStreamingTranscription(mode: modeName)
                }
            }
            AppLogger.audio.info("⏱️ Streaming max duration timer set (20 minutes)")

            // Update state to show streaming is active
            await MainActor.run {
                appState?.recordingState = .recording
            }
        } catch is CancellationError {
            // Rapid re-press during connect: the client already collapsed
            // connection state to .idle. Unwind local flow state without
            // surfacing an error toast — this matches the PR's test plan
            // ("rapid double-press while connecting: UI settles to idle,
            // no error state flash").
            AppLogger.audio.info("📡 Streaming session start cancelled (user re-press)")
            isStreamingActive = false
            streamingService = nil
            streamingMaxDurationTimer?.invalidate()
            streamingMaxDurationTimer = nil
            streamingStartTime = nil
            streamingPreviewTextSnapshot = ""
            streamingDeliveryMode = .directInsert
            streamingTargetBundleId = nil
            recordingLifecycle.audioLevel = 0
            pendingNemotronVariantPreferenceUpdate = nil
            await MainActor.run {
                appState?.recordingState = .idle
                appState?.showStreamingPreview = false
                appState?.streamingText = ""
                appState?.isStreamingShortcutTriggered = false
                KeyboardShortcuts.disable(.cancelRecording)
                StreamingPreviewWindowManager.shared.close()
            }
        } catch {
            AppLogger.audio.error("❌ Failed to start streaming session: \(error.localizedDescription, privacy: .public)")
            isStreamingActive = false
            streamingService = nil
            streamingPreviewTextSnapshot = ""
            streamingDeliveryMode = .directInsert
            streamingTargetBundleId = nil
            recordingLifecycle.audioLevel = 0
            pendingNemotronVariantPreferenceUpdate = nil
            await MainActor.run {
                appState?.showStreamingPreview = false
                StreamingPreviewWindowManager.shared.close()
            }
            await cancelRecordingWithError("Failed to start streaming: \(error.localizedDescription)")
        }
    }

    /// Stop streaming transcription and save results.
    ///
    /// **STREAMING STOP FLOW:**
    /// 1. Stop the streaming service (closes WebSocket, stops audio capture)
    /// 2. For previewOnly delivery mode, paste the accumulated text into the target
    /// 3. Create transcript in history with accumulated text
    /// 4. Clean up streaming state
    /// 5. Update UI state
    func stopStreamingTranscription(mode: String) async {
        AppLogger.audio.info("🛑 Stopping streaming transcription")
        SentryService.addBreadcrumb(
            message: "Streaming stop requested",
            category: "audio.streaming",
            data: [
                "mode": mode,
                "attemptId": currentRecordingAttemptId ?? "none"
            ]
        )

        guard let service = streamingService, isStreamingActive else {
            AppLogger.audio.warning("⚠️ stopStreamingTranscription called but no active streaming session")
            if streamingService != nil || isStreamingActive {
                await resetStreamingSessionState(cancelService: true, resetRecordingUI: true)
                powerActivityManager.endPowerActivity()
                AccessibilityHelper.shared.endRecordingSession()
            }
            return
        }

        // Stop the streaming session
        await service.stopSession()
        await Task.yield()
        SentryService.addBreadcrumb(
            message: "Streaming session stopped",
            category: "audio.streaming",
            data: ["attemptId": currentRecordingAttemptId ?? "none"]
        )

        let commitText = bestStreamingCommitText(for: streamingDeliveryMode)

        // PREVIEW-ONLY COMMIT:
        // Targets that can't take live HID typing (terminals) accumulated into the
        // preview bubble instead. Now that the session is over, paste the full
        // transcript into the target in a single shot.
        let deliveryMode = streamingDeliveryMode
        let sessionTarget = streamingTargetBundleId
        if deliveryMode == .previewOnly, !commitText.isEmpty {
            let pasteSucceeded = await TextInputService.shared.pasteTextForStreaming(
                commitText,
                targetPID: previousFrontmostPID
            )
            AppLogger.audio.info(
                "🎯 Streaming preview commit: pasted=\(pasteSucceeded, privacy: .public) chars=\(commitText.count, privacy: .public) target=\(sessionTarget ?? "unknown", privacy: .public)"
            )
            if !pasteSucceeded {
                SentryService.addBreadcrumb(
                    message: "Streaming preview paste failed",
                    category: "audio.streaming",
                    data: [
                        "target": sessionTarget ?? "unknown",
                        "chars": commitText.count,
                        "attemptId": currentRecordingAttemptId ?? "none"
                    ]
                )
            }
        }

        // Save accumulated transcript to history
        if !commitText.isEmpty {
            let processingTranscript = PersistenceController.shared.createProcessingTranscript(
                duration: 0, // Duration is tracked on server side
                mode: mode,
                audioFilePath: nil // No audio file for streaming
            )

            // Update transcript with streamed text
            // Use the strategy's provider label for history entries (e.g., "Deepgram (Streaming)")
            let providerLabel = service.transcriptionProviderLabel
            PersistenceController.shared.updateTranscriptWithTranscription(
                processingTranscript,
                transcribedText: commitText,
                postProcessedText: nil, // No post-processing for streaming yet
                transcriptionProvider: providerLabel,
                postProcessingProvider: nil
            )

            AppLogger.audio.info("💾 Saved streaming transcript: \(commitText.count, privacy: .public) chars")

            // Update lastTranscription for UI
            await MainActor.run {
                appState?.lastTranscription = commitText
            }
        }

        // Cleanup streaming state
        recordingMaxDurationTimer?.invalidate()
        recordingMaxDurationTimer = nil
        streamingMaxDurationTimer?.invalidate()
        streamingMaxDurationTimer = nil
        streamingStartTime = nil
        isStreamingActive = false
        streamingService = nil
        streamingAccumulatedText = ""
        streamingPreviewTextSnapshot = ""
        streamingDeliveryMode = .directInsert
        streamingTargetBundleId = nil
        recordingLifecycle.audioLevel = 0

        // Update UI state
        await MainActor.run {
            appState?.recordingState = .idle
            appState?.streamingConnectionState = .idle
            appState?.streamingText = ""
            appState?.showRecordingDialog = false
            appState?.showStreamingPreview = false
            appState?.isStreamingShortcutTriggered = false  // Reset streaming shortcut flag
            KeyboardShortcuts.disable(.cancelRecording)
            StreamingPreviewWindowManager.shared.close()
        }

        powerActivityManager.endPowerActivity()
        AccessibilityHelper.shared.endRecordingSession()

        AppLogger.audio.info(
            "✅ Streaming transcription stopped and saved. deliveryMode=\(deliveryMode.rawValue, privacy: .public) target=\(sessionTarget ?? "unknown", privacy: .public)"
        )
    }

    /// Case-insensitive, word-boundary-anchored exact-match vocabulary
    /// substitution. Used on the local Parakeet streaming path so that
    /// confirmed deltas benefit from the user's vocabulary before typing.
    ///
    /// Delegates to `VocabularyProcessor.applyHardenedReplacement` — the same
    /// hardened matcher the batch path uses — so streaming no longer mangles
    /// substrings (e.g. "Kat"→"Katherine" no longer rewrites "category"). This
    /// trades the previous `.diacriticInsensitive` matching for word-boundary
    /// safety, deliberately matching the batch matcher's behavior.
    fileprivate static func applyStreamingVocabulary(_ text: String, vocabulary: [VocabularyEntrySnapshot]) -> String {
        var updated = text
        for entry in vocabulary {
            guard let replacement = entry.replacement?.trimmingCharacters(in: .whitespacesAndNewlines),
                  !replacement.isEmpty else {
                continue
            }
            updated = VocabularyProcessor.applyHardenedReplacement(to: updated, word: entry.word, replacement: replacement)
        }
        return updated
    }

    fileprivate func bestStreamingCommitText(for deliveryMode: StreamingDeliveryMode) -> String {
        let committed = streamingAccumulatedText.trimmingCharacters(in: .whitespacesAndNewlines)
        guard deliveryMode == .previewOnly else {
            return committed
        }

        let preview = streamingPreviewTextSnapshot.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !preview.isEmpty else { return committed }
        guard !committed.isEmpty else {
            AppLogger.audio.info("🎯 Streaming commit using preview-only interim snapshot: committedChars=0 previewChars=\(preview.count, privacy: .public)")
            return preview
        }

        let normalizedCommitted = Self.normalizedStreamingComparisonText(committed)
        let normalizedPreview = Self.normalizedStreamingComparisonText(preview)
        if normalizedPreview.hasPrefix(normalizedCommitted), preview.count > committed.count {
            AppLogger.audio.info(
                "🎯 Streaming commit using preview snapshot tail: committedChars=\(committed.count, privacy: .public) previewChars=\(preview.count, privacy: .public)"
            )
            return preview
        }

        return committed
    }

    fileprivate static func composeStreamingPreviewText(committed: String, interim: String) -> String {
        let trimmedCommitted = committed.trimmingCharacters(in: .whitespacesAndNewlines)
        let trimmedInterim = interim.trimmingCharacters(in: .whitespacesAndNewlines)

        guard !trimmedInterim.isEmpty else { return trimmedCommitted }
        guard !trimmedCommitted.isEmpty else { return trimmedInterim }

        let normalizedCommitted = normalizedStreamingComparisonText(trimmedCommitted)
        let normalizedInterim = normalizedStreamingComparisonText(trimmedInterim)
        if normalizedInterim.hasPrefix(normalizedCommitted) {
            return trimmedInterim
        }

        return trimmedCommitted + " " + trimmedInterim
    }

    fileprivate static func normalizedStreamingComparisonText(_ text: String) -> String {
        text
            .trimmingCharacters(in: .whitespacesAndNewlines)
            .components(separatedBy: .whitespacesAndNewlines)
            .filter { !$0.isEmpty }
            .joined(separator: " ")
            .lowercased()
    }

    fileprivate static func whitespaceCount(_ text: String) -> Int {
        text.reduce(into: 0) { count, character in
            if character.isWhitespace {
                count += 1
            }
        }
    }

    fileprivate static func diagnosticExcerpt(_ text: String, limit: Int = 120) -> String {
        let escaped = text
            .replacingOccurrences(of: "\n", with: "\\n")
            .replacingOccurrences(of: "\t", with: "\\t")
        let excerpt = String(escaped.prefix(limit))
        return "\"\(excerpt)\""
    }

    /// Build a comma-separated vocabulary string from vocabulary entries.
    /// Used for Deepgram's keyterm parameter.
    ///
    /// - Parameter vocabulary: Array of vocabulary entry snapshots
    /// - Returns: Comma-separated string of vocabulary terms, or nil if empty
    private func buildVocabularyString(from vocabulary: [VocabularyEntrySnapshot]) -> String? {
        var terms: [String] = []
        var seen = Set<String>()

        for item in vocabulary {
            let word = item.word.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !word.isEmpty,
                  !seen.contains(word.lowercased()) else {
                continue
            }
            seen.insert(word.lowercased())
            terms.append(word)

            // Deepgram limit: 100 terms max
            if terms.count >= 100 { break }
        }

        return terms.isEmpty ? nil : terms.joined(separator: ",")
    }

    private func streamingConnectionStateLabel(_ state: StreamingConnectionState) -> String {
        switch state {
        case .idle:
            return "idle"
        case .warmingUp:
            return "warmingUp"
        case .connecting:
            return "connecting"
        case .ready:
            return "ready"
        case .streaming:
            return "streaming"
        case .reconnecting:
            return "reconnecting"
        case .disconnecting:
            return "disconnecting"
        case .error:
            return "error"
        }
    }
}
