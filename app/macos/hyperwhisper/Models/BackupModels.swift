//
//  BackupModels.swift
//  hyperwhisper
//
//  BACKUP DATA MODELS
//  Codable structures for settings import/export functionality.
//  These models define the JSON format for backup files.
//
//  STRUCTURE:
//  - BackupData: Root container with version and metadata
//  - BackupSettings: All UserDefaults-based settings
//  - BackupMode/BackupVocabularyItem: Core Data entity representations
//  - BackupAPIKeys: Optional keychain data (opt-in)
//  - Supporting types: Options, results, and conflict resolution enums
//
//  VERSION HISTORY:
//  - Version 1: Initial release
//

import Foundation

// MARK: - Root Backup Structure

/// Root structure for the backup file
/// Contains metadata, settings, and optional sensitive data
struct BackupData: Codable {
    /// Backup format version for future compatibility
    let version: Int
    /// Date when the backup was created
    let exportDate: Date
    /// App version that created the backup
    let appVersion: String
    /// All UserDefaults-based settings.
    /// Optional so a section-selectable backup can omit it entirely (key absent).
    let settings: BackupSettings?
    /// Optional API keys (user must opt-in during export)
    let apiKeys: BackupAPIKeys?
    /// Optional license key (user must opt-in during export)
    let licenseKey: String?
    /// All transcription modes from Core Data.
    /// Optional so a section-selectable backup can omit it entirely (key absent).
    let modes: [BackupMode]?
    /// All vocabulary items from Core Data.
    /// Optional so a section-selectable backup can omit it entirely (key absent).
    let vocabulary: [BackupVocabularyItem]?

    /// Current backup format version
    static let currentVersion = 1
}

// MARK: - Universal v2 (vocabulary-only cross-platform bridge)

/// A minimal projection of the universal `.hwbackup.json` (schemaVersion 2) format that
/// macOS reads and writes for VOCABULARY ONLY, so a vocab-only file round-trips mac↔Windows.
/// macOS does not (yet) understand the universal settings/modes shape — those stay in the
/// legacy v1 `BackupData` format. `exportDate` is kept as a String to avoid coupling the
/// bridge to any single platform's date serialization.
struct UniversalVocabBackup: Codable {
    let schemaVersion: Int
    let exportDate: String?
    let appVersion: String?
    let platform: String?
    let vocabulary: [UniversalVocabularyItem]?

    /// Schema version of the universal cross-platform format.
    static let universalSchemaVersion = 2
}

/// Vocabulary item in the universal v2 format. Field names/shape match the Windows
/// `UniversalVocabularyItem` and the `shared-backup` JSON Schema (id/word/replacement/sortOrder/source).
struct UniversalVocabularyItem: Codable {
    let id: UUID
    let word: String
    let replacement: String?
    let sortOrder: Int
    let source: String?

    /// Builds a universal item from a Core Data Vocabulary entity (for export).
    init(from vocabulary: Vocabulary) {
        self.id = vocabulary.id ?? UUID()
        self.word = vocabulary.word ?? ""
        self.replacement = vocabulary.replacement
        self.sortOrder = Int(vocabulary.sortOrder)
        self.source = vocabulary.value(forKey: "source") as? String
    }

    init(id: UUID, word: String, replacement: String?, sortOrder: Int, source: String?) {
        self.id = id
        self.word = word
        self.replacement = replacement
        self.sortOrder = sortOrder
        self.source = source
    }
}

// MARK: - Universal v2 (FULL backup DTOs — settings + modes + vocab)

/// Top-level envelope for a FULL universal-v2 `.hwbackup.json` (schemaVersion 2), used by the
/// NEW macOS full v2 import/export path (M3-D). This is ADDITIVE — the legacy v1 `BackupData`
/// path and the vocab-only `UniversalVocabBackup` path are untouched.
///
/// `exportDate` is a STRING (ISO-8601) in the v2 envelope — it is NOT run through the v1
/// `Date`-based encoder. `settings` carries ONLY the 5 universal categories; macOS-only settings
/// live under the top-level `platformExtensions.macos.settings` (the core parks them there).
/// Decoding is lenient: unknown top-level keys are ignored automatically by `Codable`, and the
/// nested DTOs ignore unknown universal keys, so a Windows backup imports without throwing.
struct UniversalBackupDTO: Codable {
    let schemaVersion: Int
    let exportDate: String
    let appVersion: String
    let platform: String
    /// 5 universal settings categories (general/textOutput/storage/streaming/advanced),
    /// kept as an opaque JSON object so we never couple to per-field churn — the core
    /// owns the macOS↔universal field mapping. `nil` when settings were not exported.
    let settings: JSONValue?
    let modes: [UniversalModeDTO]?
    /// Vocabulary carried as opaque `JSONValue` items (NOT `[UniversalVocabularyItem]`): on import
    /// the authoritative read is the RAW `topLevel["vocabulary"]` lenient path (string id, optional
    /// sortOrder), so modelling vocab strictly here would make the full-envelope decode throw on a
    /// schema-valid file. As `JSONValue` it decodes any shape and round-trips on export.
    let vocabulary: [JSONValue]?
    /// Flat lowercase-provider API keys (matches the vocab-universal path + Windows).
    let apiKeys: [String: String]?
    let licenseKey: String?
    /// Top-level platform extensions map, e.g. `{"macos":{"settings":{...}}, "windows":{...}}`.
    /// On export macOS HOISTS the `SettingsRecord`'s record-level `platformExtensions` here.
    let platformExtensions: JSONValue?

    static let universalSchemaVersion = 2
}

/// A universal-v2 mode. CodingKeys are the universal camelCase names — which already match
/// `BackupMode` 1:1 — but this is a SEPARATE type so the v1 `BackupMode` encoding is undisturbed.
///
/// Decode is lenient: unknown universal keys (`localPostProcessingModel`, per-mode
/// `platformExtensions.windows`, etc.) are simply not listed here, so a Windows mode decodes
/// without throwing. All non-identity fields are optional for the same reason. On EXPORT macOS
/// emits `platformExtensions: {}` per mode to match the macOS example fixture.
struct UniversalModeDTO: Codable {
    let id: String
    let name: String
    let preset: String?
    let language: String?
    let model: String?
    let isDefault: Bool?
    let sortOrder: Int?
    let punctuation: Bool?
    let capitalization: Bool?
    let profanityFilter: Bool?
    let removeTrailingPeriod: Bool?
    let englishSpelling: String?
    let cloudProvider: String?
    let cloudTranscriptionModel: String?
    let cloudTranscriptionDomain: String?
    let postProcessingMode: Int?
    let postProcessingProvider: String?
    let languageModel: String?
    let userSystemPrompt: String?
    let customInstructions: String?
    let geminiCustomPrompt: String?
    let cloudAccuracyTier: String?
    let cloudPostProcessingModel: String?
    /// Per-mode platform extensions. macOS emits `{}` on export; on import the value is
    /// ignored (BackupMode has no per-mode extensions — the known, accepted limitation).
    let platformExtensions: JSONValue?

    /// Builds a universal mode DTO from a v1 `BackupMode` (export projection). Emits an
    /// explicit empty `platformExtensions` object to match the macOS example fixture.
    init(from m: BackupMode) {
        self.id = m.id.uuidString
        self.name = m.name
        self.preset = m.preset
        self.language = m.language
        self.model = m.model
        self.isDefault = m.isDefault
        self.sortOrder = Int(m.sortOrder)
        self.punctuation = m.punctuation
        self.capitalization = m.capitalization
        self.profanityFilter = m.profanityFilter
        self.removeTrailingPeriod = m.removeTrailingPeriod
        self.englishSpelling = m.englishSpelling
        self.cloudProvider = m.cloudProvider
        self.cloudTranscriptionModel = m.cloudTranscriptionModel
        self.cloudTranscriptionDomain = m.cloudTranscriptionDomain
        self.postProcessingMode = Int(m.postProcessingMode)
        self.postProcessingProvider = m.postProcessingProvider
        self.languageModel = m.languageModel
        self.userSystemPrompt = m.userSystemPrompt
        self.customInstructions = m.customInstructions
        self.geminiCustomPrompt = m.geminiCustomPrompt
        self.cloudAccuracyTier = m.cloudAccuracyTier
        self.cloudPostProcessingModel = m.cloudPostProcessingModel
        // Re-emit any foreign (non-macOS) platformExtensions slices captured on a
        // prior v2 import (H4) so a Windows mode's per-mode data survives a macOS
        // round-trip. macOS has no per-mode slice of its own to add, so when
        // nothing was preserved this stays an explicit empty object (matching the
        // example fixture).
        if let raw = m.foreignPlatformExtensions,
           let data = raw.data(using: .utf8),
           let decoded = try? JSONDecoder().decode(JSONValue.self, from: data),
           case .object(let obj) = decoded, !obj.isEmpty {
            self.platformExtensions = .object(obj)
        } else {
            self.platformExtensions = .object([:])
        }
    }
}

/// A minimal `Codable` JSON value used to carry opaque sub-trees (`settings`,
/// `platformExtensions`) verbatim through the v2 DTOs without modelling every field.
/// Round-trips losslessly (objects preserve key order via `[String: JSONValue]` is NOT
/// guaranteed, but the core re-serializes anyway, so order is irrelevant on the wire).
indirect enum JSONValue: Codable {
    case null
    case bool(Bool)
    case number(Double)
    case string(String)
    case array([JSONValue])
    case object([String: JSONValue])

    init(from decoder: Decoder) throws {
        let c = try decoder.singleValueContainer()
        if c.decodeNil() {
            self = .null
        } else if let b = try? c.decode(Bool.self) {
            self = .bool(b)
        } else if let n = try? c.decode(Double.self) {
            self = .number(n)
        } else if let s = try? c.decode(String.self) {
            self = .string(s)
        } else if let a = try? c.decode([JSONValue].self) {
            self = .array(a)
        } else if let o = try? c.decode([String: JSONValue].self) {
            self = .object(o)
        } else {
            throw DecodingError.dataCorrupted(
                .init(codingPath: decoder.codingPath, debugDescription: "Unsupported JSON value")
            )
        }
    }

    func encode(to encoder: Encoder) throws {
        var c = encoder.singleValueContainer()
        switch self {
        case .null: try c.encodeNil()
        case .bool(let b): try c.encode(b)
        case .number(let n): try c.encode(n)
        case .string(let s): try c.encode(s)
        case .array(let a): try c.encode(a)
        case .object(let o): try c.encode(o)
        }
    }

    /// Convenience accessor for nested object lookup (used by the platformExtensions
    /// re-inject / parked-field reads).
    var objectValue: [String: JSONValue]? {
        if case .object(let o) = self { return o }
        return nil
    }
    var stringValue: String? {
        if case .string(let s) = self { return s }
        return nil
    }

    /// Deep-merges `self` (the OVERRIDE) on top of `base`. Where both are objects, keys are merged
    /// recursively (override keys win, base keys absent from override are preserved). For any
    /// non-object value (or a type mismatch), the override (`self`) wins wholesale.
    ///
    /// Used by the v2 import settings path: the core's imported macOS-settings JSON (which a Windows
    /// backup leaves missing macOS-only fields) is merged OVER the current live macOS settings, so
    /// imported values apply while macOS-only fields keep their current values (decode never throws).
    func deepMerged(over base: JSONValue) -> JSONValue {
        guard case .object(let overrideObj) = self, case .object(let baseObj) = base else {
            return self // non-objects (or mismatch): override wins wholesale
        }
        var merged = baseObj
        for (key, overrideValue) in overrideObj {
            if let baseValue = merged[key] {
                merged[key] = overrideValue.deepMerged(over: baseValue)
            } else {
                merged[key] = overrideValue
            }
        }
        return .object(merged)
    }
}

// MARK: - Settings Container

/// Container for all UserDefaults-based settings
/// Organized by category matching the settings managers
struct BackupSettings: Codable {
    let general: BackupGeneralSettings
    let audio: BackupAudioSettings
    let storage: BackupStorageSettings
    let textOutput: BackupTextOutputSettings
    let shortcuts: BackupShortcutSettings
    let aiModel: BackupAIModelSettings
    let advanced: BackupAdvancedSettings

    private enum CodingKeys: String, CodingKey {
        case general, audio, storage, textOutput, shortcuts, aiModel, advanced
        case developer // Legacy field, ignored on decode
    }

    init(general: BackupGeneralSettings, audio: BackupAudioSettings, storage: BackupStorageSettings, textOutput: BackupTextOutputSettings, shortcuts: BackupShortcutSettings, aiModel: BackupAIModelSettings, advanced: BackupAdvancedSettings) {
        self.general = general
        self.audio = audio
        self.storage = storage
        self.textOutput = textOutput
        self.shortcuts = shortcuts
        self.aiModel = aiModel
        self.advanced = advanced
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        general = try container.decode(BackupGeneralSettings.self, forKey: .general)
        audio = try container.decode(BackupAudioSettings.self, forKey: .audio)
        storage = try container.decode(BackupStorageSettings.self, forKey: .storage)
        textOutput = try container.decode(BackupTextOutputSettings.self, forKey: .textOutput)
        shortcuts = try container.decode(BackupShortcutSettings.self, forKey: .shortcuts)
        aiModel = try container.decode(BackupAIModelSettings.self, forKey: .aiModel)
        advanced = try container.decode(BackupAdvancedSettings.self, forKey: .advanced)
        // Ignore developer field if present in old backups
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode(general, forKey: .general)
        try container.encode(audio, forKey: .audio)
        try container.encode(storage, forKey: .storage)
        try container.encode(textOutput, forKey: .textOutput)
        try container.encode(shortcuts, forKey: .shortcuts)
        try container.encode(aiModel, forKey: .aiModel)
        try container.encode(advanced, forKey: .advanced)
        // Don't encode developer - it's removed
    }
}

// MARK: - Individual Settings Categories

/// General app behavior settings
/// Source: GeneralSettingsManager
struct BackupGeneralSettings: Codable {
    let launchAtLogin: Bool
    let showInDock: Bool
    let launchMinimized: Bool
    let showRecordingWindow: Bool
    let checkForUpdatesAutomatically: Bool
    let enableErrorLogging: Bool
}

/// Audio-related settings
/// Source: AudioSettingsManager
/// NOTE: selectedMicrophoneId is excluded (device-specific)
struct BackupAudioSettings: Codable {
    let autoIncreaseMicVolume: Bool
    let mediaControlMode: String
    let enableSoundEffects: Bool
    let soundTheme: String
    let soundEffectsVolume: Double
}

/// Storage settings
/// Source: StorageSettingsManager
/// NOTE: Folder paths are excluded (device-specific)
struct BackupStorageSettings: Codable {
    let filesyncEnabled: Bool
    let storeAsM4A: Bool
}

/// Text output/clipboard settings
/// Source: SettingsManager
struct BackupTextOutputSettings: Codable {
    let pasteResultText: Bool
    let removeFillerWords: Bool
    let restoreClipboardAfterPaste: Bool
    let hideFromClipboardHistory: Bool
    let clipboardRestoreDelaySeconds: Double
    /// Optional so legacy (pre-#643) backups that lack the field decode to `nil`,
    /// letting the import path leave the user's current preference untouched
    /// instead of silently flipping a disabled Autocapitalize Insert back on.
    let autocapitalizeInsert: Bool?
    let storeWordTimestamps: Bool

    // BACKWARDS COMPATIBILITY: Accept copyToClipboard from old backups but ignore it
    private enum CodingKeys: String, CodingKey {
        case pasteResultText
        case removeFillerWords
        case restoreClipboardAfterPaste
        case hideFromClipboardHistory
        case clipboardRestoreDelaySeconds
        case autocapitalizeInsert
        case storeWordTimestamps
        case copyToClipboard // Legacy field, ignored on decode
    }

    init(pasteResultText: Bool, removeFillerWords: Bool, restoreClipboardAfterPaste: Bool, hideFromClipboardHistory: Bool, clipboardRestoreDelaySeconds: Double, autocapitalizeInsert: Bool?, storeWordTimestamps: Bool) {
        self.pasteResultText = pasteResultText
        self.removeFillerWords = removeFillerWords
        self.restoreClipboardAfterPaste = restoreClipboardAfterPaste
        self.hideFromClipboardHistory = hideFromClipboardHistory
        self.clipboardRestoreDelaySeconds = clipboardRestoreDelaySeconds
        self.autocapitalizeInsert = autocapitalizeInsert
        self.storeWordTimestamps = storeWordTimestamps
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        pasteResultText = try container.decode(Bool.self, forKey: .pasteResultText)
        removeFillerWords = try container.decodeIfPresent(Bool.self, forKey: .removeFillerWords) ?? true
        restoreClipboardAfterPaste = try container.decode(Bool.self, forKey: .restoreClipboardAfterPaste)
        hideFromClipboardHistory = try container.decodeIfPresent(Bool.self, forKey: .hideFromClipboardHistory) ?? true
        clipboardRestoreDelaySeconds = try container.decode(Double.self, forKey: .clipboardRestoreDelaySeconds)
        // Optional (no default): legacy backups that omit the field decode to nil
        // so the import path can skip applying it and preserve the user's setting.
        autocapitalizeInsert = try container.decodeIfPresent(Bool.self, forKey: .autocapitalizeInsert)
        // New in v2.x — default to true (matches SettingsManager default) so older backups decode unchanged.
        storeWordTimestamps = try container.decodeIfPresent(Bool.self, forKey: .storeWordTimestamps) ?? true
        // Ignore copyToClipboard if present in old backups
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode(pasteResultText, forKey: .pasteResultText)
        try container.encode(removeFillerWords, forKey: .removeFillerWords)
        try container.encode(restoreClipboardAfterPaste, forKey: .restoreClipboardAfterPaste)
        try container.encode(hideFromClipboardHistory, forKey: .hideFromClipboardHistory)
        try container.encode(clipboardRestoreDelaySeconds, forKey: .clipboardRestoreDelaySeconds)
        try container.encodeIfPresent(autocapitalizeInsert, forKey: .autocapitalizeInsert)
        try container.encode(storeWordTimestamps, forKey: .storeWordTimestamps)
        // Don't encode copyToClipboard - it's removed
    }
}

/// Keyboard shortcut settings
/// Source: SettingsManager
///
/// `quickCapture*` fields are optional so backups created before v1 of the
/// Quick Capture feature still decode without error.
struct BackupShortcutSettings: Codable {
    let pushToTalkMode: String
    let pushToTalkDoublePressEnabled: Bool
    let quickCaptureEnabled: Bool?
    let quickCaptureModeId: String?
}

/// AI model and transcription settings
/// Source: SettingsManager
struct BackupAIModelSettings: Codable {
    let showExperimentalModels: Bool
    let defaultTranscriptionModel: String
    let defaultLanguage: String
    /// Dictionary mapping mode IDs to their default model selections
    let defaultModelByMode: [String: String]
}

/// Advanced transcription settings
/// Source: SettingsManager
struct BackupAdvancedSettings: Codable {
    let maxRecordingDuration: Int
    let audioSampleRate: Double
    let keepAudioFiles: Bool
    let historyRetentionDays: Int
}

// MARK: - Core Data Entity Representations

/// Represents a Mode entity for backup
/// Maps all Mode Core Data properties except relationships
struct BackupMode: Codable {
    let id: UUID
    let name: String
    let preset: String
    let language: String
    let model: String
    let punctuation: Bool
    let capitalization: Bool
    let profanityFilter: Bool
    let customInstructions: String?
    let languageModel: String?
    let cloudProvider: String?
    let cloudTranscriptionModel: String?
    let postProcessingMode: Int16
    let postProcessingProvider: String?
    let englishSpelling: String?
    let userSystemPrompt: String?
    let isDefault: Bool
    let sortOrder: Int16
    let cloudAccuracyTier: String?
    let removeTrailingPeriod: Bool?
    let geminiCustomPrompt: String?
    let cloudPostProcessingModel: String?
    let cloudTranscriptionDomain: String?  // X-STT-Domain ("medical") — optional for back-compat decode

    /// Raw JSON of the mode's NON-macOS `platformExtensions` slices (e.g. the
    /// `windows` blob), captured on universal-v2 import and re-emitted on v2 export
    /// so a foreign platform's per-mode data survives a macOS round-trip (H4).
    /// In-memory carrier only — EXCLUDED from the v1 `BackupMode` Codable surface
    /// (see `CodingKeys`); it is persisted on the Core Data `Mode` entity instead.
    var foreignPlatformExtensions: String?

    /// Explicit keys so the v1 backup encoding stays byte-identical: every shared
    /// field is listed; `foreignPlatformExtensions` is intentionally absent so it
    /// never appears in a v1 `.hwbackup` document.
    private enum CodingKeys: String, CodingKey {
        case id, name, preset, language, model, punctuation, capitalization
        case profanityFilter, customInstructions, languageModel, cloudProvider
        case cloudTranscriptionModel, postProcessingMode, postProcessingProvider
        case englishSpelling, userSystemPrompt, isDefault, sortOrder
        case cloudAccuracyTier, removeTrailingPeriod, geminiCustomPrompt
        case cloudPostProcessingModel, cloudTranscriptionDomain
    }

    /// Creates a BackupMode from a Core Data Mode entity
    init(from mode: Mode) {
        self.id = mode.id ?? UUID()
        self.name = mode.name ?? "Untitled"
        self.preset = mode.preset ?? "custom"
        self.language = mode.language ?? "en"
        self.model = mode.model ?? "base"
        self.punctuation = mode.punctuation
        self.capitalization = mode.capitalization
        self.profanityFilter = mode.profanityFilter
        self.customInstructions = mode.customInstructions
        self.languageModel = mode.languageModel
        self.cloudProvider = mode.cloudProvider
        self.cloudTranscriptionModel = mode.cloudTranscriptionModel
        self.postProcessingMode = mode.postProcessingMode
        self.postProcessingProvider = mode.postProcessingProvider
        self.englishSpelling = mode.englishSpelling
        self.userSystemPrompt = mode.userSystemPrompt
        self.isDefault = mode.isDefault
        self.sortOrder = mode.sortOrder
        self.cloudAccuracyTier = mode.cloudAccuracyTier
        self.removeTrailingPeriod = mode.removeTrailingPeriod
        self.geminiCustomPrompt = mode.geminiCustomPrompt
        self.cloudPostProcessingModel = mode.cloudPostProcessingModel
        self.cloudTranscriptionDomain = mode.cloudTranscriptionDomain
        self.foreignPlatformExtensions = mode.foreignPlatformExtensions
    }

    /// Memberwise initializer used by the universal-v2 import path (M3-D) to build a `BackupMode`
    /// from a `UniversalModeDTO`. Additive — the v1 `init(from:)` and Codable encoding are
    /// unchanged. Defaulted args let the call site omit fields that are absent in a v2 mode.
    init(
        id: UUID,
        name: String,
        preset: String,
        language: String,
        model: String,
        punctuation: Bool,
        capitalization: Bool,
        profanityFilter: Bool,
        customInstructions: String?,
        languageModel: String?,
        cloudProvider: String?,
        cloudTranscriptionModel: String?,
        postProcessingMode: Int16,
        postProcessingProvider: String?,
        englishSpelling: String?,
        userSystemPrompt: String?,
        isDefault: Bool,
        sortOrder: Int16,
        cloudAccuracyTier: String?,
        removeTrailingPeriod: Bool?,
        geminiCustomPrompt: String?,
        cloudPostProcessingModel: String?,
        cloudTranscriptionDomain: String?,
        foreignPlatformExtensions: String? = nil
    ) {
        self.id = id
        self.name = name
        self.preset = preset
        self.language = language
        self.model = model
        self.punctuation = punctuation
        self.capitalization = capitalization
        self.profanityFilter = profanityFilter
        self.customInstructions = customInstructions
        self.languageModel = languageModel
        self.cloudProvider = cloudProvider
        self.cloudTranscriptionModel = cloudTranscriptionModel
        self.postProcessingMode = postProcessingMode
        self.postProcessingProvider = postProcessingProvider
        self.englishSpelling = englishSpelling
        self.userSystemPrompt = userSystemPrompt
        self.isDefault = isDefault
        self.sortOrder = sortOrder
        self.cloudAccuracyTier = cloudAccuracyTier
        self.removeTrailingPeriod = removeTrailingPeriod
        self.geminiCustomPrompt = geminiCustomPrompt
        self.cloudPostProcessingModel = cloudPostProcessingModel
        self.cloudTranscriptionDomain = cloudTranscriptionDomain
        self.foreignPlatformExtensions = foreignPlatformExtensions
    }
}

/// Represents a Vocabulary entity for backup
struct BackupVocabularyItem: Codable {
    let id: UUID
    let word: String
    let replacement: String?
    let sortOrder: Int16
    let source: String?

    /// Creates a BackupVocabularyItem from a Core Data Vocabulary entity
    init(from vocabulary: Vocabulary) {
        self.id = vocabulary.id ?? UUID()
        self.word = vocabulary.word ?? ""
        self.replacement = vocabulary.replacement
        self.sortOrder = vocabulary.sortOrder
        self.source = vocabulary.value(forKey: "source") as? String
    }

    /// Direct initializer used when bridging a universal (v2) vocabulary item (parsed leniently)
    /// into the internal import shape so the existing `importVocabulary(_:resolution:)` merge path
    /// can be reused unchanged.
    init(id: UUID, word: String, replacement: String?, sortOrder: Int16, source: String?) {
        self.id = id
        self.word = word
        self.replacement = replacement
        self.sortOrder = sortOrder
        self.source = source
    }
}

// MARK: - API Keys Container

/// Container for API keys (all 9 supported providers)
/// NOTE: All fields are optional - only non-empty keys are included
struct BackupAPIKeys: Codable {
    let openai: String?
    let groq: String?
    /// Deprecated: Fireworks AI was removed as a provider. Field retained so old
    /// backups still decode without error; the value is read-and-ignored on restore.
    let fireworks: String?
    let anthropic: String?
    let gemini: String?
    let deepgram: String?
    let assemblyai: String?
    let elevenlabs: String?
    let mistral: String?
    let grok: String?

    /// Returns true if any API key is present
    var hasAnyKey: Bool {
        [openai, groq, fireworks, anthropic, gemini, deepgram, assemblyai, elevenlabs, mistral, grok]
            .compactMap { $0 }
            .contains { !$0.isEmpty }
    }
}

// MARK: - Import/Export Options

/// Options for export operation
struct ExportOptions {
    /// Include the settings section (ON by default — preserves prior whole-backup behavior)
    var includeSettings: Bool = true
    /// Include the modes section (ON by default)
    var includeModes: Bool = true
    /// Include the vocabulary section (ON by default)
    var includeVocabulary: Bool = true
    /// Include API keys in the backup (OFF by default for security)
    var includeAPIKeys: Bool = false
    /// Include license key in the backup (OFF by default)
    var includeLicenseKey: Bool = false

    /// True when ONLY vocabulary is selected — this is the cross-platform case that is
    /// written in the universal v2 `.hwbackup.json` format instead of the legacy v1 format.
    var isVocabularyOnly: Bool {
        includeVocabulary && !includeSettings && !includeModes && !includeAPIKeys && !includeLicenseKey
    }

    /// True when at least one section is selected (export is otherwise meaningless).
    var hasAnySelection: Bool {
        includeSettings || includeModes || includeVocabulary || includeAPIKeys || includeLicenseKey
    }
}

/// Options for import operation
struct ImportOptions {
    /// Whether to import the settings section (only applies if present in the file)
    var importSettings: Bool = true
    /// Whether to import the modes section (only applies if present in the file)
    var importModes: Bool = true
    /// Whether to import the vocabulary section (only applies if present in the file)
    var importVocabulary: Bool = true
    /// How to handle mode conflicts (by name, case-insensitive).
    /// Defaults to the non-destructive `.skip`; the import UI passes `.replace` explicitly.
    var modeConflict: ModeConflictResolution = .skip
    /// How to handle vocabulary conflicts (by word, case-insensitive)
    var vocabularyConflict: VocabularyConflictResolution = .skip
    /// Whether to import API keys from the backup
    var importAPIKeys: Bool = true
    /// Whether to import the license key from the backup
    var importLicenseKey: Bool = false
}

// MARK: - Conflict Resolution

/// How to handle mode conflicts during import
/// Modes are matched by name (case-insensitive)
enum ModeConflictResolution: String, CaseIterable, Identifiable {
    /// Don't import if mode with same name exists
    case skip
    /// Delete existing mode, import new one
    case replace
    /// Import as "Mode Name (imported)"
    case keepBoth

    var id: String { rawValue }

    var localizedName: String {
        switch self {
        case .skip:
            return NSLocalizedString("settings.backup.conflict.skip", value: "Skip existing", comment: "")
        case .replace:
            return NSLocalizedString("settings.backup.conflict.replace", value: "Replace existing", comment: "")
        case .keepBoth:
            return NSLocalizedString("settings.backup.conflict.keepBoth", value: "Keep both", comment: "")
        }
    }
}

/// How to handle vocabulary conflicts during import
/// Vocabulary items are matched by word (case-insensitive)
enum VocabularyConflictResolution: String, CaseIterable, Identifiable {
    /// Don't import if word already exists
    case skip
    /// Update existing item's replacement text
    case replace

    var id: String { rawValue }

    var localizedName: String {
        switch self {
        case .skip:
            return NSLocalizedString("settings.backup.conflict.skip", value: "Skip existing", comment: "")
        case .replace:
            return NSLocalizedString("settings.backup.conflict.replace", value: "Replace existing", comment: "")
        }
    }
}

// MARK: - File Inspection (auto-detect what a backup contains)

/// Which on-disk format a backup file uses.
enum BackupFileFormat {
    /// Legacy macOS v1 `BackupData` (may contain settings/modes/vocabulary).
    case legacyV1
    /// Universal v2 `.hwbackup.json` — macOS reads only its vocabulary section.
    case universalVocab
}

/// Descriptor of what sections a chosen backup file actually contains, computed by
/// pre-parsing the file before showing import options. Key-presence is the source of truth:
/// a section the file omits is reported as absent (and is disabled in the import UI).
struct BackupContents {
    let format: BackupFileFormat
    let hasSettings: Bool
    let hasModes: Bool
    let hasVocabulary: Bool
    let vocabularyCount: Int
    let hasAPIKeys: Bool
    let hasLicense: Bool
    let appVersion: String?
}

// MARK: - Validation & Results

/// Result of validating a backup file before import
/// Provides preview information without actually importing
struct BackupValidationResult {
    let isValid: Bool
    let version: Int?
    let exportDate: Date?
    let appVersion: String?
    let modeCount: Int
    let vocabularyCount: Int
    let hasAPIKeys: Bool
    let hasLicenseKey: Bool
    let errorMessage: String?

    /// Creates a successful validation result
    static func success(
        version: Int,
        exportDate: Date,
        appVersion: String,
        modeCount: Int,
        vocabularyCount: Int,
        hasAPIKeys: Bool,
        hasLicenseKey: Bool
    ) -> BackupValidationResult {
        BackupValidationResult(
            isValid: true,
            version: version,
            exportDate: exportDate,
            appVersion: appVersion,
            modeCount: modeCount,
            vocabularyCount: vocabularyCount,
            hasAPIKeys: hasAPIKeys,
            hasLicenseKey: hasLicenseKey,
            errorMessage: nil
        )
    }

    /// Creates a failed validation result
    static func failure(_ message: String) -> BackupValidationResult {
        BackupValidationResult(
            isValid: false,
            version: nil,
            exportDate: nil,
            appVersion: nil,
            modeCount: 0,
            vocabularyCount: 0,
            hasAPIKeys: false,
            hasLicenseKey: false,
            errorMessage: message
        )
    }
}

/// Result of an import operation
/// Contains statistics about what was imported
struct ImportResult {
    let success: Bool
    let modesImported: Int
    let modesSkipped: Int
    let vocabularyImported: Int
    let vocabularySkipped: Int
    let apiKeysImported: Bool
    let licenseKeyImported: Bool
    let errorMessage: String?

    /// Local-LLM model ids referenced by restored `.local` modes that are in the
    /// catalog but not yet downloaded on this (capable) Mac. When non-empty, the
    /// restore flow offers a batched "Download all" prompt. Empty on Intel/Rosetta.
    var pendingLocalDownloadModelIds: Set<String> = []

    /// Creates a successful import result
    static func success(
        modesImported: Int,
        modesSkipped: Int,
        vocabularyImported: Int,
        vocabularySkipped: Int,
        apiKeysImported: Bool = false,
        licenseKeyImported: Bool = false
    ) -> ImportResult {
        ImportResult(
            success: true,
            modesImported: modesImported,
            modesSkipped: modesSkipped,
            vocabularyImported: vocabularyImported,
            vocabularySkipped: vocabularySkipped,
            apiKeysImported: apiKeysImported,
            licenseKeyImported: licenseKeyImported,
            errorMessage: nil
        )
    }

    /// Creates a failed import result
    static func failure(_ message: String) -> ImportResult {
        ImportResult(
            success: false,
            modesImported: 0,
            modesSkipped: 0,
            vocabularyImported: 0,
            vocabularySkipped: 0,
            apiKeysImported: false,
            licenseKeyImported: false,
            errorMessage: message
        )
    }
}
