//
//  AppleSpeechAnalyzerProvider.swift
//  hyperwhisper
//
//  TranscriptionProvider implementation for Apple's SpeechAnalyzer API (macOS 26+)
//  Uses on-device speech recognition via the Speech framework's SpeechTranscriber
//

#if canImport(Speech)
import Foundation
import Speech
import AVFoundation
import CoreMedia
import os

// APPLE SPEECH ANALYZER PROVIDER:
// TranscriptionProvider implementation wrapping Apple's SpeechAnalyzer API
// Requires macOS 26+ and on-device speech assets to be downloaded
@available(macOS 26.0, *)
final class AppleSpeechAnalyzerProvider: TranscriptionProvider {

    let name: String = "Apple Speech"

    private let logger = Logger(subsystem: "com.hyperwhisper.app", category: "SpeechAnalyzer")

    init() {}

    // AVAILABILITY CHECK:
    // Returns true if the SpeechTranscriber API is available on this device
    var isAvailable: Bool {
        SpeechTranscriber.isAvailable
    }

    // PREPARE IF NEEDED:
    // Pre-downloads assets and preheats the analyzer for faster first transcription
    func prepareIfNeeded(language: String?, modelId: String? = nil) async throws {
        let locale = await resolveLocale(language: language)
        let transcriber = SpeechTranscriber(locale: locale, preset: .transcription)

        // Ensure assets are downloaded
        try await ensureAssets(for: locale, using: transcriber)

        // Preheat the analyzer so subsequent transcriptions start faster
        do {
            let analyzer = SpeechAnalyzer(modules: [transcriber])
            try await analyzer.prepareToAnalyze(in: nil)
            logger.info("SpeechAnalyzer preheated for locale: \(locale.identifier, privacy: .public)")
        } catch {
            let nsError = error as NSError
            logger.error("Failed to preheat SpeechAnalyzer; errorDomain=\(nsError.domain, privacy: .public) errorCode=\(nsError.code, privacy: .public)")
            throw TranscriptionError.providerNotAvailable(
                provider: "Apple Speech",
                reason: "Failed to prepare speech recognition: \(error.localizedDescription)"
            )
        }
    }

    // TRANSCRIPTION:
    // Transcribes an audio file using SpeechAnalyzer with concurrent analysis and result collection
    func transcribe(audioURL: URL, language: String?, mode: Mode?, vocabulary: [Vocabulary]) async throws -> String {
        // STEP 1: Validate audio file exists and is readable
        let fm = FileManager.default
        guard fm.fileExists(atPath: audioURL.path) else {
            logger.error("SpeechAnalyzer audio file not found")
            throw TranscriptionError.audioFileNotFound
        }

        guard fm.isReadableFile(atPath: audioURL.path) else {
            logger.error("SpeechAnalyzer audio file not readable")
            throw TranscriptionError.providerNotAvailable(
                provider: "Apple Speech",
                reason: "Audio file is not readable"
            )
        }

        // STEP 1.5: Open the audio file, and refuse one with no audio frames.
        // `analyzeSequence` never finishes on a 0-frame file (#1515), so this
        // runs before any asset download or analyzer work.
        let audioFile = try AppleSpeechAudioInput.openForAnalysis(audioURL, logger: logger)

        // STEP 2: Resolve locale from language parameter
        let effectiveLanguage = mode?.language ?? language
        let locale = await resolveLocale(language: effectiveLanguage)
        logger.info("Transcribing with locale: \(locale.identifier, privacy: .public)")

        // STEP 3: Create transcriber and ensure on-device assets are available
        let transcriber = SpeechTranscriber(locale: locale, preset: .transcription)
        try await ensureAssets(for: locale, using: transcriber)

        // STEP 4: Create analyzer and set vocabulary context
        let analyzer = SpeechAnalyzer(modules: [transcriber])

        let contextualWords = vocabulary.compactMap { entry -> String? in
            guard let word = entry.word?.trimmingCharacters(in: .whitespacesAndNewlines),
                  !word.isEmpty else { return nil }
            return word
        }
        if !contextualWords.isEmpty {
            var analysisContext = AnalysisContext()
            analysisContext.contextualStrings[.general] = contextualWords
            try await analyzer.setContext(analysisContext)
            logger.info("Added \(contextualWords.count) contextual strings for recognition")
        }

        // STEP 5: Concurrently feed audio and collect results
        // Tracks the self-inflicted teardown route: when `analyzeSequence` returns
        // no last sample time we cancel the analyzer while `collectTranscriptionResults`
        // is still consuming `transcriber.results`, which terminates that stream with
        // a `CancellationError` even though nothing cancelled the task. Reported as a
        // breadcrumb so Sentry can tell the two routes apart. HYPERWHISPER-SQ.
        // A lock, not a `var`: it is set inside the time-limited operation below.
        let didSelfCancelAnalyzer = OSAllocatedUnfairLock(initialState: false)

        // TIME LIMIT (#1701): 60 s plus the audio's own duration, over the whole
        // step (analysis, finalize, results). An input that never ends the
        // results stream (#1515 was one) would otherwise hang a dictation forever.
        let audioDuration = AppleSpeechTimeLimit.audioDuration(
            frameCount: audioFile.length,
            sampleRate: audioFile.processingFormat.sampleRate
        )
        let limitSeconds = AppleSpeechTimeLimit.limitSeconds(forAudioDuration: audioDuration)
        do {
            let segments = try await AppleSpeechTimeLimit.run(
                limit: .seconds(limitSeconds),
                onTimeout: {
                    // Ends the hung analysis; `run` has already stopped waiting on it.
                    await analyzer.cancelAndFinishNow()
                },
                operation: {
                    // Start analysis and result collection concurrently
                    async let analysisTask: CMTime? = analyzer.analyzeSequence(from: audioFile)
                    async let resultsTask: [String] = AppleSpeechAnalyzerProvider.collectTranscriptionResults(from: transcriber)

                    // Wait for analysis to complete and get last sample time
                    let lastSampleTime = try await analysisTask

                    // Finalize the analysis
                    if let lastSampleTime = lastSampleTime {
                        try await analyzer.finalizeAndFinish(through: lastSampleTime)
                    } else {
                        didSelfCancelAnalyzer.withLock { $0 = true }
                        await analyzer.cancelAndFinishNow()
                    }

                    // Wait for results
                    return try await resultsTask
                }
            )

            // Join all segments into final text
            // No vocabulary replacement here (issue #1622): the pipeline's `\b`
            // pass applies replacement rows once, to whole words, after the
            // raw text is kept. The vocabulary still biases the analyzer above.
            let text = segments.joined(separator: " ")

            let result = text.trimmingCharacters(in: .whitespacesAndNewlines)
            logger.info("Transcription complete: \(result.count) characters")
            return result
        } catch is AppleSpeechTimeLimit.TimedOut {
            // Its own arm, ahead of the catch-all: a timeout is neither a
            // "Transcription failed: …" wrapper nor a cancellation.
            let isTaskCancelled = Task.isCancelled
            logger.error("SpeechAnalyzer transcription timed out; limitSeconds=\(limitSeconds, privacy: .public) audioSeconds=\(audioDuration, privacy: .public) callerCancelled=\(isTaskCancelled, privacy: .public)")

            if AppLogger.isErrorLoggingEnabled {
                SentryService.addBreadcrumb(
                    message: "SpeechAnalyzer transcription timed out",
                    category: "speechanalyzer.transcription",
                    level: .error,
                    data: [
                        // No file name or path: the import flow makes the name
                        // the user's own document name.
                        "locale": locale.identifier,
                        "audioDurationSeconds": audioDuration,
                        "limitSeconds": limitSeconds,
                        "vocabularyCount": vocabulary.count,
                        "callerCancelled": isTaskCancelled
                    ]
                )
            }

            // The caller asked to stop and the analyzer ignored it until the
            // limit: still the caller's cancellation, so no error is shown.
            if isTaskCancelled {
                throw CancellationError()
            }

            throw TranscriptionError.providerNotAvailable(
                provider: "Apple Speech",
                reason: "Transcription timed out"
            )
        } catch {
            // `Task.isCancelled` is task-local: read it once here, at the catch
            // site, and hand the value to the policy — the policy never reads it.
            let isTaskCancelled = Task.isCancelled

            // A cancellation that the caller actually asked for is benign: the
            // pipeline already maps `CancellationError` to `.idle` without a
            // Sentry capture. Re-wrapping it as `.providerNotAvailable` is what
            // defeated that and produced HYPERWHISPER-SQ. Note this is NOT the
            // same as a bare `CancellationError` — see TranscriptionCancellationPolicy.
            if TranscriptionCancellationPolicy.outcome(
                for: error,
                isTaskCancelled: isTaskCancelled
            ) == .genuineCancellation {
                logger.info("SpeechAnalyzer transcription cancelled by the caller")
                throw CancellationError()
            }

            let nsError = error as NSError
            logger.error("SpeechAnalyzer transcription failed; errorDomain=\(nsError.domain, privacy: .public) errorCode=\(nsError.code, privacy: .public)")
            let analyzerSelfCancelled: Bool = didSelfCancelAnalyzer.withLock { $0 }

            if AppLogger.isErrorLoggingEnabled {
                SentryService.addBreadcrumb(
                    message: "SpeechAnalyzer transcription error",
                    category: "speechanalyzer.transcription",
                    level: .error,
                    data: [
                        "errorDomain": nsError.domain,
                        "errorCode": nsError.code,
                        "locale": locale.identifier,
                        // Not the file NAME: the import flow makes it the user's
                        // own document name. The extension is the diagnostic part.
                        "audioFileExtension": audioURL.pathExtension,
                        "vocabularyCount": vocabulary.count,
                        "analyzerSelfCancelled": analyzerSelfCancelled
                    ]
                )
            }

            throw TranscriptionError.providerNotAvailable(
                provider: "Apple Speech",
                reason: "Transcription failed: \(error.localizedDescription)"
            )
        }
    }

    // MARK: - Private Helpers

    // COLLECT TRANSCRIPTION RESULTS:
    // Iterates over the transcriber's async results sequence and collects text segments
    // Static so the time-limited operation in STEP 5 does not capture the provider
    private static func collectTranscriptionResults(from transcriber: SpeechTranscriber) async throws -> [String] {
        var segments: [String] = []
        for try await result in transcriber.results {
            let text = String(result.text.characters)
            if !text.isEmpty {
                segments.append(text)
            }
        }
        return segments
    }

    // RESOLVE LOCALE:
    // Determines the best locale for transcription from the language parameter
    // Falls back through: language param -> supported equivalent -> Locale.current -> en-US
    private func resolveLocale(language: String?) async -> Locale {
        // Try the provided language first
        if let language = language, !language.isEmpty {
            let requestedLocale = Locale(identifier: language)
            if let supported = await SpeechTranscriber.supportedLocale(equivalentTo: requestedLocale) {
                return supported
            }
            logger.warning("Requested locale '\(language, privacy: .public)' not supported, trying fallbacks")
        }

        // Try the system locale
        if let supported = await SpeechTranscriber.supportedLocale(equivalentTo: Locale.current) {
            return supported
        }
        logger.warning("System locale not supported, falling back to en-US")

        // Final fallback to en-US
        return Locale(identifier: "en-US")
    }

    // ENSURE ASSETS:
    // Checks if on-device speech recognition assets are available and downloads them if needed
    private func ensureAssets(for locale: Locale, using transcriber: SpeechTranscriber) async throws {
        let status = await AssetInventory.status(forModules: [transcriber])
        switch status {
        case .installed:
            logger.info("Speech assets available for locale: \(locale.identifier, privacy: .public)")
            return
        case .unsupported:
            logger.error("Locale \(locale.identifier, privacy: .public) is not supported by SpeechTranscriber")
            throw TranscriptionError.modelNotDownloaded
        case .supported, .downloading:
            logger.info("Downloading speech assets for locale: \(locale.identifier, privacy: .public)")
            try await downloadAssets(for: transcriber, locale: locale)
        @unknown default:
            logger.warning("Unknown asset status for locale: \(locale.identifier, privacy: .public)")
            try await downloadAssets(for: transcriber, locale: locale)
        }
    }

    private func downloadAssets(for transcriber: SpeechTranscriber, locale: Locale) async throws {
        do {
            if let request = try await AssetInventory.assetInstallationRequest(supporting: [transcriber]) {
                try await request.downloadAndInstall()
                logger.info("Speech assets downloaded for locale: \(locale.identifier, privacy: .public)")
            } else {
                logger.warning("No installation request available for locale: \(locale.identifier, privacy: .public)")
                throw TranscriptionError.modelNotDownloaded
            }
        } catch let error as TranscriptionError {
            throw error
        } catch {
            let nsError = error as NSError
            logger.error("Failed to download speech assets; errorDomain=\(nsError.domain, privacy: .public) errorCode=\(nsError.code, privacy: .public)")
            throw TranscriptionError.modelNotDownloaded
        }
    }
}
#endif
