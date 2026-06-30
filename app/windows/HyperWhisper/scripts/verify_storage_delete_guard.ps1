param()

$ErrorActionPreference = "Stop"

function Assert-Match {
    param(
        [Parameter(Mandatory = $true)][string]$Content,
        [Parameter(Mandatory = $true)][string]$Pattern,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if (-not [regex]::IsMatch($Content, $Pattern, [System.Text.RegularExpressions.RegexOptions]::Singleline)) {
        throw "Missing expected storage delete guard wiring: $Label"
    }
}

$ProjectRoot = Split-Path -Parent $PSScriptRoot
$RepoRoot = Resolve-Path (Join-Path $ProjectRoot "..\..\..")

$HistorySource = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "Services\HistoryService.cs")
$AutoDeleteSource = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "Services\AutoDeleteService.cs")
$SettingsSource = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "Services\SettingsService.cs")
$StorageSource = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "Services\StorageService.cs")
$AppPathsSource = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "Services\AppPaths.cs")
$MacPersistenceSource = Get-Content -Raw -LiteralPath (Join-Path $RepoRoot "app\macos\hyperwhisper\CoreData\PersistenceController.swift")

Assert-Match `
    -Content $HistorySource `
    -Pattern "public void DeleteAudioFile\(string\? path\).*?!IsDeletableAudioPath\(path\).*?Skipping audio deletion outside trusted recording roots.*?File\.Exists\(path\).*?File\.Delete\(path\)" `
    -Label "manual/history audio deletion blocks untrusted transcript paths before File.Delete"

Assert-Match `
    -Content $HistorySource `
    -Pattern "public static bool IsTrustedAudioPath\(string\? path\) => IsTrustedAudioPath\(path, resolveTrustedRoot: null\).*?internal static bool IsTrustedAudioPath\(string\? path, Func<string, string>\? resolveTrustedRoot\).*?Path\.GetFullPath\(path\).*?IsTempRecordingFallbackFile\(fullPath\).*?GetTrustedAudioRoots\(\).*?IsPathUnderDirectory\(fullPath, root\).*?IsPathUnderResolvedDirectory\(fullPath, root, resolveTrustedRoot\)" `
    -Label "trusted-path guard normalizes paths and checks them against trusted roots or exact temp fallback recording files"

Assert-Match `
    -Content $HistorySource `
    -Pattern "public static bool IsDeletableAudioPath\(string\? path\) => IsTrustedAudioPath\(path\)" `
    -Label "IsDeletableAudioPath is a thin delegate to IsTrustedAudioPath so deletion and /transcribe share one implementation"

Assert-Match `
    -Content $HistorySource `
    -Pattern "IsPathUnderResolvedDirectory\(.*?Func<string, string>\? resolveTrustedRoot\).*?resolveTrustedRoot\(Path\.GetFullPath\(directory\)\).*?IsPathUnderDirectory\(fullPath, resolvedDirectory\)" `
    -Label "trusted-path guard can compare opened real paths against reparsed trusted recording roots"

Assert-Match `
    -Content $HistorySource `
    -Pattern "GetTrustedAudioRoots\(\).*?StorageService\.Instance\.GetRecordingsFolder\(\).*?SettingsService\.GetLegacyAudioFolder\(\).*?GetTempRecordingsRoot\(\)" `
    -Label "trusted roots include active recordings, legacy audio, and the active HyperWhisper temp recordings folder"

Assert-Match `
    -Content $HistorySource `
    -Pattern "GetTempRecordingsRoot\(\).*?AppPaths\.IsAppDataRootOverridden.*?AppPaths\.ProfileTempRecordingsDirectory.*?Path\.Combine\(Path\.GetTempPath\(\), `"HyperWhisper`", `"recordings`"\)" `
    -Label "temp recordings trust root follows the isolated profile override and preserves production temp fallback behavior"

Assert-Match `
    -Content $HistorySource `
    -Pattern "IsTempRecordingFallbackFile\(string fullPath\).*?Path\.GetFullPath\(Path\.GetTempPath\(\)\).*?Path\.GetFileName\(fullPath\).*?Path\.GetExtension\(fullPath\).*?Path\.GetDirectoryName\(fullPath\).*?StartsWith\(`"hyperwhisper_`".*?`"\.wav`"" `
    -Label "delete guard permits only exact top-level HyperWhisper temp WAV fallback files, not the whole temp directory"

Assert-Match `
    -Content $HistorySource `
    -Pattern "IsPathUnderDirectory\(string fullPath, string directory\).*?Path\.TrimEndingDirectorySeparator\(fullDirectory\) \+ Path\.DirectorySeparatorChar.*?StartsWith\(directoryWithSeparator, StringComparison\.OrdinalIgnoreCase\)" `
    -Label "directory-boundary check prevents sibling-prefix deletion"

Assert-Match `
    -Content $HistorySource `
    -Pattern "DeleteTranscriptAudioFiles\(Transcript transcript\).*?transcript\.AudioFilePath, transcript\.TrimmedAudioFilePath.*?Distinct\(StringComparer\.OrdinalIgnoreCase\).*?DeleteAudioFile\(path\)" `
    -Label "original and VAD-trimmed audio deletion both route through the guarded helper"

Assert-Match `
    -Content $AutoDeleteSource `
    -Pattern "filesDeleted = transcriptsToDelete.*?AudioFilePath, t\.TrimmedAudioFilePath.*?Distinct\(StringComparer\.OrdinalIgnoreCase\).*?Where\(HistoryService\.IsDeletableAudioPath\).*?Count\(File\.Exists\)" `
    -Label "auto-delete stats count only trusted/deletable audio files"

Assert-Match `
    -Content $SettingsSource `
    -Pattern "internal static string GetLegacyAudioFolder\(\).*?AppPaths\.LegacyAudioDirectory" `
    -Label "legacy Windows audio root is centralized and reusable"

Assert-Match `
    -Content $AppPathsSource `
    -Pattern "LegacyAudioDirectory.*?Path\.Combine\(AppDataRoot, `"Audio`"\)" `
    -Label "legacy Windows audio root stays under the active app-data profile"

Assert-Match `
    -Content $StorageSource `
    -Pattern "Path\.Combine\(.*?Path\.GetTempPath\(\).*?`"HyperWhisper`".*?`"recordings`".*?\).*?GetFallbackFolders\(\).*?_tempRecordingFolder" `
    -Label "HyperWhisper temp recordings root matches StorageService fallback"

Assert-Match `
    -Content $MacPersistenceSource `
    -Pattern "deleteTranscript\(.*?audioFilePath.*?FileManager\.default\.removeItem.*?trimmedAudioFilePath.*?FileManager\.default\.removeItem" `
    -Label "macOS comparison currently deletes stored transcript audio paths directly"

Write-Host "Storage delete guard verifier passed."
