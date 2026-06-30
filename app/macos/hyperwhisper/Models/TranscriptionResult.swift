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
}

