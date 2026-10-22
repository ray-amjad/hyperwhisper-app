//
//  TranscriptionProvider.swift
//  hyperwhisper
//
//  Protocol definition for transcription providers
//

import Foundation
import CoreData

/// Metadata-only facts from the final response handled by a transcription provider.
struct TranscriptionAttemptDiagnostics: Sendable, Equatable {
    let attemptSource: String
    let providerDisplayName: String
    let backendRequestId: String?
    let backendSTTProvider: String?
    let backendSTTModel: String?
    let backendNoSpeechDetected: Bool?
    let httpStatusCode: Int?
    let responseLatencyMs: Int?
    var providerAttemptMs: Int?

    static func failureSnapshot(
        providerDiagnostics: TranscriptionAttemptDiagnostics?,
        providerDisplayName: String,
        providerAttemptMs: Int
    ) -> TranscriptionAttemptDiagnostics {
        var diagnostics = providerDiagnostics ?? TranscriptionAttemptDiagnostics(
            attemptSource: "provider_uninstrumented",
            providerDisplayName: providerDisplayName,
            backendRequestId: nil,
            backendSTTProvider: nil,
            backendSTTModel: nil,
            backendNoSpeechDetected: nil,
            httpStatusCode: nil,
            responseLatencyMs: nil,
            providerAttemptMs: nil
        )
        diagnostics.providerAttemptMs = providerAttemptMs
        return diagnostics
    }
}

/// Everything one `transcribeDetailed(...)` call produced, returned to the
/// caller that made it (issue #754).
///
/// The router caches one instance of each provider, and the GUI pipeline and
/// the Local API `POST /transcribe` share it, so per-call results parked on the
/// instance and read after the `await` could belong to an overlapping call.
/// They travel back with the call instead.
struct TranscriptionOutcome: Sendable {
    /// The engine's text: the raw transcript, before any vocabulary pass.
    let text: String
    /// The language the provider detected for THIS call (BCP-47-ish, e.g.
    /// "en", "de"), or nil when the provider does not surface one.
    let detectedLanguage: String?
    /// Timestamps for THIS call, or nil when none were requested or the
    /// engine cannot produce them.
    let timestamps: TranscriptionTimestamps?
    /// Metadata from the final response this call handled.
    let diagnostics: TranscriptionAttemptDiagnostics?
    /// HyperWhisper Cloud's server-side post-processed text for THIS call, or
    /// nil when the server did not correct it. Every other provider: nil.
    let serverCorrectedText: String?

    init(
        text: String,
        detectedLanguage: String? = nil,
        timestamps: TranscriptionTimestamps? = nil,
        diagnostics: TranscriptionAttemptDiagnostics? = nil,
        serverCorrectedText: String? = nil
    ) {
        self.text = text
        self.detectedLanguage = detectedLanguage
        self.timestamps = timestamps
        self.diagnostics = diagnostics
        self.serverCorrectedText = serverCorrectedText
    }
}

/// A failed `transcribeDetailed(...)` call that still has attempt diagnostics
/// to report: HyperWhisper Cloud answers a no-speech request with a full
/// response, and its metadata is what the no-speech diagnostic sends.
///
/// A returned `TranscriptionOutcome` cannot carry anything on a throw, so the
/// diagnostics ride on the error. Only `transcribeDetailed` throws this, and
/// only when it has diagnostics. Every caller unwraps it AT THE CALL SITE and
/// rethrows `underlying` unchanged, so nothing that switches on
/// `TranscriptionError` (cancellation, licence repair, health feedback, the
/// Local API error table) ever sees the wrapper. The plain `transcribe(...)`
/// never throws it.
struct TranscriptionAttemptFailure: Error {
    /// The error the provider would have thrown without the wrapper.
    let underlying: Error
    /// Metadata from the final response the failed attempt handled.
    let diagnostics: TranscriptionAttemptDiagnostics?
}

/// Protocol that all transcription providers must implement
/// This allows us to easily switch between local and cloud providers
protocol TranscriptionProvider {
    /// Transcribe audio file to text with optional vocabulary
    func transcribe(audioURL: URL, language: String?, mode: Mode?, vocabulary: [Vocabulary]) async throws -> String

    /// Transcribe, and return everything THIS call produced (issue #754).
    ///
    /// A protocol REQUIREMENT, not only an extension method, so a provider's
    /// own implementation is what a call through `any TranscriptionProvider`
    /// reaches. The default below wraps `transcribe(...)` and reports no
    /// metadata; `HyperWhisperCloudProvider` and `LibWhisperProvider` override it.
    ///
    /// - Throws: whatever `transcribe(...)` throws, or a
    ///   `TranscriptionAttemptFailure` wrapping it when the attempt has
    ///   diagnostics. Callers unwrap that wrapper where they make the call.
    func transcribeDetailed(audioURL: URL, language: String?, mode: Mode?, vocabulary: [Vocabulary]) async throws -> TranscriptionOutcome

    /// Check if the provider is available
    var isAvailable: Bool { get }

    /// Provider name for display
    var name: String { get }

    /// Request that the NEXT `transcribe(...)` call also produce timestamps at
    /// the given granularities. Providers that can't produce timestamps ignore
    /// this. Default no-op so the other 17 providers need no change.
    func setTimestampGranularities(_ granularities: TimestampGranularities)

    /// Whether this one instance can run a dictation and a file job at the
    /// same time (#1425). `false` makes the pipeline run them one after the
    /// other on it (`TranscriptionEngineAccess`). Default `false`: a provider
    /// opts in only when it keeps no per-call state on the instance and
    /// shares no engine that one pass at a time can use.
    var supportsConcurrentPasses: Bool { get }
}

extension TranscriptionProvider {
    /// Providers that surface no per-call metadata inherit this: the text, and
    /// nil for everything else. No existing conformer needs to change.
    func transcribeDetailed(audioURL: URL, language: String?, mode: Mode?, vocabulary: [Vocabulary]) async throws -> TranscriptionOutcome {
        let text = try await transcribe(audioURL: audioURL, language: language, mode: mode, vocabulary: vocabulary)
        return TranscriptionOutcome(text: text)
    }

    /// Providers that don't support timestamps inherit a no-op.
    func setTimestampGranularities(_ granularities: TimestampGranularities) {}
    var supportsConcurrentPasses: Bool { false }
}

/// A local engine that has its own vocabulary correction to run over its raw
/// output: the Parakeet family's phonetic (Beider-Morse) pass, issue #283.
///
/// The engine does NOT run it inside its own `transcribe(...)`. The caller
/// (`TranscriptionPipeline.transcribeWithDetails`, and the Local API's
/// `TranscribeEndpoint`) runs it once, through
/// `VocabularyProcessor.applyLocalEngineVocabularyCorrection`, right after it
/// has kept the engine's text as the raw transcript and before the `\b`-anchored
/// `applyVocabularyReplacements` pass. So the History row's raw transcript is
/// the engine's own text, as it is for Whisper, and each vocabulary pass runs
/// exactly once per transcription (issue #1622, the macOS side of Windows #1596).
protocol LocalVocabularyCorrecting {
    /// Returns `rawText` with the engine's own vocabulary correction applied.
    /// Must never write a vocabulary row's REPLACEMENT value: that is the `\b`
    /// pass's job, and doing it here too would apply a swap twice.
    func applyLocalVocabularyCorrection(to rawText: String, vocabulary: [Vocabulary]) -> String
}
