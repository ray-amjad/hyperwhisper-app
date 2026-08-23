# Cross-Platform Backup Schema

This folder defines the universal backup format for HyperWhisper. macOS and Windows use this schema as the live contract for cross-platform backup compatibility; the Linux port uses the same contract and is represented by a checked-in fixture while its platform importer is implemented.

## Overview

- **Schema file**: `hyperwhisper-backup.schema.json` (JSON Schema Draft 2020-12)
- **Format**: Single `.hwbackup.json` plain JSON file
- **Schema version**: `2` (version 1 = legacy macOS-only format)
- **Examples**: `examples/` folder has sample exports from each platform

## How It Works

Both platforms export to the same universal JSON format. On import, each platform:
1. Detects the format (ZIP = legacy Windows `.hwbackup`, JSON with `version` = legacy macOS, JSON with `schemaVersion` = universal)
2. Reads shared fields (modes, vocabulary, settings, API keys)
3. Reads `platformExtensions.<thisPlatform>` for platform-specific fields
4. Ignores unknown `platformExtensions` from the other platform but **preserves them** for round-trip fidelity

**Shared fields** live at the top level of each object (mode, vocabulary, settings) and are portable between platforms. **Platform-specific fields** go into `platformExtensions.<platform>` at each level. When one platform imports the other's backup, it reads the shared fields, ignores the other platform's extension slice, and writes that slice back out unchanged on the next export. See `examples/` for exact shapes.

> **Current cross-platform reality (as of 2026-06).** BOTH platforms now read/write the **full**
> universal v2 format — settings, modes, and vocabulary. The mapping tables below describe a live
> code path on each side (macOS routes its 7 native settings categories through the Rust shared
> core's `hw-backup` adapter; Windows maps its EF Core entities directly). macOS's full universal
> export/import ships behind the `backup.useUniversalV2Export` flag (default OFF) while it beds in; a
> **vocabulary-only** `.hwbackup.json` (only the `vocabulary` key present — see
> `examples/vocab-only.hwbackup.json`) remains the always-on, flag-independent interchange unit, and
> on import each platform merges words by `word` (case-insensitive — never a wipe).

<important if="you are adding or modifying a setting, or editing the settings field-mapping tables">

Settings use a grouped structure. Platform-specific settings go into `platformExtensions.<platform>.settings`.

The macOS extension blob is **category-keyed**: macOS-only settings are nested under
`platformExtensions.macos.settings.{audio,general,storage,advanced,shortcuts,aiModel}`, each holding
only that category's macOS-only keys (the promoted cross-platform keys below are excluded). On import
every key routes home by its recorded category — there is no per-key allowlist, so a NEW macOS-only
key automatically round-trips into the correct category instead of drifting. See
`examples/macos-export.hwbackup.json` for the exact shape, and `shared-core-rs/crates/hw-backup`
(`mapping.rs`: `macos_settings_to_universal` / `universal_to_macos_settings`) for the adapter.

| Universal Key | macOS Source | Windows Source |
|---|---|---|
| `general.launchMinimized` | `GeneralSettingsManager.launchMinimized` | `SettingsData.LaunchMinimized` |
| `general.showRecordingWindow` | `GeneralSettingsManager.showRecordingWindow` | `SettingsData.ShowRecordingWindow` |
| `general.checkForUpdatesAutomatically` | `GeneralSettingsManager.checkForUpdatesAutomatically` | `SettingsData.CheckForUpdatesAutomatically` |
| `general.enableErrorLogging` | `GeneralSettingsManager.enableErrorLogging` | `SettingsData.EnableErrorLogging` |
| `general.shareAnonymousSpeedData` | `GeneralSettingsManager.shareAnonymousSpeedData` | `SettingsData.ShareAnonymousSpeedData` |
| `general.enableSoundEffects` | `AudioSettingsManager.enableSoundEffects` | `SettingsData.EnableSoundEffects` |
| `textOutput.pasteResultText` | `SettingsManager.pasteResultText` | `SettingsData.AutoPasteEnabled` |
| `textOutput.removeFillerWords` | `SettingsManager.removeFillerWords` | `SettingsData.RemoveFillerWords` |
| `textOutput.restoreClipboardAfterPaste` | `SettingsManager.restoreClipboardAfterPaste` | `SettingsData.RestoreClipboardAfterPaste` |
| `textOutput.hideFromClipboardHistory` | `SettingsManager.hideFromClipboardHistory` | `SettingsData.HideFromClipboardHistory` |
| `textOutput.clipboardRestoreDelaySeconds` | `SettingsManager.clipboardRestoreDelaySeconds` | `SettingsData.ClipboardRestoreDelaySeconds` |
| `textOutput.autocapitalizeInsert` | `SettingsManager.autocapitalizeInsert` | `SettingsData.AutocapitalizeInsert` |
| `textOutput.storeWordTimestamps` | `SettingsManager.storeWordTimestamps` | — (local Whisper word/segment timestamps; macOS-only) |
| `storage.storeAsM4A` | `StorageSettingsManager.storeAsM4A` | `SettingsData.StoreAsM4A` |
| `advanced.maxRecordingDuration` | `SettingsManager.maxRecordingDurationSeconds` (seconds, 0 = no limit; macOS treats the value `300` — the old never-exposed default — as unset on import) | `SettingsData.MaxRecordingDuration` |
| `advanced.typingSpeedWPM` | — (HomeStatsBar `@AppStorage("homeStats.typingSpeedWPM")` — macOS keeps this device-local, not exported) | `SettingsData.TypingSpeedWPM` |

macOS-only shortcut settings live under `platformExtensions.macos.settings.shortcuts` (the
category-keyed extension above) and round-trip losslessly through the universal v2 adapter:

| Extension Key (`platformExtensions.macos.settings.shortcuts.*`) | macOS Source | Windows Source |
|---|---|---|
| `pushToTalkMode` | `SettingsManager.pushToTalkMode` (raw value) | — |
| `pushToTalkDoublePressEnabled` | `SettingsManager.pushToTalkDoublePressEnabled` | — |
| `quickCaptureEnabled` | `SettingsManager.quickCaptureEnabled` | — |
| `quickCaptureModeId` | `SettingsManager.quickCaptureModeId` (UUID string, `""` = current mode) | — |

Windows-only settings (go into `platformExtensions.windows.settings`; not yet
shared at the top level). `autoIncreaseMicVolume` is also round-tripped by macOS
under `platformExtensions.macos.settings`:

| Key | Windows Source | Notes |
|---|---|---|
| `autoIncreaseMicVolume` | `SettingsData.AutoIncreaseMicVolume` | Bool; macOS also round-trips this key |
| `autocapitalizeInsert` | `SettingsData.AutocapitalizeInsert` | Bool |
| `customEndpoints` | `SettingsData.CustomEndpoints` | Array of custom OpenAI-compatible endpoints (`id`, `name`, `endpointURL`, `modelName`, …). Required so modes whose `postProcessingProvider` is `custom:<uuid>` resolve after restore. API keys are stored separately in Credential Manager and are NOT round-tripped. |

Linux-only settings go into `platformExtensions.linux.settings`:

| Key | Linux Source | Notes |
|---|---|---|
| `localWhisperBackend` | Linux local Whisper backend preference | `auto`, `cpu`, `vulkan`, or `cuda12`; process-wide selection takes effect after restart. |
| `allowLocalWhisperCpuFallback` | Linux local Whisper fallback preference | Bool; permits CPU when the selected GPU runtime cannot start. |
| `autostartEnabled` | Linux autostart preference | Bool; controls the per-user XDG autostart entry. |
| `toggleShortcutModifiers` | Linux transcription shortcut modifiers | `ShortcutModifiers` names, comma separated. |
| `toggleShortcutKey` | Linux transcription shortcut key | Stable portable key name. |
| `cancelShortcutModifiers` | Linux active-session cancellation modifiers | `None` for the default session-scoped Escape binding. |
| `cancelShortcutKey` | Linux active-session cancellation key | Stable portable key name; registered only while recording. |
| `changeModeShortcutModifiers` | Linux mode-cycle shortcut modifiers | `ShortcutModifiers` names, comma separated. |
| `changeModeShortcutKey` | Linux mode-cycle shortcut key | Stable portable key name. |
| `streamingShortcutModifiers` | Linux dedicated live-transcription modifiers | `ShortcutModifiers` names, comma separated. |
| `streamingShortcutKey` | Linux dedicated live-transcription key | Stable portable key name. |
| `pushToTalkMode` | Linux push-to-talk mode | `Disabled`, `Modifier`, or `CustomShortcut`. |
| `pushToTalkModifier` | Linux modifier-only push-to-talk input | Stable `ModifierSide` name. |
| `pushToTalkShortcutModifiers` | Linux custom push-to-talk modifiers | `ShortcutModifiers` names, comma separated. |
| `pushToTalkShortcutKey` | Linux custom push-to-talk key | Stable portable key name. |
| `pushToTalkDoublePressLock` | Linux push-to-talk latch preference | Bool. |
| `autoIncreaseMicVolume` | Linux temporary microphone boost | Bool; restored after every recording. |
| `keepMicrophoneWarm` | Linux microphone keep-warm preference | Bool. |
| `audioEnvironmentPolicy` | Linux other-audio behavior | `unchanged`, `duck`, or `mute`. |
| `autoDeleteEnabled` | Linux recording-history retention preference | Bool; enables startup/hourly transcript and app-owned audio cleanup. |
| `autoDeleteDaysOld` | Linux recording-history retention age | Integer clamped to 1–365 days. |
| `customEndpoints` | Linux custom post-processing endpoints | Windows-compatible array of `id`, `name`, `endpointURL`, and `modelName`; credentials remain in secure storage and are never exported. |

The Linux `selectedModeId` setting is intentionally device-local and is not exported: a
mode selection is transient UI state, and importing it could silently change the active
recording workflow on another device.
</important>

<important if="you are adding or modifying a Mode property, or editing the mode field-mapping tables">

Shared mode fields (top-level in the schema):

| Field | macOS (Core Data) | Windows (EF Core) |
|---|---|---|
| `id` | `Mode.id` (UUID) | `Mode.Id` (Guid) |
| `name` | `Mode.name` | `Mode.Name` |
| `preset` | `Mode.preset` | `Mode.Preset` |
| `language` | `Mode.language` | `Mode.Language` |
| `model` | `Mode.model` | `Mode.Model` |
| `isDefault` | `Mode.isDefault` | `Mode.IsDefault` |
| `sortOrder` | `Mode.sortOrder` (Int16) | `Mode.SortOrder` (int) |
| `punctuation` | `Mode.punctuation` | `Mode.Punctuation` |
| `capitalization` | `Mode.capitalization` | `Mode.Capitalization` |
| `profanityFilter` | `Mode.profanityFilter` | `Mode.ProfanityFilter` |
| `removeTrailingPeriod` | `Mode.removeTrailingPeriod` | `Mode.RemoveTrailingPeriod` |
| `englishSpelling` | `Mode.englishSpelling` | `Mode.EnglishSpelling` |
| `cloudProvider` | `Mode.cloudProvider` | `Mode.CloudProvider` |
| `cloudTranscriptionModel` | `Mode.cloudTranscriptionModel` | `Mode.CloudTranscriptionModel` |
| `cloudTranscriptionDomain` | `Mode.cloudTranscriptionDomain` | `Mode.CloudTranscriptionDomain` |
| `postProcessingMode` | `Mode.postProcessingMode` (Int16) | `Mode.PostProcessingMode` (int) |
| `postProcessingProvider` | `Mode.postProcessingProvider` | `Mode.PostProcessingProvider` |
| `languageModel` | `Mode.languageModel` | `Mode.LanguageModel` |
| `userSystemPrompt` | `Mode.userSystemPrompt` | `Mode.UserSystemPrompt` |
| `customInstructions` | `Mode.customInstructions` | `Mode.CustomInstructions` |
| `geminiCustomPrompt` | `Mode.geminiCustomPrompt` | `Mode.GeminiCustomPrompt` |
| `cloudPostProcessingModel` | `Mode.cloudPostProcessingModel` | `Mode.CloudPostProcessingModel` |

Windows-only mode fields (go into `platformExtensions.windows`):

| Field | Windows Property | Default on Import |
|---|---|---|
| `modelType` | `Mode.ModelType` | Same as `model` |
| `localEngine` | `Mode.LocalEngine` | `"whisper"` |
| `localParakeetModel` | `Mode.LocalParakeetModel` | `null` |
| `providerType` | `Mode.ProviderType` | Infer from `cloudProvider` |
| `cloudAccuracyTier` | `Mode.CloudAccuracyTier` | `"High"` |
| `enableScreenOCR` | `Mode.EnableScreenOCR` | `false` |
| `customVocabulary` | `Mode.CustomVocabulary` | `null` |
| `isSystemProvided` | `Mode.IsSystemProvided` | `false` |
| `createdDate` | `Mode.CreatedDate` | Current UTC time |
| `modifiedDate` | `Mode.ModifiedDate` | Current UTC time |
</important>

<important if="you are changing per-mode platformExtensions, foreign-slice retention, or unknown-key round-trip behavior">

**Foreign-slice passthrough (all platforms).** On import, each platform captures every *other*
platform's per-mode `platformExtensions` slice and persists it, then re-emits it on the next export
— so a Windows mode's `platformExtensions.windows` survives a macOS round-trip, and a macOS slice
survives a Windows round-trip. Linux slices obey the same rule. Storage: macOS
`Mode.foreignPlatformExtensions` (Core Data, raw JSON); Windows and the shared C# Linux core use
`Mode.ForeignPlatformExtensions` (EF Core, raw JSON column). Each platform's own slice always wins
over a stale preserved copy on re-export. A mac→v2→Windows→v2→mac trip retains the `windows` mode
slice, and Linux-authored slices must likewise survive trips through either existing platform.

**Unknown-key fidelity.** The shared core preserves any unknown top-level / settings-category /
mode / vocabulary key verbatim through a parse → re-serialize round-trip (serde `flatten`), so a
backup written by a newer build does not lose data when re-exported by an older one.
</important>

<important if="you are adding or changing an API key provider">

API keys are a flat object with lowercase provider-name keys. Both platforms map to their native secure storage (macOS Keychain, Windows SettingsService). Unknown keys are silently ignored on import.
</important>

<important if="you are adding or changing a field on Mode, VocabularyItem, settings, or an API key provider on either platform">

**You MUST update this schema when:**
- Adding a new property to `Mode` entity on either platform (add to shared fields if cross-platform, or to the platform extensions table)
- Adding a new property to `VocabularyItem` on either platform
- Adding a new setting that exists on both platforms
- Adding a new API key provider
- Changing the type or semantics of an existing shared field

**Update checklist:**
1. Update `hyperwhisper-backup.schema.json` with the new field
2. Update the field mapping tables above
3. Update example files in `examples/`
4. Update the platform-specific import code to handle the new field (with a sensible default for imports from the other platform)
</important>

<important if="you need to edit backup import/export code on either platform">

| Platform | Export/Import Service | Backup Models |
|---|---|---|
| macOS | `app/macos/hyperwhisper/Managers/BackupManager.swift` | `app/macos/hyperwhisper/Models/BackupModels.swift` |
| Windows | `app/windows/HyperWhisper/Services/BackupService.cs` + `Services/UniversalBackupMapper.cs` | `app/windows/HyperWhisper/Models/UniversalBackupModels.cs` |
| Linux | Shared C# backup service/mapper (during the Linux port) | Shared C# universal backup models |
| Shared core | `shared-core-rs/crates/hw-backup` — universal⇄records mapping, the macOS 7→5 settings adapter, lossless `extra` passthrough, and structural validation | `crates/hw-backup/src/records.rs` |

The shared core is sans-I/O: it parses, maps, and validates in memory; each platform owns reading and writing the `.hwbackup.json` bytes and persisting the resulting records.
</important>
