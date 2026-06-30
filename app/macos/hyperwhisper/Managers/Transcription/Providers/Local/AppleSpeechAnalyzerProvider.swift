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

    // LOCALE SUPPORT CHECK:
    // Returns true if the given locale identifier is supported by SpeechTranscriber
    func isLocaleSupported(_ localeIdentifier: String) async -> Bool {
        let locale = Locale(identifier: localeIdentifier)
        if let _ = await SpeechTranscriber.supportedLocale(equivalentTo: locale) {
            return true
        }
        logger.info("Locale not supported: \(localeIdentifier, privacy: .public)")
        return false
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
            logger.error("Failed to preheat SpeechAnalyzer: \(error.localizedDescription, privacy: .public)")
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
            logger.error("Audio file not found: \(audioURL.lastPathComponent, privacy: .public)")
            throw TranscriptionError.audioFileNotFound
        }

        guard fm.isReadableFile(atPath: audioURL.path) else {
            logger.error("Audio file not readable: \(audioURL.lastPathComponent, privacy: .public)")
            throw TranscriptionError.providerNotAvailable(
                provider: "Apple Speech",
                reason: "Audio file is not readable"
            )
        }

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

        // STEP 5: Open audio file
        let audioFile: AVAudioFile
        do {
            audioFile = try AVAudioFile(forReading: audioURL)
        } catch {
            logger.error("Failed to open audio file: \(error.localizedDescription, privacy: .public)")
            throw TranscriptionError.invalidAudioFormat
        }

        // STEP 6: Concurrently feed audio and collect results
        do {
            // Start analysis and result collection concurrently
            async let analysisTask: CMTime? = analyzer.analyzeSequence(from: audioFile)
            async let resultsTask: [String] = collectTranscriptionResults(from: transcriber)

            // Wait for analysis to complete and get last sample time
            let lastSampleTime = try await analysisTask

            // Finalize the analysis
            if let lastSampleTime = lastSampleTime {
                try await analyzer.finalizeAndFinish(through: lastSampleTime)
            } else {
                await analyzer.cancelAndFinishNow()
            }

            // Wait for results
            let segments = try await resultsTask

            // Join all segments into final text
            var text = segments.joined(separator: " ")

            // Apply vocabulary replacements post-transcription
            if !vocabulary.isEmpty {
                text = applyVocabulary(text, vocabulary: vocabulary)
            }

            let result = text.trimmingCharacters(in: .whitespacesAndNewlines)
            logger.info("Transcription complete: \(result.count) characters")
            return result
        } catch {
            logger.error("SpeechAnalyzer transcription failed: \(String(describing: type(of: error))) - \(error.localizedDescription, privacy: .public)")

            SentryService.addBreadcrumb(
                message: "SpeechAnalyzer transcription error",
                category: "speechanalyzer.transcription",
                level: .error,
                data: [
                    "errorType": String(describing: type(of: error)),
                    "errorDescription": error.localizedDescription,
                    "locale": locale.identifier,
                    "audioFile": audioURL.lastPathComponent,
                    "vocabularyCount": vocabulary.count
                ]
            )

            throw TranscriptionError.providerNotAvailable(
                provider: "Apple Speech",
                reason: "Transcription failed: \(error.localizedDescription)"
            )
        }
    }

    // MARK: - Private Helpers

    // COLLECT TRANSCRIPTION RESULTS:
    // Iterates over the transcriber's async results sequence and collects text segments
    private func collectTranscriptionResults(from transcriber: SpeechTranscriber) async throws -> [String] {
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
            logger.error("Failed to download speech assets: \(error.localizedDescription, privacy: .public)")
            throw TranscriptionError.modelNotDownloaded
        }
    }

    // VOCABULARY POST-PROCESSING:
    // Applies custom vocabulary replacements to the transcribed text
    // Case-insensitive and diacritic-insensitive matching
    private func applyVocabulary(_ text: String, vocabulary: [Vocabulary]) -> String {
        var updated = text
        for entry in vocabulary {
            guard let word = entry.word?.trimmingCharacters(in: .whitespacesAndNewlines), !word.isEmpty else {
                continue
            }
            guard let replacement = entry.replacement?.trimmingCharacters(in: .whitespacesAndNewlines), !replacement.isEmpty else {
                continue
            }
            updated = updated.replacingOccurrences(of: word, with: replacement, options: [.caseInsensitive, .diacriticInsensitive], range: nil)
        }
        return updated
    }
}
#endif
