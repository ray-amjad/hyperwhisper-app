//
//  AudioSettingsManager.swift
//  hyperwhisper
//
//  AUDIO SETTINGS MANAGER
//  Manages all audio-related settings including microphone selection,
//  volume control, and sound effects preferences.
//
//  RESPONSIBILITIES:
//  - Microphone device selection
//  - Automatic volume adjustment
//  - Sound effects configuration
//  - Sound theme management
//
//  ARCHITECTURE:
//  - @AppStorage for automatic UserDefaults persistence
//  - Observable for reactive UI updates
//  - Validation of audio parameter ranges
//

import Foundation
import SwiftUI

/// Manages audio-related application settings
@MainActor
class AudioSettingsManager: ObservableObject {

    // MARK: - Microphone Settings

    /// Selected microphone device ID
    /// Empty string means use system default microphone
    /// Device IDs are persistent identifiers from AVCaptureDevice
    @AppStorage("selectedMicrophoneId") var selectedMicrophoneId: String = ""

    /// Whether to automatically increase microphone volume
    /// When enabled, attempts to boost low input levels during recording
    @AppStorage("autoIncreaseMicVolume") var autoIncreaseMicVolume: Bool = true

    /// Whether HyperWhisper keeps an idle microphone session running between recordings.
    /// This reduces push-to-talk startup delay, especially on Bluetooth devices.
    @AppStorage("keepMicrophoneWarm") var keepMicrophoneWarm: Bool = false

    /// MEDIA CONTROL MODE SETTING
    /// Controls how HyperWhisper handles other audio sources during recording.
    ///
    /// OPTIONS:
    /// - .off: No changes to audio (default)
    /// - .muteAudio: Mutes system output volume during recording, restores after
    ///
    /// Persisted to UserDefaults via @AppStorage. Two migrations run in init():
    /// 1. Old boolean "pauseOtherAudioDuringRecording" → .muteAudio
    /// 2. Removed "pauseMedia" raw value → .off (v2.32)
    @AppStorage("mediaControlMode") var mediaControlMode: MediaControlMode = .off

    // MARK: - Sound Effects Settings

    /// Whether to enable sound effects
    /// When disabled, all audio feedback is muted
    @AppStorage("enableSoundEffects") var enableSoundEffects: Bool = true

    /// Sound theme (classic or new)
    /// Determines which sound effect set to use for UI feedback
    @AppStorage("soundTheme") var soundTheme: SoundTheme = .classic

    /// Sound effects volume (0.0 to 1.0)
    /// Master volume for all sound effects
    /// Validated on set to ensure valid range
    @AppStorage("soundEffectsVolume") var soundEffectsVolume: Double = 1.0 {
        didSet {
            // Validate volume range
            if soundEffectsVolume < 0 || soundEffectsVolume > 1 {
                soundEffectsVolume = 1.0
            }
        }
    }

    // MARK: - Initialization

    init() {
        // MIGRATION: Handle upgrade from old boolean setting to new enum
        // Users who had "pauseOtherAudioDuringRecording" enabled are migrated to .muteAudio
        // This preserves their preference to have audio controlled during recording
        migrateFromOldSetting()

        // MIGRATION: Handle removal of "pauseMedia" mode
        // @AppStorage falls back to default (.off) when raw value doesn't match,
        // so we check UserDefaults directly for the removed value
        migratePauseMediaMode()

        // Validate settings on initialization
        validateSettings()
    }

    // MARK: - Private Methods

    /// MIGRATION: Convert old boolean "pauseOtherAudioDuringRecording" to new enum
    ///
    /// MIGRATION FLOW:
    /// 1. Check if the old key exists in UserDefaults
    /// 2. If it was enabled (true), set new mode to .muteAudio (preserves behavior)
    /// 3. Remove the old key to prevent re-migration
    ///
    /// This ensures existing users who had the mute feature enabled continue
    /// to have their audio muted during recording without manual re-configuration.
    private func migrateFromOldSetting() {
        let oldKey = "pauseOtherAudioDuringRecording"

        // Only migrate if the old key exists (user has used the app before)
        if UserDefaults.standard.object(forKey: oldKey) != nil {
            let wasEnabled = UserDefaults.standard.bool(forKey: oldKey)

            if wasEnabled {
                // User had mute enabled - migrate to .muteAudio mode
                // This preserves their previous behavior
                mediaControlMode = .muteAudio
                AppLogger.audio.info("Migrated pauseOtherAudioDuringRecording=true to mediaControlMode=.muteAudio")
            }

            // Remove old key regardless of value to prevent re-migration
            UserDefaults.standard.removeObject(forKey: oldKey)
            AppLogger.audio.info("Removed deprecated pauseOtherAudioDuringRecording setting")
        }
    }

    /// MIGRATION (v2.32): Convert removed "pauseMedia" mode to "off"
    /// The pauseMedia feature was removed because it caused issues with media apps
    /// unexpectedly resuming and interfering with playback state.
    private func migratePauseMediaMode() {
        let key = "mediaControlMode"
        if let stored = UserDefaults.standard.string(forKey: key), stored == "pauseMedia" {
            mediaControlMode = .off
            AppLogger.audio.info("Migrated removed pauseMedia mode to off")
        }
    }

    /// Validate all audio settings
    /// Ensures all values are within acceptable ranges
    /// Called on initialization and when settings are loaded
    private func validateSettings() {
        // Validate volume
        if soundEffectsVolume < 0 || soundEffectsVolume > 1 {
            soundEffectsVolume = 1.0
        }
    }
}

// MARK: - Supporting Types

/// Sound theme options
enum SoundTheme: String, CaseIterable {
    case classic = "Classic"
    case new = "New"

    var description: String {
        switch self {
        case .classic:
            return "Traditional system sounds"
        case .new:
            return "Modern, subtle sounds"
        }
    }
}

/// MEDIA CONTROL MODE
/// Determines how HyperWhisper handles other audio sources while recording.
///
/// BEHAVIOR:
/// - .off: No audio changes during recording (default)
/// - .muteAudio: Mutes system output volume, restores after recording
///
/// MIGRATION:
/// - v2.30: Users with the old boolean "pauseOtherAudioDuringRecording" setting
///   are automatically migrated to .muteAudio mode.
/// - v2.32: "pauseMedia" mode removed (caused issues with media apps unexpectedly
///   resuming, interfering with playback state). Users are migrated to .off.
enum MediaControlMode: String, CaseIterable, Codable {
    case off
    case muteAudio

    /// Localized display name for use in the settings UI
    /// Returns translated string based on current app locale
    var localizedName: String {
        switch self {
        case .off:
            return NSLocalizedString("settings.general.mediaControl.mode.off", value: "Off", comment: "Media control mode: disabled")
        case .muteAudio:
            return NSLocalizedString("settings.general.mediaControl.mode.mute", value: "Mute Audio", comment: "Media control mode: mute system audio")
        }
    }

    /// Custom decoding to handle migration from removed "pauseMedia" case
    init(from decoder: Decoder) throws {
        let container = try decoder.singleValueContainer()
        let rawValue = try container.decode(String.self)
        if rawValue == "pauseMedia" {
            self = .off
        } else {
            self = MediaControlMode(rawValue: rawValue) ?? .off
        }
    }
}
