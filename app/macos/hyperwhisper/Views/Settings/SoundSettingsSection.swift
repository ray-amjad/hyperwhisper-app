//
//  SoundSettingsSection.swift
//  hyperwhisper
//
//  Sound and audio-related settings including microphone control,
//  media handling during recording, and voice activity detection.
//

import SwiftUI

struct SoundSettingsSection: View {
    @EnvironmentObject var settingsManager: SettingsManager

    var body: some View {
        SettingsSection(title: "settings.section.sound") {
            soundEffectsCard

            mediaControlCard

            microphoneCard

            voiceActivityDetectionCard
        }
    }

    // MARK: - Cards

    // SOUND EFFECTS CARD
    // Toggle to enable/disable sound effects (start/stop recording sounds)
    private var soundEffectsCard: some View {
        SettingsCard(horizontalPadding: 8) {
            VStack(spacing: 0) {
                SettingsToggleRow(
                    title: "settings.sound.soundEffects.title",
                    subtitle: nil,
                    info: "settings.sound.soundEffects.info",
                    isOn: $settingsManager.enableSoundEffects,
                    standalone: false
                )
            }
        }
    }

    // MEDIA CONTROL CARD
    // Controls how HyperWhisper handles other audio during recording
    private var mediaControlCard: some View {
        SettingsCard(horizontalPadding: 8) {
            VStack(spacing: 0) {
                // MEDIA CONTROL WHILE RECORDING
                // Dropdown to select how HyperWhisper handles other audio during recording:
                // - Off: No changes to audio
                // - Mute Audio: Mutes system output volume
                // - Pause Media: Pauses media players (Music, Spotify, etc.) and auto-resumes
                SettingsPickerRow(
                    title: "settings.sound.mediaControl.title",
                    subtitle: nil,
                    info: "settings.sound.mediaControl.info",
                    selection: $settingsManager.audio.mediaControlMode,
                    options: MediaControlMode.allCases,
                    optionLabel: { $0.localizedName },
                    standalone: false,
                    pickerWidth: 130
                )

            }
        }
    }

    // MICROPHONE CARD
    // Settings for microphone input handling
    private var microphoneCard: some View {
        SettingsCard(horizontalPadding: 8) {
            VStack(spacing: 0) {
                // AUTO-INCREASE MICROPHONE VOLUME
                // Automatically sets the system microphone input volume to max when recording starts.
                // Only works when using the system default input device (not a specific selected device).
                // Original volume is restored after recording completes.
                SettingsToggleRow(
                    title: "settings.sound.autoIncreaseMicVolume.title",
                    subtitle: nil,
                    info: "settings.sound.autoIncreaseMicVolume.info",
                    isOn: $settingsManager.autoIncreaseMicVolume,
                    standalone: false
                )

                SettingsToggleRow(
                    title: "settings.sound.keepWarmMicrophone.title",
                    subtitle: nil,
                    info: "settings.sound.keepWarmMicrophone.info",
                    isOn: $settingsManager.keepMicrophoneWarm,
                    standalone: false
                )
            }
        }
    }

    // VOICE ACTIVITY DETECTION CARD
    // Controls VAD-based silence trimming before transcription.
    // When enabled, audio is analyzed using Silero VAD and leading/trailing
    // silence is removed before sending to transcription providers.
    //
    // Benefits:
    // - Reduces API costs (less audio to process)
    // - Improves transcription speed
    // - May improve accuracy (less noise for the model)
    private var voiceActivityDetectionCard: some View {
        SettingsCard(horizontalPadding: 8) {
            VStack(spacing: 0) {
                SettingsToggleRow(
                    title: "settings.sound.vad.title",
                    subtitle: nil,
                    info: "settings.sound.vad.info",
                    isOn: $settingsManager.enableVAD,
                    standalone: false
                )
            }
        }
    }
}

#Preview {
    SoundSettingsSection()
        .environmentObject(SettingsManager())
        .frame(width: 600)
        .padding()
}
