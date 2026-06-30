//
//  KeyboardShortcuts+Names.swift
//  hyperwhisper
//
//  Defines strongly-typed names for all keyboard shortcuts used in the app.
//  This allows for compile-time safety and easy refactoring.
//

import Foundation
import KeyboardShortcuts

extension KeyboardShortcuts.Name {
    // MARK: - Recording Shortcuts
    
    /// Toggle recording with transcription
    static let toggleRecordingWithTranscription = Self("toggleRecordingWithTranscription", default: .init(.space, modifiers: [.option]))

    /// Cancel recording
    static let cancelRecording = Self("cancelRecording", default: .init(.escape, modifiers: []))

    /// Push to Talk - hold to record, release to transcribe
    /// DEFAULT: Option+; (semicolon) - not reserved by macOS, distinctive hold gesture
    /// Note: Fn key is macOS system-reserved and documented as deprecated/unusable throughout the codebase
    static let pushToTalk = Self("pushToTalk", default: .init(.semicolon, modifiers: [.option]))

    /// Start streaming transcription - dedicated shortcut for real-time streaming
    /// DEFAULT: Option+Shift+Space - similar to toggle recording but with Shift to distinguish
    /// Uses the language configured in streaming settings (streamingLanguage)
    static let startStreaming = Self("startStreaming", default: .init(.space, modifiers: [.option, .shift]))

    // MARK: - Navigation Shortcuts
    
    /// Open the mode switcher
    static let changeMode = Self("changeMode", default: .init(.k, modifiers: [.control, .shift]))

    // MARK: - Destination Shortcuts

    /// Quick Capture: record from anywhere and send the transcription to Apple Notes
    /// as a new note. No default key combo — user must pick one to avoid collisions
    /// with apps that already use distinctive chords.
    static let quickCapture = Self("quickCapture")

}

// MARK: - CaseIterable Conformance

extension KeyboardShortcuts.Name: CaseIterable {
    public static let allCases: [Self] = [
        .toggleRecordingWithTranscription,
        .cancelRecording,
        .pushToTalk,
        .startStreaming,
        .changeMode,
        .quickCapture
    ]
}
