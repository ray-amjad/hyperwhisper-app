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
> code path on each side: **all three platforms now build the `settings` block in the Rust shared
> core's `hw-backup` adapter** (macOS through its 7→5 category map, Windows and Linux through the
> flat `(native, universal)` pairs tables below). Modes and vocabulary are still mapped natively on
> each head, apart from the shared cloud-routing normalization. macOS's full universal
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

**All three platforms build this block in the shared core** (`hw-backup` `mapping.rs`). macOS uses
the 7→5 category adapter above. Windows and Linux use flat adapters driven by `(native, universal)`
PAIRS tables, because their native settings are flat:

| Universal category | Windows table | Linux table |
|---|---|---|
| `general` | `WINDOWS_GENERAL_PAIRS` | `LINUX_GENERAL_PAIRS` |
| `textOutput` | `WINDOWS_TEXT_OUTPUT_PAIRS` | `LINUX_TEXT_OUTPUT_PAIRS` |
| `storage` | `WINDOWS_STORAGE_PAIRS` | `LINUX_STORAGE_PAIRS` |
| `streaming` | `WINDOWS_STREAMING_PAIRS` | `LINUX_STREAMING_PAIRS` |
| `advanced` | `WINDOWS_ADVANCED_PAIRS` | `LINUX_ADVANCED_PAIRS` |

Windows needs PAIRS rather than macOS's bare key list because its native names diverge
(`pasteResultText` ← `AutoPasteEnabled`, and all six `Streaming*`) and `settings.json` is
**PascalCase** — `SettingsService.Save()` uses a plain `JsonSerializerOptions` with no naming policy.
Windows' native values reach the core through `SettingsService.BuildBackupSettingsSnapshot()`, which
carries ONLY the promoted keys — never `RecordingsFolder`, `LastSelectedMicrophone` or any other
device-local value. The Linux tables are a near-identity map (`PortableSettingsService`'s dotted keys
ARE the universal keys) plus the export DEFAULTS, which is what makes an untouched Linux profile
export all 23 shared keys. **Adding a universal setting on Windows or Linux means adding a row to the
matching table**, not editing a native mapper.

| Universal Key | macOS Source | Windows Source (`WINDOWS_*_PAIRS` row → native) |
|---|---|---|
| `general.launchMinimized` | `GeneralSettingsManager.launchMinimized` | `WINDOWS_GENERAL_PAIRS` → `SettingsData.LaunchMinimized` |
| `general.showRecordingWindow` | `GeneralSettingsManager.showRecordingWindow` | `WINDOWS_GENERAL_PAIRS` → `SettingsData.ShowRecordingWindow` |
| `general.checkForUpdatesAutomatically` | `GeneralSettingsManager.checkForUpdatesAutomatically` | `WINDOWS_GENERAL_PAIRS` → `SettingsData.CheckForUpdatesAutomatically` |
| `general.enableErrorLogging` | `GeneralSettingsManager.enableErrorLogging` | `WINDOWS_GENERAL_PAIRS` → `SettingsData.EnableErrorLogging` |
| `general.shareAnonymousSpeedData` | `GeneralSettingsManager.shareAnonymousSpeedData` | `WINDOWS_GENERAL_PAIRS` → `SettingsData.ShareAnonymousSpeedData` |
| `general.enableSoundEffects` | `AudioSettingsManager.enableSoundEffects` | `WINDOWS_GENERAL_PAIRS` → `SettingsData.EnableSoundEffects` |
| `textOutput.pasteResultText` | `SettingsManager.pasteResultText` | `WINDOWS_TEXT_OUTPUT_PAIRS` → `SettingsData.AutoPasteEnabled` (the rename) |
| `textOutput.removeFillerWords` | `SettingsManager.removeFillerWords` | `WINDOWS_TEXT_OUTPUT_PAIRS` → `SettingsData.RemoveFillerWords` |
| `textOutput.restoreClipboardAfterPaste` | `SettingsManager.restoreClipboardAfterPaste` | `WINDOWS_TEXT_OUTPUT_PAIRS` → `SettingsData.RestoreClipboardAfterPaste` |
| `textOutput.hideFromClipboardHistory` | `SettingsManager.hideFromClipboardHistory` | `WINDOWS_TEXT_OUTPUT_PAIRS` → `SettingsData.HideFromClipboardHistory` |
| `textOutput.clipboardRestoreDelaySeconds` | `SettingsManager.clipboardRestoreDelaySeconds` | `WINDOWS_TEXT_OUTPUT_PAIRS` → `SettingsData.ClipboardRestoreDelaySeconds` (setter clamps to 1–60) |
| `textOutput.autocapitalizeInsert` | `SettingsManager.autocapitalizeInsert` | `WINDOWS_TEXT_OUTPUT_PAIRS` → `SettingsData.AutocapitalizeInsert` |
| `textOutput.storeWordTimestamps` | `SettingsManager.storeWordTimestamps` | **No pairs row and no native property, by design.** Preserved, not interpreted: `SettingsData.BackupUnknownSettings["textOutput"]["storeWordTimestamps"]` (see the unknown-key block below). Linux maps `PortableSettingsService` `textOutput.storeWordTimestamps` (local Whisper word/segment timestamps) |
| `storage.storeAsM4A` | `StorageSettingsManager.storeAsM4A` | `WINDOWS_STORAGE_PAIRS` → `SettingsData.StoreAsM4A` |
| `storage.keepAudioFiles` | `SettingsManager.keepAudioFiles` (macOS `advanced` category) | `WINDOWS_STORAGE_PAIRS` → `SettingsData.KeepAudioFiles` (default `true`). Windows PERSISTS and round-trips the value but does not yet act on it — retention on Windows runs off `autoDeleteEnabled` / `autoDeleteDaysOld`, and there is no Storage-page toggle. Before it existed the value was discarded outright |
| `advanced.maxRecordingDuration` | `SettingsManager.maxRecordingDurationSeconds` (seconds, 0 = no limit; macOS treats the value `300` — the old never-exposed default — as unset on import) | `WINDOWS_ADVANCED_PAIRS` → `SettingsData.MaxRecordingDuration`, in seconds. **A backup may TIGHTEN this cap and can never loosen it** — see the table below |
| `advanced.typingSpeedWPM` | — (HomeStatsBar `@AppStorage("homeStats.typingSpeedWPM")` — macOS keeps this device-local, not exported) | `WINDOWS_ADVANCED_PAIRS` → `SettingsData.TypingSpeedWPM` |

**`advanced.maxRecordingDuration` is a SAFETY limit, so it is the one key an imported file cannot set
freely.** Windows enforces a hard 20-minute ceiling (`MainViewModel.MaxRecordingDuration`), which is
also its default. The rules below are applied in the shared core
(`universal_to_windows_settings`, so all three bindings answer identically and
`shared-conformance/backup-vectors.json` can pin them) and again in
`SettingsService.MaxRecordingDurationSeconds`, so no writer — not even a hand-edited
`settings.json` — can raise the ceiling:

| Universal value (seconds) | Windows result |
|---|---|
| `300` — macOS's never-exposed legacy default | ABSENT: keep the live value, exactly as macOS does |
| `<= 0` — macOS's "no limit" | ABSENT: Windows has no "off"; an unbounded recording is the failure mode the guard exists for |
| `1`–`1200` | applied verbatim — TIGHTENING the cap is always allowed |
| `> 1200` (Linux and macOS both default to `3600`) | clamped to `1200` |

The universal `streaming` block is Windows- and Linux-only today — macOS does not export it. Its six
Windows native properties are all separately named, which is why `WINDOWS_STREAMING_PAIRS` exists.
`StreamingShortcut` is a `KeyboardShortcut`, not a scalar: it crosses as its `ToPersistedString()`
form, and `FromPersistedString` stays native.

| Universal Key | Windows Source (`WINDOWS_STREAMING_PAIRS` row → native) | Linux Source |
|---|---|---|
| `streaming.enabled` | `SettingsData.StreamingEnabled` | `streaming.enabled` |
| `streaming.provider` | `SettingsData.StreamingProvider` (setter falls back to `hyperwhisperCloud` for an unknown value) | `streaming.provider` (stored verbatim) |
| `streaming.language` | `SettingsData.StreamingLanguage` | `streaming.language` (stored verbatim) |
| `streaming.deepgramModel` | `SettingsData.StreamingDeepgramModel` (setter collapses anything but `nova-3-medical` to `nova-3-general`) | `streaming.deepgramModel` (stored verbatim) |
| `streaming.fastFormatting` | `SettingsData.StreamingFastFormatting` | `streaming.fastFormatting` |
| `streaming.shortcut` | `SettingsData.StreamingShortcut` (persisted string; setter re-canonicalises it) | `streaming.shortcut` (stored verbatim) |

The core renames and regroups; it never interprets a value. Every rewrite in the Windows column above
happens in a `SettingsService` setter on import, exactly where it did before — which is why the same
universal blob restores differently on Windows and on Linux.

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

Linux-only settings go into `platformExtensions.linux.settings` (including `themeMode` with `system`, `light`, or `dark`, and `minimizeToTray`):

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
| `soundEffectsVolume` | Linux recording cue volume | Number clamped to 0–1; Linux also accepts the equivalent macOS audio-extension value when importing a macOS backup. |

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

**Foreign-slice passthrough, TOP LEVEL (all platforms).** The same rule applies to the backup's
TOP-LEVEL `platformExtensions` map, not just the per-mode ones: on import each platform preserves
every *other* platform's top-level slice and re-emits it on the next export, and its OWN slice —
rebuilt from live settings — always overwrites a stale preserved copy. Until issue #288 this worked
on **Linux only**: Windows' `BuildPlatformExtensions` returned `{"windows": …}` and nothing else, and
macOS's `encodeBackupV2` built the map purely from the Rust core's settings record, which only ever
holds `macos`. Both now preserve.

| Platform | Storage | What it holds | Merged back in |
|---|---|---|---|
| macOS | `UserDefaults` key `backup.foreignPlatformExtensions` (raw JSON) | the non-`macos` slices only | `BackupManager.mergingForeignTopLevelExtensions(into:stored:)`, called from `encodeBackupV2` |
| Windows | `SettingsData.BackupForeignPlatformExtensions` (raw JSON, `settings.json`) | the non-`windows` slices only | `UniversalBackupMapper.BuildPlatformExtensions` |
| Linux | the `backup.platformExtensions` setting (`PortableSettingsService`) | the WHOLE imported map, including `linux` | `ApplicationBackupExport`, which overwrites the `linux` slice on the way out |

The two storage strategies differ and that is fine — the observable contract is identical, and
`shared-conformance/backup-vectors.json`'s `unknownKeyRoundTrip` rows record both. Capture is a
REPLACE, never a merge, on every head: the store describes the LAST IMPORTED file, so a backup with
no foreign slice CLEARS it. Merging would re-publish a slice from an unrelated file under a different
user's export. macOS's `deepMerged(over:)` / `currentSettingsBaseline()` is a whole-blob apply of the
seven macOS settings categories and knows nothing about this field — do not route it through there.

**Unknown-key fidelity.** The shared core preserves any unknown top-level / settings-category /
mode / vocabulary key verbatim through a parse → re-serialize round-trip (serde `flatten`), so a
backup written by a newer build does not lose data when re-exported by an older one. That is the
CORE's guarantee; each head then needs somewhere to keep the keys between an import and the next
export, because the DTO is discarded at the end of the import.

On Windows that store is **`SettingsData.BackupUnknownSettings`** (raw JSON), a MIRROR of the
universal `settings` tree so every key keeps its SECTION:

```json
{ "textOutput": { "storeWordTimestamps": true }, "someFutureSection": { "a": 1 } }
```

A key unknown inside a known section nests under that section's name; a whole unknown section sits at
the top of the same object. That is unambiguous, because the five known section names are declared
properties and therefore never reach the settings-root extension bag. On export the blob is
re-attached to the typed DTOs' `[JsonExtensionData]` bags, so `System.Text.Json` emits each key at
exactly its original nesting level — `textOutput.storeWordTimestamps` comes back **under
`textOutput`, never at the document root**, which would be a schema violation. Unknown TOP-LEVEL keys
ride separately in `SettingsData.BackupUnknownRootKeys`; `platformExtensions` is never among them
because it has its own store above. **These are three separate fields with three shapes and three
merge points — do not collapse them.**

Two known gaps, named rather than papered over:

- **Windows mode / vocabulary unknown keys are NOT persisted.** `UniversalMode` and
  `UniversalVocabularyItem` carry the `[JsonExtensionData]` bag, so an unknown key survives the DTO,
  but their homes are EF entities (`Data/Entities/Mode.cs`, `VocabularyItem`) and a store would need
  a real EF migration — out of scope for #288. Settings keys, which is where
  `storeWordTimestamps` lives, are fully covered.
- **Linux drops unknown SETTINGS keys.** `ApplicationBackupExport.ApplySharedSettings` →
  `CopyCategory` is a per-category ALLOWLIST, so an unknown key inside a known category and a whole
  unknown category are both dropped on import. Linux's top-level `platformExtensions` passthrough is
  unaffected. Fixing this was not in #288's scope.
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
| Shared core | `shared-core-rs/crates/hw-backup` — universal⇄records mapping, all three settings adapters (macOS 7→5 categories; Windows and Linux flat pairs tables), lossless `extra` passthrough, and structural validation | `crates/hw-backup/src/records.rs` |

The shared core is sans-I/O: it parses, maps, and validates in memory; each platform owns reading and writing the `.hwbackup.json` bytes and persisting the resulting records.
</important>

<important if="you are changing a settings mapping table, the mode normalizer, or backup-vectors.json">

`shared-conformance/backup-vectors.json` is the golden conformance file for this schema, and FOUR
suites read it. Change one head and the other three fail, which is the point.

| Suite | Runs | CI job |
|---|---|---|
| `shared-core-rs/crates/hw-core/tests/backup_vectors.rs` | every group except `unknownKeyRoundTrip`, straight off the FFI with no app around it | Shared Core Tests, Linux CI |
| `app/macos/hyperwhisperTests/BackupConformanceVectorTests.swift` | `macosSettings` (macOS's own adapter) and `modeNormalization` (a binding check) | macOS CI |
| `app/macos/hyperwhisperTests/BackupTopLevelExtensionsTests.swift` | the `macos` rows of `unknownKeyRoundTrip` | macOS CI |
| `app/shared-dotnet/HyperWhisper.Backup.Application.Tests` | the Linux adapters, `macosSettings`, and the `linux` rows of `unknownKeyRoundTrip` | Linux CI, Windows CI |
| `app/windows/HyperWhisper.SmokeTests` | `windowsSettings`, `modeNormalization` and the `windows` rows of `unknownKeyRoundTrip`, over the real `SettingsService` | Windows CI |

Rules for the file itself:

- The 132 `modeNormalization` rows are FROZEN. A row may be ADDED; an existing one may not move
  without a deliberate behaviour change, and two suites assert the count.
- A row pins VALUES, never JSON formatting. Every suite compares number-representation-insensitively,
  so write `10` and not `10.0`.
- A `modeNormalization` row must carry ONE `expected`. The per-head `expectedWindows` /
  `expectedLinux` shape recorded a drift that phase 1b removed; re-introducing it is rejected.
- `setterRewrittenNativeKeys` exempts a `windowsSettings` import key's VALUE from the core-only
  suites, because the `SettingsService` setters own the final answer. It never exempts the key's
  presence. Add one only with the setter that justifies it.
</important>
