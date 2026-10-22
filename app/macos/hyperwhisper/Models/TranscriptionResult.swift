//
//  TranscriptionResult.swift
//  hyperwhisper
//
//  Extracted for modularity and reuse.

import Foundation

/// Result of a transcription
struct TranscriptionResult: Identifiable {
    let id = UUID()
    let text: String  // Final text (post-processed or raw)
    let rawText: String  // Original transcribed text before post-processing
    let timestamp: Date
    let duration: TimeInterval
    let mode: Mode?
    let provider: String  // Transcription provider (local/cloud)
    let wasPostProcessed: Bool  // Whether AI post-processing was applied
    let postProcessingProvider: String?  // Provider used for post-processing (openai/anthropic/gemini)
    /// True when client-side post-processing was attempted but produced no mutated text
    /// (e.g. local LLM runtime failed and raw transcript was returned).
    let postProcessingSkipped: Bool
    /// Segment/word timestamps from the transcription engine, or nil when none
    /// were produced (engine can't, or not requested). The timings align to
    /// `rawText` (the uncleaned transcript), NEVER to the post-processed `text`.
    let timestamps: TranscriptionTimestamps?

    init(
        text: String,
        rawText: String,
        timestamp: Date,
        duration: TimeInterval,
        mode: Mode?,
        provider: String,
        wasPostProcessed: Bool,
        postProcessingProvider: String?,
        postProcessingSkipped: Bool = false,
        timestamps: TranscriptionTimestamps? = nil
    ) {
        self.text = text
        self.rawText = rawText
        self.timestamp = timestamp
        self.duration = duration
        self.mode = mode
        self.provider = provider
        self.wasPostProcessed = wasPostProcessed
        self.postProcessingProvider = postProcessingProvider
        self.postProcessingSkipped = postProcessingSkipped
        self.timestamps = timestamps
    }

    /// The `postProcessedText` a History row saves: the delivered `text`
    /// after an AI post-processing run, or whenever it is not the engine's
    /// `rawText`. Nil only when AI did not run and the two match.
    ///
    /// Not gated on `wasPostProcessed` (issue #1638): with AI post-processing
    /// off, the pipeline still removes filler words, applies dictated "new line"
    /// commands and Vocabulary swaps, so the pasted text differs from the raw
    /// text. Gating on the AI flag saved only the raw text, and History and
    /// `GET /recordings/{id}` showed text the user never received.
    var historyPostProcessedText: String? {
        wasPostProcessed || text != rawText ? text : nil
    }

    /// True when the engine heard nothing: `rawText` is empty or only whitespace.
    var isNoSpeech: Bool {
        rawText.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }

    /// Throws `.noSpeechDetected` for a result with no speech, before a caller
    /// saves it to a History row (issue #1429).
    ///
    /// Some engines return `""` for silence instead of throwing (Apple Speech,
    /// the Nemotron/Parakeet family), and `updateTranscriptWithTranscription`
    /// marks any text `completed`, so a silent file gave a finished row with no
    /// text and no notice. File import and History Retry both call this, so the
    /// row is marked failed with the "no speech" reason instead.
    func throwIfNoSpeech() throws {
        if isNoSpeech {
            throw TranscriptionError.noSpeechDetected
        }
    }
}

