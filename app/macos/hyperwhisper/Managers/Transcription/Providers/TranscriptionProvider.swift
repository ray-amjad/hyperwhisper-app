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

/// Protocol that all transcription providers must implement
/// This allows us to easily switch between local and cloud providers
protocol TranscriptionProvider {
    /// Transcribe audio file to text with optional vocabulary
    func transcribe(audioURL: URL, language: String?, mode: Mode?, vocabulary: [Vocabulary]) async throws -> String
    
    /// Check if the provider is available
    var isAvailable: Bool { get }

    /// Provider name for display
    var name: String { get }

    /// The language the provider detected for the most recent `transcribe(...)`
    /// call (BCP-47-ish, e.g. "en", "de"), or nil when the provider does not
    /// surface a detected language. Read immediately after the awaited
    /// `transcribe(...)` returns; transcriptions are serialized per session so
    /// the read is ordered with respect to the call that produced it.
    var detectedLanguage: String? { get }

    /// Request that the NEXT `transcribe(...)` call also produce timestamps at
    /// the given granularities. Providers that can't produce timestamps ignore
    /// this. Default no-op so the other 17 providers need no change.
    func setTimestampGranularities(_ granularities: TimestampGranularities)

    /// Timestamps produced by the most recent `transcribe(...)` call, or nil
    /// when none were requested / the engine can't produce them. Read with the
    /// same ordering guarantee as `detectedLanguage`. Default nil.
    var lastTimestamps: TranscriptionTimestamps? { get }

    /// Metadata from the final response handled by the most recent attempt.
    var lastAttemptDiagnostics: TranscriptionAttemptDiagnostics? { get }
}

extension TranscriptionProvider {
    /// Providers that don't surface a detected language inherit nil, so no
    /// existing conformer needs to change.
    var detectedLanguage: String? { nil }

    /// Providers that don't support timestamps inherit a no-op + nil.
    func setTimestampGranularities(_ granularities: TimestampGranularities) {}
    var lastTimestamps: TranscriptionTimestamps? { nil }
    var lastAttemptDiagnostics: TranscriptionAttemptDiagnostics? { nil }
}
