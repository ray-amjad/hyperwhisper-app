//
//  BackupManager+Settings.swift
//  hyperwhisper
//
//  Settings: writing a backup's settings section to the live app, and the
//  per-mode default model map.
//  Split out of BackupManager.swift (#732); same class, same behaviour.
//

import Foundation
import SwiftUI
import AppKit

extension BackupManager {

    // MARK: - Private Helpers

    /// Applies settings from backup to the app. Reached through
    /// `settingsStore` (see `LiveBackupImportSettingsStore`).
    /// - Parameter settings: BackupSettings to apply
    static func applySettings(_ settings: BackupSettings) async {
        let settingsManager = SettingsManager.shared

        // General settings
        // Registering the login item blocks on XPC, so it runs off the main thread (#853).
        await settingsManager.setLaunchAtLogin(settings.general.launchAtLogin)
        settingsManager.showInDock = settings.general.showInDock
        settingsManager.launchMinimized = settings.general.launchMinimized
        settingsManager.showRecordingWindow = settings.general.showRecordingWindow
        settingsManager.checkForUpdatesAutomatically = settings.general.checkForUpdatesAutomatically
        settingsManager.enableErrorLogging = settings.general.enableErrorLogging
        // A backup from before this setting existed says nothing about it, so
        // leave the current preference alone rather than resetting it.
        if let shareAnonymousSpeedData = settings.general.shareAnonymousSpeedData {
            settingsManager.shareAnonymousSpeedData = shareAnonymousSpeedData
        }

        // Audio settings
        settingsManager.autoIncreaseMicVolume = settings.audio.autoIncreaseMicVolume
        if settings.audio.mediaControlMode == "pauseMedia" {
            // Removed mode — fall back to .off (matches startup migration)
            settingsManager.audio.mediaControlMode = .off
        } else if let mode = MediaControlMode(rawValue: settings.audio.mediaControlMode) {
            settingsManager.audio.mediaControlMode = mode
        }
        settingsManager.enableSoundEffects = settings.audio.enableSoundEffects
        if let theme = SoundTheme(rawValue: settings.audio.soundTheme) {
            settingsManager.soundTheme = theme
        }
        settingsManager.soundEffectsVolume = settings.audio.soundEffectsVolume

        // Storage settings
        settingsManager.filesyncEnabled = settings.storage.filesyncEnabled
        settingsManager.storeAsM4A = settings.storage.storeAsM4A

        // Text output settings
        settingsManager.pasteResultText = settings.textOutput.pasteResultText
        settingsManager.removeFillerWords = settings.textOutput.removeFillerWords
        settingsManager.restoreClipboardAfterPaste = settings.textOutput.restoreClipboardAfterPaste
        settingsManager.hideFromClipboardHistory = settings.textOutput.hideFromClipboardHistory
        settingsManager.clipboardRestoreDelaySeconds = settings.textOutput.clipboardRestoreDelaySeconds
        // Optional in backup payload — legacy (pre-#643) backups omit this field.
        // Only apply when present so restoring an old backup doesn't silently flip
        // a user's disabled Autocapitalize Insert back on.
        if let autocapitalizeInsert = settings.textOutput.autocapitalizeInsert {
            settingsManager.autocapitalizeInsert = autocapitalizeInsert
        }
        settingsManager.storeWordTimestamps = settings.textOutput.storeWordTimestamps

        // Shortcut settings
        if let mode = PushToTalkMode(rawValue: settings.shortcuts.pushToTalkMode) {
            settingsManager.pushToTalkMode = mode
        }
        settingsManager.pushToTalkDoublePressEnabled = settings.shortcuts.pushToTalkDoublePressEnabled
        // Quick Capture (optional in backup payload — pre-feature backups omit these).
        if let qcEnabled = settings.shortcuts.quickCaptureEnabled {
            settingsManager.quickCaptureEnabled = qcEnabled
        }
        if let qcModeId = settings.shortcuts.quickCaptureModeId {
            settingsManager.quickCaptureModeId = qcModeId
        }
        // Key combos (#1481). A backup without the map leaves every shortcut
        // alone; see BackupKeyboardShortcuts for the per-entry rules.
        BackupKeyboardShortcuts.applyLive(settings.shortcuts.keyboardShortcuts)
        // Programmatic writes above bypass the settings UI's .onChange posters,
        // so re-sync shortcut consumers (PTT observer, feature-gated hotkey
        // registration) explicitly.
        NotificationCenter.default.post(name: .shortcutDidChange, object: nil)

        // AI model settings
        settingsManager.showExperimentalModels = settings.aiModel.showExperimentalModels
        settingsManager.defaultTranscriptionModel = settings.aiModel.defaultTranscriptionModel
        settingsManager.defaultLanguage = settings.aiModel.defaultLanguage

        // Advanced settings
        // Legacy backups carry 300 in advanced.maxRecordingDuration — the old
        // never-exposed default, not a user choice. Treat it as unset so the
        // import doesn't silently cap recordings at 5 minutes.
        if settings.advanced.maxRecordingDuration != 300 {
            settingsManager.maxRecordingDurationSeconds = settings.advanced.maxRecordingDuration
        }
        settingsManager.audioSampleRate = settings.advanced.audioSampleRate
        settingsManager.keepAudioFiles = settings.advanced.keepAudioFiles
        settingsManager.historyRetentionDays = settings.advanced.historyRetentionDays

    }

    func applyDefaultModelByMode(_ importedMap: [String: String], idRemap: [UUID: UUID]) {
        let resolvedImportedMap = Self.remapDefaultModelByMode(
            importedMap.mapValues { CloudTranscriptionModels.resolveModelAlias($0, provider: nil) },
            using: idRemap
        )

        settingsStore.defaultModelByMode = Self.mergeDefaultModelByMode(
            current: settingsStore.defaultModelByMode,
            imported: resolvedImportedMap
        )
    }

    nonisolated static func mergeDefaultModelByMode(current: [String: String], imported: [String: String]) -> [String: String] {
        current.merging(imported) { _, imported in imported }
    }

    nonisolated static func remapDefaultModelByMode(_ map: [String: String], using idRemap: [UUID: UUID]) -> [String: String] {
        var remapped = map

        for (oldId, newId) in idRemap {
            if let model = remapped.removeValue(forKey: oldId.uuidString) {
                remapped[newId.uuidString] = model
            }
        }

        return remapped
    }

    /// Converts empty strings to nil
    func emptyToNil(_ string: String?) -> String? {
        guard let string = string, !string.isEmpty else { return nil }
        return string
    }
}
