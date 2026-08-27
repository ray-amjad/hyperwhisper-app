param()

$ErrorActionPreference = "Stop"

function Assert-Match {
    param(
        [string] $Content,
        [string] $Pattern,
        [string] $Message
    )

    if (-not [regex]::IsMatch($Content, $Pattern, [System.Text.RegularExpressions.RegexOptions]::Singleline)) {
        throw $Message
    }
}

function Assert-NotMatch {
    param(
        [string] $Content,
        [string] $Pattern,
        [string] $Message
    )

    if ([regex]::IsMatch($Content, $Pattern, [System.Text.RegularExpressions.RegexOptions]::Singleline)) {
        throw $Message
    }
}

$ProjectRoot = Split-Path -Parent $PSScriptRoot
$RepoRoot = Resolve-Path (Join-Path $ProjectRoot "..\..\..")

$WindowsViewModel = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "ViewModels\MainViewModel.cs")
$WindowsSettings = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "Services\SettingsService.cs")
$WindowsStrings = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "Resources\Strings.resx")
$RustMapping = Get-Content -Raw -LiteralPath (Join-Path $RepoRoot "shared-core-rs\crates\hw-backup\src\mapping.rs")
$MacFlow = Get-Content -Raw -LiteralPath (Join-Path $RepoRoot "app\macos\hyperwhisper\Managers\AudioRecording\RecordingFlow\RecordingTranscriptionFlow.swift")
$MacStart = Get-Content -Raw -LiteralPath (Join-Path $RepoRoot "app\macos\hyperwhisper\Managers\AudioRecording\RecordingFlow\RecordingTranscriptionFlow+StartRecording.swift")
$MacStop = Get-Content -Raw -LiteralPath (Join-Path $RepoRoot "app\macos\hyperwhisper\Managers\AudioRecording\RecordingFlow\RecordingTranscriptionFlow+StopRecording.swift")
$MacStreaming = Get-Content -Raw -LiteralPath (Join-Path $RepoRoot "app\macos\hyperwhisper\Managers\AudioRecording\RecordingFlow\RecordingTranscriptionFlow+Streaming.swift")

# macOS's cap is user-configurable (SettingsManager.maxRecordingDurationSeconds,
# default 1 hour, 0 = off) — the assertions below verify the setting-driven timer
# still auto-stops into transcription, not a hardcoded duration.
Assert-Match `
    -Content $MacFlow `
    -Pattern "var recordingMaxDurationTimer: Timer\?" `
    -Message "macOS must define a batch recording safety timer."

Assert-Match `
    -Content $MacStart `
    -Pattern "armRecordingMaxDurationTimer\(mode: mode, attemptId: attemptId\).*?maxRecordingDurationInterval.*?Timer\.scheduledTimer\(withTimeInterval: maxDuration.*?currentRecordingTriggerSource = \.autoStop.*?handleStopRecordingWithTranscription\(mode: mode, cancelled: false\)" `
    -Message "macOS batch recording must arm the user-configured cap and auto-stop into transcription, not cancellation."

Assert-Match `
    -Content $MacStop `
    -Pattern "recordingMaxDurationTimer\?\.invalidate\(\).*?recordingMaxDurationTimer = nil.*?recordingLifecycle\.stopRecording\(cancelled: cancelled\)" `
    -Message "macOS batch stop must clear the runaway timer before finalizing the recording."

Assert-Match `
    -Content $MacStop `
    -Pattern "guard !isStopInProgress else.*?Stop recording ignored because another stop is already in progress.*?return.*?isStopInProgress = true.*?defer \{ isStopInProgress = false \}" `
    -Message "macOS stop handler must acquire the stop latch at entry so queued manual and timer stops cannot double-finalize."

Assert-Match `
    -Content $MacStreaming `
    -Pattern "Timer\.scheduledTimer\(withTimeInterval: maxDuration.*?!self\.isStopInProgress.*?self\.isStopInProgress = true.*?streaming\.maxDuration\.reachedToast.*?currentRecordingTriggerSource = \.autoStop.*?stopStreamingTranscription\(mode: modeName\)" `
    -Message "macOS streaming must use the same user-configured cap, claim the stop latch, and stop normally."

Assert-NotMatch `
    -Content $MacStreaming `
    -Pattern "withTimeInterval: 3600|withTimeInterval: 20 \* 60|withTimeInterval: Self\.maxRecordingDuration" `
    -Message "macOS streaming must not hardcode a runaway limit; the cap comes from the user setting."

Assert-Match `
    -Content $WindowsViewModel `
    -Pattern "MaxRecordingDuration = TimeSpan\.FromMinutes\(20\).*?_durationTimer\.Elapsed \+= \(s, e\) =>.*?RecordingDuration = _recorderService\.Duration;.*?CheckRecordingDurationLimit\(\)" `
    -Message "Windows batch recording must use the shared 20-minute cap from the duration timer."

Assert-Match `
    -Content $WindowsViewModel `
    -Pattern "private void CheckRecordingDurationLimit\(\).*?_isStreamingSession.*?_recordingDurationLimitReached.*?RecordingDuration < EffectiveMaxRecordingDuration.*?recording_duration_limit_reached.*?AutoStopRecordingAfterDurationLimitAsync\(\)" `
    -Message "Windows batch duration guard must be one-shot, exclude streaming, and dispatch normal auto-stop."

# The cap became restorable from a backup in phase 3 (issue #288), so the
# 20-minute value is now a CEILING as well as the default. These assertions are
# what stop a shared .hwbackup.json from weakening the guard: the effective cap is
# the minimum of the setting and the hardcoded ceiling, the setter clamps every
# write, and the shared core clamps the import path for all three heads.
Assert-Match `
    -Content $WindowsViewModel `
    -Pattern "EffectiveMaxRecordingDuration.*?MaxRecordingDurationSeconds.*?configured < MaxRecordingDuration \? configured : MaxRecordingDuration" `
    -Message "Windows must enforce the MINIMUM of the restored setting and the 20-minute ceiling."

Assert-Match `
    -Content $WindowsSettings `
    -Pattern "MaxRecordingDurationCeilingSeconds = 20 \* 60" `
    -Message "SettingsService must keep the 20-minute ceiling."

Assert-Match `
    -Content $WindowsSettings `
    -Pattern "private static int Clamp\(int seconds\) =>.*?seconds > MaxRecordingDurationCeilingSeconds \? MaxRecordingDurationCeilingSeconds" `
    -Message "SettingsService must clamp every MaxRecordingDurationSeconds write to the ceiling."

Assert-Match `
    -Content $RustMapping `
    -Pattern "WINDOWS_MAX_RECORDING_DURATION_CEILING_SECS: i64 = 20 \* 60" `
    -Message "The shared core must clamp an imported maxRecordingDuration to the same 20-minute ceiling."

Assert-Match `
    -Content $WindowsViewModel `
    -Pattern "if \(_isStoppingRecording\).*?stop already in progress.*?return;.*?_isStoppingRecording = true;.*?finally.*?_isStoppingRecording = false;" `
    -Message "Windows batch stop must be guarded against duplicate stop/transcribe flows."

# The two toast strings moved into Resources\Strings.resx behind Loc.S(), so the
# literal text is asserted where it now lives and the call site is asserted by key.
Assert-Match `
    -Content $WindowsViewModel `
    -Pattern "private async Task AutoStopRecordingAfterDurationLimitAsync\(\).*?ShowErrorToastRequested.*?errors\.recordingDurationLimit.*?await StopRecordingAndTranscribeAsync\(\)" `
    -Message "Windows batch auto-stop must preserve the recording by transcribing it."

Assert-Match `
    -Content $WindowsStrings `
    -Pattern "errors\.recordingDurationLimit.*?20-minute safety limit reached" `
    -Message "The batch auto-stop toast must still name the 20-minute limit."

Assert-Match `
    -Content $WindowsViewModel `
    -Pattern "private void CheckStreamingDurationLimit\(\).*?RecordingDuration < EffectiveMaxRecordingDuration.*?_streamingFailureMessage = Loc\.S\(`"errors\.streamingDurationLimit`"\)" `
    -Message "Windows streaming must use the shared 20-minute cap."

Assert-Match `
    -Content $WindowsStrings `
    -Pattern "errors\.streamingDurationLimit.*?Streaming reached the 20-minute safety limit\." `
    -Message "The streaming limit toast must still name the 20-minute limit."

Assert-Match `
    -Content $WindowsViewModel `
    -Pattern "if \(!string\.IsNullOrWhiteSpace\(_streamingFailureMessage\) && !_streamingDurationLimitReached\)" `
    -Message "Windows duration-limited streaming sessions with text must not be marked failed."

Assert-NotMatch `
    -Content $WindowsViewModel `
    -Pattern "MaxStreamingDuration|60-minute session limit" `
    -Message "Windows must not keep the previous 60-minute streaming-only cap."

Write-Host "Recording runaway guard verifier passed."
