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
$MacFlow = Get-Content -Raw -LiteralPath (Join-Path $RepoRoot "app\macos\hyperwhisper\Managers\AudioRecording\RecordingFlow\RecordingTranscriptionFlow.swift")
$MacStart = Get-Content -Raw -LiteralPath (Join-Path $RepoRoot "app\macos\hyperwhisper\Managers\AudioRecording\RecordingFlow\RecordingTranscriptionFlow+StartRecording.swift")
$MacStop = Get-Content -Raw -LiteralPath (Join-Path $RepoRoot "app\macos\hyperwhisper\Managers\AudioRecording\RecordingFlow\RecordingTranscriptionFlow+StopRecording.swift")
$MacStreaming = Get-Content -Raw -LiteralPath (Join-Path $RepoRoot "app\macos\hyperwhisper\Managers\AudioRecording\RecordingFlow\RecordingTranscriptionFlow+Streaming.swift")

Assert-Match `
    -Content $MacFlow `
    -Pattern "static let maxRecordingDuration: TimeInterval = 20 \* 60.*?var recordingMaxDurationTimer: Timer\?" `
    -Message "macOS must define one 20-minute recording cap and a batch recording safety timer."

Assert-Match `
    -Content $MacStart `
    -Pattern "armRecordingMaxDurationTimer\(mode: mode, attemptId: attemptId\).*?Timer\.scheduledTimer\(withTimeInterval: Self\.maxRecordingDuration.*?currentRecordingTriggerSource = \.autoStop.*?handleStopRecordingWithTranscription\(mode: mode, cancelled: false\)" `
    -Message "macOS batch recording must auto-stop at the cap and continue into transcription, not cancellation."

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
    -Pattern "Timer\.scheduledTimer\(withTimeInterval: Self\.maxRecordingDuration.*?!self\.isStopInProgress.*?self\.isStopInProgress = true.*?Streaming stopped.*?20-minute safety limit reached.*?currentRecordingTriggerSource = \.autoStop.*?stopStreamingTranscription\(mode: modeName\)" `
    -Message "macOS streaming must use the same 20-minute cap, claim the stop latch, and stop normally."

Assert-NotMatch `
    -Content $MacStreaming `
    -Pattern "withTimeInterval: 3600|1-hour limit|1 hour" `
    -Message "macOS streaming must not keep the previous 1-hour runaway limit."

Assert-Match `
    -Content $WindowsViewModel `
    -Pattern "MaxRecordingDuration = TimeSpan\.FromMinutes\(20\).*?_durationTimer\.Elapsed \+= \(s, e\) =>.*?RecordingDuration = _recorderService\.Duration;.*?CheckRecordingDurationLimit\(\)" `
    -Message "Windows batch recording must use the shared 20-minute cap from the duration timer."

Assert-Match `
    -Content $WindowsViewModel `
    -Pattern "private void CheckRecordingDurationLimit\(\).*?_isStreamingSession.*?_recordingDurationLimitReached.*?RecordingDuration < MaxRecordingDuration.*?recording_duration_limit_reached.*?AutoStopRecordingAfterDurationLimitAsync\(\)" `
    -Message "Windows batch duration guard must be one-shot, exclude streaming, and dispatch normal auto-stop."

Assert-Match `
    -Content $WindowsViewModel `
    -Pattern "if \(_isStoppingRecording\).*?stop already in progress.*?return;.*?_isStoppingRecording = true;.*?finally.*?_isStoppingRecording = false;" `
    -Message "Windows batch stop must be guarded against duplicate stop/transcribe flows."

Assert-Match `
    -Content $WindowsViewModel `
    -Pattern "private async Task AutoStopRecordingAfterDurationLimitAsync\(\).*?ShowErrorToastRequested.*?20-minute safety limit reached.*?await StopRecordingAndTranscribeAsync\(\)" `
    -Message "Windows batch auto-stop must preserve the recording by transcribing it."

Assert-Match `
    -Content $WindowsViewModel `
    -Pattern "private void CheckStreamingDurationLimit\(\).*?RecordingDuration < MaxRecordingDuration.*?_streamingFailureMessage = `"Streaming reached the 20-minute safety limit\.`"" `
    -Message "Windows streaming must use the shared 20-minute cap."

Assert-Match `
    -Content $WindowsViewModel `
    -Pattern "if \(!string\.IsNullOrWhiteSpace\(_streamingFailureMessage\) && !_streamingDurationLimitReached\)" `
    -Message "Windows duration-limited streaming sessions with text must not be marked failed."

Assert-NotMatch `
    -Content $WindowsViewModel `
    -Pattern "MaxStreamingDuration|60-minute session limit" `
    -Message "Windows must not keep the previous 60-minute streaming-only cap."

Write-Host "Recording runaway guard verifier passed."
