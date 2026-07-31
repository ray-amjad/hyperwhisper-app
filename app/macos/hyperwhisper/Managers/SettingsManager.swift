//
//  SettingsManager.swift
//  hyperwhisper
//
//  Created by Rehman Amjad on 16/08/2025.
//
//  SETTINGS MANAGER
//  Centralized management of all app settings and preferences.
//
//  REFACTORED ARCHITECTURE:
//  This class now delegates to specialized sub-managers for better organization:
//  - GeneralSettingsManager: Launch, dock, updates, error logging
//  - AudioSettingsManager: Microphone, sound effects
//  - StorageSettingsManager: Folders, permissions, fallbacks
//  - APIKeySettingsManager: API keys, keychain, validation
//
//  Key Responsibilities:
//  - Coordinating specialized settings managers
//  - Providing unified interface for settings access
//  - Maintaining backward compatibility with existing code
//
//  Design Pattern:
//  - Composition over a monolithic manager
//  - Delegation to specialized managers
//  - Computed properties forward to sub-managers
//

import Foundation
import SwiftUI
import Combine
import ServiceManagement
import AppKit

// MARK: - Push to Talk Mode Enum

/// Represents the different modes for Push to Talk functionality
///
/// Cases:
/// - disabled: Push to Talk is off
/// - fn: Hold FN key to record (bare modifier, requires Accessibility permission)
/// - control: Hold Control key to record (bare modifier, requires Accessibility permission)
/// - leftOption: Hold Left Option key to record (bare modifier, requires Accessibility permission)
/// - rightOption: Hold Right Option key to record (bare modifier, requires Accessibility permission)
/// - custom: Use custom keyboard shortcut via KeyboardShortcuts library
enum PushToTalkMode: String, CaseIterable, Codable {
    case disabled
    case fn
    case control
    case leftOption
    case rightOption
    case fnControl
    case fnOption
    case custom

    var localizedName: String {
        switch self {
        case .disabled:
            return NSLocalizedString("settings.shortcuts.pushToTalk.mode.disabled", value: "Disabled", comment: "")
        case .fn:
            return NSLocalizedString("settings.shortcuts.pushToTalk.mode.fn", value: "FN Key", comment: "")
        case .control:
            return NSLocalizedString("settings.shortcuts.pushToTalk.mode.control", value: "Control Key", comment: "")
        case .leftOption:
            return NSLocalizedString("settings.shortcuts.pushToTalk.mode.leftOption", value: "Left Option Key", comment: "")
        case .rightOption:
            return NSLocalizedString("settings.shortcuts.pushToTalk.mode.rightOption", value: "Right Option Key", comment: "")
        case .fnControl:
            return NSLocalizedString("settings.shortcuts.pushToTalk.mode.fnControl", value: "FN + Control", comment: "")
        case .fnOption:
            return NSLocalizedString("settings.shortcuts.pushToTalk.mode.fnOption", value: "FN + Left Option", comment: "")
        case .custom:
            return NSLocalizedString("settings.shortcuts.pushToTalk.mode.custom", value: "Custom Shortcut", comment: "")
        }
    }
}

// MARK: - Settings Manager

/// Central manager for all app settings and preferences
/// Delegates to specialized managers for organization and maintainability
@MainActor
class SettingsManager: ObservableObject {

    // MARK: - Singleton

    /// Shared singleton instance. The app's root `@StateObject` and non-View
    /// code (e.g. `BackupManager`) must both use this instance — a second
    /// instance would keep its own in-memory `defaultModelByMode` copy and
    /// silently overwrite imported values on the next per-mode model edit.
    static let shared = SettingsManager()

    // MARK: - Sub-Managers

    /// General app behavior settings (launch, dock, updates)
    @Published var general = GeneralSettingsManager()

    /// Audio-related settings (microphone, sound effects)
    @Published var audio = AudioSettingsManager()

    /// Storage and folder management settings
    @Published var storage = StorageSettingsManager()

    /// API key management and validation
    @Published var apiKeys = APIKeySettingsManager()

    /// Auto-delete settings for automatic cleanup of old recordings
    @Published var autoDelete = AutoDeleteSettingsManager()

    // MARK: - Text Input Settings

    @AppStorage("pasteResultText") var pasteResultText: Bool = true
    @AppStorage("removeFillerWords") var removeFillerWords: Bool = true
    @AppStorage("restoreClipboardAfterPaste") var restoreClipboardAfterPaste: Bool = true
    @AppStorage("clipboardRestoreDelaySeconds") var clipboardRestoreDelaySeconds: Double = 10.0
    @AppStorage("hideFromClipboardHistory") var hideFromClipboardHistory: Bool = true
    @AppStorage("autocapitalizeInsert") var autocapitalizeInsert: Bool = true
    /// Auto-capture & store segment/word timestamps for local Whisper recordings
    /// (stored as a JSON blob on the Transcript for future use, e.g. caption
    /// export). Non-Whisper engines ignore this and pay nothing.
    @AppStorage("storeWordTimestamps") var storeWordTimestamps: Bool = true

    // MARK: - AI Model Settings

    @AppStorage("showExperimentalModels") var showExperimentalModels: Bool = false
    @AppStorage("defaultTranscriptionModel") var defaultTranscriptionModel: String = "base"
    @AppStorage("defaultLanguage") var defaultLanguage: String = "en"
    @AppStorage("currentMode") var currentMode: String = "Default"
    @AppStorage("currentModeId") var currentModeId: String = ""

    @Published var defaultModelByMode: [String: String] = SettingsManager.loadDefaultModelByMode() {
        didSet { SettingsManager.saveDefaultModelByMode(defaultModelByMode) }
    }

    // MARK: - Advanced Settings

    /// Maximum recording length in seconds before auto-stopping to prevent
    /// runaway captures. 0 = no limit. Stored under a NEW key
    /// (`maxRecordingDurationSeconds`) rather than the legacy
    /// `maxRecordingDuration`: that key was never exposed in UI, so its old
    /// default (300) leaked into exported backups without ever reflecting a
    /// user choice — `migrateLegacyMaxRecordingDuration()` carries over only a
    /// non-300 legacy value.
    @AppStorage("maxRecordingDurationSeconds") var maxRecordingDurationSeconds: Int = 3600
    @AppStorage("audioSampleRate") var audioSampleRate: Double = 16000
    @AppStorage("keepAudioFiles") var keepAudioFiles: Bool = false
    @AppStorage("historyRetentionDays") var historyRetentionDays: Int = 30

    // MARK: - Keyboard Shortcut Settings

    /// Push to Talk feature toggle - enabled/disabled in settings (kept for backward compatibility)
    @AppStorage("pushToTalkEnabled") var pushToTalkEnabled: Bool = false

    /// Push to Talk mode - determines which key(s) trigger recording
    /// New setting that replaces the simple boolean toggle
    @AppStorage("pushToTalkMode") var pushToTalkMode: PushToTalkMode = .disabled

    /// Push to Talk Double Press - determines if double pressing the PTT key toggles recording
    @AppStorage("pushToTalkDoublePressEnabled") var pushToTalkDoublePressEnabled: Bool = true

    /// Quick Capture feature toggle - when on, the quickCapture shortcut starts a
    /// recording whose transcription is sent to Apple Notes (instead of pasted into
    /// the focused app).
    @AppStorage("quickCaptureEnabled") var quickCaptureEnabled: Bool = false

    /// Quick Capture transcription mode (UUID string). Empty string means
    /// "use the currently active mode at the moment the shortcut fires".
    @AppStorage("quickCaptureModeId") var quickCaptureModeId: String = ""

    /// Whether streaming transcription is enabled
    /// When disabled, the streaming shortcut does nothing and UI elements are disabled
    /// Defaults to false - users must opt-in to enable streaming
    @AppStorage("streamingEnabled") var streamingEnabled: Bool = false

    /// Language for streaming transcription
    /// Used by the streaming shortcut (Option+Shift+Space)
    /// Streaming operates independently of modes and uses HyperWhisper Cloud
    /// Defaults to English for best accuracy; users can change to other languages or auto-detect
    @AppStorage("streamingLanguage") var streamingLanguage: String = "en"

    /// Streaming-language read with an empty-string boundary-normalization step:
    /// legacy installs may have a literal `""` stored from old code paths that
    /// treated empty-string as "no preference". Treat that as `"auto"` so the
    /// streaming flow lets the server auto-detect instead of silently coercing
    /// to `"en"` and regressing non-English users.
    var streamingLanguageEffective: String {
        let stored = streamingLanguage
        return stored.isEmpty ? LanguageData.automaticCode : stored
    }

    /// Selected streaming transcription provider
    /// Determines which backend is used for real-time streaming:
    /// - "hyperwhisperCloud": HyperWhisper Cloud (default, no API key needed)
    /// - "deepgram": Deepgram direct (requires API key)
    /// - "elevenLabs": ElevenLabs direct (requires API key)
    /// - "xai": xAI direct (requires Grok/xAI API key)
    @AppStorage("streamingProvider") var streamingProvider: String = "hyperwhisperCloud"

    /// Deepgram model for streaming transcription
    /// Only used when streamingProvider is "deepgram"
    /// Nova-3 family only: "nova-3-general" (default) or "nova-3-medical"
    @AppStorage("streamingDeepgramModel") var streamingDeepgramModel: String = "nova-3-general"

    /// Deepgram fast formatting (no_delay) for streaming transcription
    /// When enabled, smart formatting results are returned immediately without
    /// waiting for additional context, prioritizing typing speed over formatting accuracy.
    /// Only used when streamingProvider is "deepgram"
    @AppStorage("streamingFastFormatting") var streamingFastFormatting: Bool = true

    /// Parakeet version for on-device streaming transcription.
    /// - "parakeet-tdt-0.6b-v2": English-only, highest recall
    /// - "parakeet-tdt-0.6b-v3": Multilingual (25 European languages)
    /// Only used when streamingProvider is "parakeetLocal".
    @AppStorage("streamingLocalParakeetVersion") var streamingLocalParakeetVersion: String = "parakeet-tdt-0.6b-v3"

    /// Default Nemotron variant when no preference is stored. Multilingual is
    /// the broader cover so it's the safer first run; users can downshift to
    /// Latin for speed.
    static let defaultStreamingNemotronVariant = "nemotron-asr-3.5-multilingual"

    /// Nemotron 3.5 variant for on-device streaming transcription.
    /// - "nemotron-asr-3.5-latin": Fast path, ~6 Latin-script languages.
    /// - "nemotron-asr-3.5-multilingual": ~40 languages incl. zh/ja/ko/ar.
    /// Only used when streamingProvider is "nemotronLocal".
    @AppStorage("streamingLocalNemotronVariant") var streamingLocalNemotronVariant: String = SettingsManager.defaultStreamingNemotronVariant

    // MARK: - Private Properties

    private var cancellables = Set<AnyCancellable>()

    // MARK: - Initialization

    init() {
        validateSettings()

        // Normalize legacy language values
        if defaultLanguage == "en-US" { defaultLanguage = "en" }

        migrateLegacyMaxRecordingDuration()

        bindSubManagerChanges()
    }

    /// The recording cap as a `TimeInterval`, or `nil` when disabled (0 = off).
    var maxRecordingDurationInterval: TimeInterval? {
        maxRecordingDurationSeconds > 0 ? TimeInterval(maxRecordingDurationSeconds) : nil
    }

    /// One-time carry-over from the legacy `maxRecordingDuration` key. The
    /// legacy default (300) was never surfaced in UI and never enforced, so it
    /// can't represent a user choice — treat it as unset and keep the new 1h
    /// default. Any other legacy value (e.g. restored from a backup that was
    /// hand-edited) is preserved.
    ///
    /// Reads the persistent domain, NOT `object(forKey:)`, because the latter
    /// also returns values registered via `registerHyperWhisperDefaults()` and
    /// would make the new key look "already set" on every launch.
    private func migrateLegacyMaxRecordingDuration() {
        let stored = UserDefaults.standard.persistentDomain(
            forName: Bundle.main.bundleIdentifier ?? "com.hyperwhisper"
        ) ?? [:]
        guard stored["maxRecordingDurationSeconds"] == nil,
              let legacy = stored["maxRecordingDuration"] as? Int,
              legacy != 300
        else { return }
        maxRecordingDurationSeconds = legacy
    }
    
    // MARK: - Forwarding Properties - General Settings

    var launchAtLogin: Bool {
        get { general.launchAtLogin }
        set { general.launchAtLogin = newValue }
    }

    var showInDock: Bool {
        get { general.showInDock }
        set { general.showInDock = newValue }
    }

    var launchMinimized: Bool {
        get { general.launchMinimized }
        set { general.launchMinimized = newValue }
    }

    var showRecordingWindow: Bool {
        get { general.showRecordingWindow }
        set { general.showRecordingWindow = newValue }
    }

    var checkForUpdatesAutomatically: Bool {
        get { general.checkForUpdatesAutomatically }
        set { general.checkForUpdatesAutomatically = newValue }
    }

    var enableErrorLogging: Bool {
        get { general.enableErrorLogging }
        set { general.enableErrorLogging = newValue }
    }

    var enableVAD: Bool {
        get { general.enableVAD }
        set { general.enableVAD = newValue }
    }

    // MARK: - Forwarding Properties - Audio Settings

    var selectedMicrophoneId: String {
        get { audio.selectedMicrophoneId }
        set { audio.selectedMicrophoneId = newValue }
    }

    var autoIncreaseMicVolume: Bool {
        get { audio.autoIncreaseMicVolume }
        set { audio.autoIncreaseMicVolume = newValue }
    }

    var keepMicrophoneWarm: Bool {
        get { audio.keepMicrophoneWarm }
        set { audio.keepMicrophoneWarm = newValue }
    }

    var enableSoundEffects: Bool {
        get { audio.enableSoundEffects }
        set { audio.enableSoundEffects = newValue }
    }

    var soundTheme: SoundTheme {
        get { audio.soundTheme }
        set { audio.soundTheme = newValue }
    }

    var soundEffectsVolume: Double {
        get { audio.soundEffectsVolume }
        set { audio.soundEffectsVolume = newValue }
    }

    // MARK: - Forwarding Properties - Storage Settings

    var appFolderPath: String {
        get { storage.appFolderPath }
        set { storage.appFolderPath = newValue }
    }

    var recordingsFolder: String {
        get { storage.recordingsFolder }
        set { storage.recordingsFolder = newValue }
    }

    var documentsPermissionExplained: Bool {
        get { storage.documentsPermissionExplained }
        set { storage.documentsPermissionExplained = newValue }
    }

    var documentsAccessDenied: Bool {
        get { storage.documentsAccessDenied }
        set { storage.documentsAccessDenied = newValue }
    }

    var userChoseAlternateStorage: Bool {
        get { storage.userChoseAlternateStorage }
        set { storage.userChoseAlternateStorage = newValue }
    }

    var showDocumentsPermissionAlert: Bool {
        get { storage.showDocumentsPermissionAlert }
        set { storage.showDocumentsPermissionAlert = newValue }
    }

    var filesyncEnabled: Bool {
        get { storage.filesyncEnabled }
        set { storage.filesyncEnabled = newValue }
    }

    var storeAsM4A: Bool {
        get { storage.storeAsM4A }
        set { storage.storeAsM4A = newValue }
    }

    var validationError: String? {
        get { storage.validationError }
        set { storage.validationError = newValue }
    }

    // Storage methods
    func changeRecordingsFolder(to url: URL) {
        storage.changeRecordingsFolder(to: url)
    }

    func ensureRecordingsFolderExists() {
        storage.ensureRecordingsFolderExists()
    }

    func prepareRecordingsFolderIfNeeded() {
        storage.prepareRecordingsFolderIfNeeded()
    }

    func prepareRecordingsFolderIfNeededAsync(timeoutSeconds: Double = 120) async -> Bool {
        await storage.prepareRecordingsFolderIfNeededAsync(timeoutSeconds: timeoutSeconds)
    }

    func proceedWithDocumentsAccess() {
        storage.proceedWithDocumentsAccess()
    }

    func useAlternateStorageInstead() {
        storage.useAlternateStorageInstead()
    }

    func fallbackToBestAvailableLocation() -> Bool {
        storage.fallbackToBestAvailableLocation()
    }

    func offerManualFolderSelection() -> Bool {
        storage.offerManualFolderSelection()
    }

    func presentStorageRecoveryPrompt() -> Bool {
        storage.presentStorageRecoveryPrompt()
    }

    // MARK: - Forwarding Properties - API Keys

    var openAIAPIKey: String {
        get { apiKeys.openAIAPIKey }
        set { apiKeys.openAIAPIKey = newValue }
    }

    var groqAPIKey: String {
        get { apiKeys.groqAPIKey }
        set { apiKeys.groqAPIKey = newValue }
    }

    var anthropicAPIKey: String {
        get { apiKeys.anthropicAPIKey }
        set { apiKeys.anthropicAPIKey = newValue }
    }

    var geminiAPIKey: String {
        get { apiKeys.geminiAPIKey }
        set { apiKeys.geminiAPIKey = newValue }
    }

    var deepgramAPIKey: String {
        get { apiKeys.deepgramAPIKey }
        set { apiKeys.deepgramAPIKey = newValue }
    }

    var assemblyAIAPIKey: String {
        get { apiKeys.assemblyAIAPIKey }
        set { apiKeys.assemblyAIAPIKey = newValue }
    }

    var elevenLabsAPIKey: String {
        get { apiKeys.elevenLabsAPIKey }
        set { apiKeys.elevenLabsAPIKey = newValue }
    }

    var mistralAPIKey: String {
        get { apiKeys.mistralAPIKey }
        set { apiKeys.mistralAPIKey = newValue }
    }

    var sonioxAPIKey: String {
        get { apiKeys.sonioxAPIKey }
        set { apiKeys.sonioxAPIKey = newValue }
    }

    var cerebrasAPIKey: String {
        get { apiKeys.cerebrasAPIKey }
        set { apiKeys.cerebrasAPIKey = newValue }
    }

    var grokAPIKey: String {
        get { apiKeys.grokAPIKey }
        set { apiKeys.grokAPIKey = newValue }
    }

    var useOpenAITranscription: Bool {
        get { apiKeys.useOpenAITranscription }
        set { apiKeys.useOpenAITranscription = newValue }
    }

    // API key methods
    func apiKey(for provider: CloudProvider) -> String {
        apiKeys.apiKey(for: provider)
    }

    func setAPIKey(_ key: String, for provider: CloudProvider) {
        apiKeys.setAPIKey(key, for: provider)
    }

    func hasAPIKey(for provider: CloudProvider) -> Bool {
        apiKeys.hasAPIKey(for: provider)
    }

    func postProcessingAPIKey(for provider: PostProcessingProvider) -> String {
        apiKeys.postProcessingAPIKey(for: provider)
    }

    func setPostProcessingAPIKey(_ key: String, for provider: PostProcessingProvider) {
        apiKeys.setPostProcessingAPIKey(key, for: provider)
    }

    func hasPostProcessingAPIKey(for provider: PostProcessingProvider) -> Bool {
        apiKeys.hasPostProcessingAPIKey(for: provider)
    }

    func getMissingAPIKeys(for mode: Mode) -> [MissingAPIKey] {
        apiKeys.getMissingAPIKeys(for: mode)
    }

    func getMissingAPIKeys(for snapshot: ModeSnapshot) -> [MissingAPIKey] {
        apiKeys.getMissingAPIKeys(for: snapshot)
    }

    static func onlyPostProcessingKeysMissing(_ missingKeys: [MissingAPIKey]) -> Bool {
        APIKeySettingsManager.onlyPostProcessingKeysMissing(missingKeys)
    }

    // MARK: - Public Methods

    func defaultModel(forModeId modeId: String) -> String? {
        defaultModelByMode[modeId]
    }

    func setDefaultModel(_ modelId: String, forModeId modeId: String) {
        defaultModelByMode[modeId] = modelId
    }

    // MARK: - Private Methods

    private func validateSettings() {
        if audioSampleRate < 8000 || audioSampleRate > 48000 {
            audioSampleRate = 16000
        }

        if historyRetentionDays < 0 {
            historyRetentionDays = 30
        }
    }

    /// Propagate change notifications from sub-managers so SwiftUI views observing
    /// `SettingsManager` continue to refresh when delegated values change.
    private func bindSubManagerChanges() {
        general.objectWillChange
            .sink { [weak self] _ in self?.objectWillChange.send() }
            .store(in: &cancellables)

        audio.objectWillChange
            .sink { [weak self] _ in self?.objectWillChange.send() }
            .store(in: &cancellables)

        storage.objectWillChange
            .sink { [weak self] _ in self?.objectWillChange.send() }
            .store(in: &cancellables)

        apiKeys.objectWillChange
            .sink { [weak self] _ in self?.objectWillChange.send() }
            .store(in: &cancellables)

        autoDelete.objectWillChange
            .sink { [weak self] _ in self?.objectWillChange.send() }
            .store(in: &cancellables)
    }

    // MARK: - Persistence Helpers

    private static let defaultModelByModeKey = "defaultModelByMode"

    private static func loadDefaultModelByMode() -> [String: String] {
        if let data = UserDefaults.standard.data(forKey: defaultModelByModeKey),
           let dict = try? JSONDecoder().decode([String: String].self, from: data) {
            return dict
        }
        return [:]
    }

    private static func saveDefaultModelByMode(_ dict: [String: String]) {
        if let data = try? JSONEncoder().encode(dict) {
            UserDefaults.standard.set(data, forKey: defaultModelByModeKey)
        }
    }
}

// MARK: - UserDefaults Extension

extension UserDefaults {
    static func registerHyperWhisperDefaults() {
        let defaults: [String: Any] = [
            "launchAtLogin": true,
            "showInDock": true,
            "showRecordingWindow": true,
            "checkForUpdatesAutomatically": true,
            "enableErrorLogging": true,
            "enableVAD": true,
            "autoIncreaseMicVolume": true,
            "keepMicrophoneWarm": false,
            "enableSoundEffects": true,
            "soundTheme": "Classic",
            "soundEffectsVolume": 0.5,
            "pasteResultText": true,
            "removeFillerWords": true,
            "restoreClipboardAfterPaste": true,
            "clipboardRestoreDelaySeconds": 10.0,
            "hideFromClipboardHistory": true,
            "autocapitalizeInsert": true,
            "storeWordTimestamps": true,
            "filesyncEnabled": false,
            "showExperimentalModels": false,
            "defaultTranscriptionModel": "base",
            "defaultLanguage": "en",
            "maxRecordingDurationSeconds": 3600,
            "audioSampleRate": 16000.0,
            "keepAudioFiles": false,
            "historyRetentionDays": 30,
            "launchMinimized": false,
            "pushToTalkEnabled": false,
            "pushToTalkMode": "disabled",
            "pushToTalkDoublePressEnabled": true,
            "quickCaptureEnabled": false,
            "quickCaptureModeId": ""
        ]

        UserDefaults.standard.register(defaults: defaults)
    }
}

// MARK: - Notification Names

extension Notification.Name {
    static let updateDockVisibility = Notification.Name("updateDockVisibility")
}

// MARK: - CloudProviderAPIKeyProviding

extension SettingsManager: CloudProviderAPIKeyProviding {}
