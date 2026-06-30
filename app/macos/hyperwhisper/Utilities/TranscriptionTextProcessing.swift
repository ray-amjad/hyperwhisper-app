//
//  TranscriptionTextProcessing.swift
//  hyperwhisper
//
//  Facade over the shared Rust core (`hw-text`) for tag cleanup, streaming
//  sanitization, filler-word removal and voice commands. The logic lives in
//  Rust so macOS and Windows stay in lockstep; these are thin delegating shims.
//
//  The global UniFFI binding functions share base names with these facade
//  methods, so they're qualified with the module name (`HyperWhisper.`) to defeat
//  member-shadowing (an unqualified call would resolve back to the member).
//

import Foundation

enum TranscriptionTextProcessing {

    /// Extract content between `<<CLEANED>>` and `<<END>>` markers from a
    /// post-processing response. STRICT: returns "" when no start marker is
    /// present (the model didn't honour the wrapping contract) — callers fall
    /// back to the original transcription. Use `stripWrapperMarkers` for plain
    /// transcription text that may not be wrapped.
    static func extractCleanedFromWrapped(_ text: String) -> String {
        HyperWhisper.extractCleanedFromWrapped(text: text)
    }

    /// Lenient wrapper handling for plain transcription text: extract wrapped
    /// content if present, otherwise return the text (stray markers stripped).
    static func stripWrapperMarkers(_ text: String) -> String {
        HyperWhisper.stripWrapperMarkers(text: text)
    }

    /// For streaming display: drop everything before `<<CLEANED>>`, remove tag variants.
    static func sanitizeStreamingBuffer(_ buffer: String) -> String {
        HyperWhisper.sanitizeStreamingBuffer(buffer: buffer)
    }

    /// Remove a single trailing period (preserves an ellipsis "...").
    static func removeTrailingPeriod(_ text: String) -> String {
        HyperWhisper.removeTrailingPeriod(text: text)
    }

    /// Remove English filler words ("uh", "um", "er"). No-op for non-English /
    /// unknown languages (those are real words elsewhere).
    static func removeFillerWords(_ text: String, language: String?) -> String {
        HyperWhisper.removeFillerWords(text: text, language: language)
    }

    /// Replace the spoken "new line" command with a paragraph break.
    static func processVoiceCommands(_ text: String) -> String {
        HyperWhisper.processVoiceCommands(text: text)
    }
}
