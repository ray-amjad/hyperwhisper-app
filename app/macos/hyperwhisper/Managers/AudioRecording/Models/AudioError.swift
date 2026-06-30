//
//  AudioError.swift
//  hyperwhisper
//
//  Created by modularization refactoring
//

import Foundation

// MARK: - Audio Errors

/// Custom errors for audio recording operations
///
/// **Purpose:**
/// Provides detailed error cases for all audio-related failures during recording,
/// with user-friendly localized error messages.
///
/// **Error Cases:**
/// - `noPermission`: Microphone permission not granted
/// - `permissionDenied(reason)`: Permission denied with specific reason
/// - `engineCreationFailed`: Failed to create AVAudioEngine
/// - `noInputNode`: Audio engine has no input node
/// - `fileCreationFailed`: Cannot create audio file for recording
/// - `noTranscriptionPipeline`: Transcription manager not available
/// - `invalidHardwareFormat`: Hardware format invalid (sample rate/channels issue)
/// - `formatCreationFailed`: Cannot create audio format
/// - `converterCreationFailed`: Cannot create AVAudioConverter
/// - `exportFailed`: M4A export failed
/// - `fileNotReadable`: Audio file cannot be read
/// - `recordingTooShort`: Recording was too short to contain audio data
/// - `recordingFailed(reason)`: Recording failed to start with specific reason
/// - `noAudioTrack`: Video file has no audio track to extract
/// - `noMicrophoneAvailable`: No audio input devices were available when recording started
///
/// **Localization:**
/// All error messages use localized strings from Localizable.strings files.
enum AudioError: LocalizedError {
    case noPermission
    case permissionDenied(reason: String)
    case engineCreationFailed
    case noInputNode
    case fileCreationFailed
    case noTranscriptionPipeline
    case invalidHardwareFormat
    case formatCreationFailed
    case converterCreationFailed
    case exportFailed
    case fileNotReadable
    case recordingTooShort
    case recordingFailed(reason: String)
    case noAudioTrack
    case noMicrophoneAvailable

    var errorDescription: String? {
        switch self {
        case .noPermission:
            return "audio.error.microphonePermission".localized
        case .permissionDenied(let reason):
            return reason
        case .engineCreationFailed:
            return "audio.error.createEngine".localized
        case .noInputNode:
            return "audio.error.noInput".localized
        case .fileCreationFailed:
            return "audio.error.createFile".localized
        case .noTranscriptionPipeline:
            return "audio.error.managerUnavailable".localized
        case .invalidHardwareFormat:
            return "audio.error.invalidHardwareFormat".localized
        case .formatCreationFailed:
            return "audio.error.createFormat".localized
        case .converterCreationFailed:
            return "audio.error.createConverter".localized
        case .exportFailed:
            return "audio.error.exportM4A".localized
        case .fileNotReadable:
            return "audio.error.readFile".localized
        case .recordingTooShort:
            return "audio.error.recordingTooShort".localized
        case .recordingFailed(let reason):
            return reason
        case .noAudioTrack:
            return "audio.error.noAudioTrack".localized
        case .noMicrophoneAvailable:
            return "audio.error.noInput".localized
        }
    }
}

// MARK: - File Watcher Errors

/// Custom errors for the file watcher utility
///
/// **Purpose:**
/// Errors that can occur when waiting for audio files to be written to disk
/// after recording stops.
///
/// **Error Cases:**
/// - `failedToOpenFileDescriptor`: Cannot open file for monitoring
/// - `timeout`: File was not written within timeout period
/// - `fileNotCreated`: File does not exist after waiting
///
/// **Usage:**
/// Used by FileWatcher to signal failures when monitoring file creation
/// and write completion for audio files.
enum FileWatcherError: LocalizedError {
    case failedToOpenFileDescriptor
    case timeout
    case fileNotCreated

    var errorDescription: String? {
        switch self {
        case .failedToOpenFileDescriptor:
            return "audio.error.fileWatcher.open".localized
        case .timeout:
            return "audio.error.fileWatcher.timeout".localized
        case .fileNotCreated:
            return "audio.error.fileWatcher.creation".localized
        }
    }
}
