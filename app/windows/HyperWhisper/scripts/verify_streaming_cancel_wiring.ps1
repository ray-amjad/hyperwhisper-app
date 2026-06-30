param()

$ErrorActionPreference = "Stop"

function Assert-Match {
    param(
        [Parameter(Mandatory = $true)][string]$Content,
        [Parameter(Mandatory = $true)][string]$Pattern,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if (-not [regex]::IsMatch($Content, $Pattern, [System.Text.RegularExpressions.RegexOptions]::Singleline)) {
        throw "Missing expected streaming cancel wiring: $Label"
    }
}

$ProjectRoot = Split-Path -Parent $PSScriptRoot
$RepoRoot = Resolve-Path (Join-Path $ProjectRoot "..\..\..")

$MainViewModelSource = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "ViewModels\MainViewModel.cs")
$MacStreamingSource = Get-Content -Raw -LiteralPath (Join-Path $RepoRoot "app\macos\hyperwhisper\Managers\AudioRecording\RecordingFlow\RecordingTranscriptionFlow+Streaming.swift")
$MacToggleSource = Get-Content -Raw -LiteralPath (Join-Path $RepoRoot "app\macos\hyperwhisper\Managers\AudioRecording\RecordingFlow\RecordingTranscriptionFlow+Toggle.swift")

Assert-Match `
    -Content $MainViewModelSource `
    -Pattern "public async Task HandleCancelRequest\(\).*?if \(_isStreamingStarting\).*?CancelStreamingStart\(\).*?if \(_isStreamingSession\).*?await CancelRecordingAsync\(\);.*?return;" `
    -Label "cancel shortcut during active streaming routes to cancel/discard cleanup, not stop-and-save"

Assert-Match `
    -Content $MainViewModelSource `
    -Pattern "private bool _isStoppingStreaming;.*?private bool IsStreamingActive\(\) => _isStreamingSession \|\| _isStreamingStarting \|\| _isStoppingStreaming;" `
    -Label "streaming remains globally active while the stop/save path owns cleanup"

Assert-Match `
    -Content $MainViewModelSource `
    -Pattern "private async Task StopStreamingRecordingAsync\(\).*?if \(_isStoppingStreaming\).*?ignoring duplicate request.*?return;.*?_isStoppingStreaming = true;" `
    -Label "streaming stop/save flow ignores concurrent duplicate entries"

Assert-Match `
    -Content $MainViewModelSource `
    -Pattern "private async Task CancelRecordingAsync\(\).*?if \(_isStreamingSession\).*?await CancelStreamingRecordingAsync\(\);.*?return;" `
    -Label "streaming cancel uses the dedicated streaming cleanup path"

Assert-Match `
    -Content $MainViewModelSource `
    -Pattern "private async Task CancelStreamingRecordingAsync\(\).*?_streamingAudioCapture\?\.Stop\(\).*?RestoreAudioEnvironment\(\).*?ResumeMicrophoneKeepWarm\(\).*?IsRecording = false.*?await CleanupStreamingSessionAsync\(\).*?HideOverlayRequested\?\.Invoke\(this, EventArgs\.Empty\).*?status\.recordingCancelled.*?_pasteService\?\.EndRecordingSession\(\)" `
    -Label "streaming cancel stops capture, restores state, hides overlay, and ends paste session"

Assert-Match `
    -Content $MainViewModelSource `
    -Pattern "private async Task StopStreamingRecordingAsync\(\).*?CreateProcessingTranscript.*?HistoryService\.Instance\.UpdateTranscript\(transcript\).*?ShowSuccessRequested|private async Task StopStreamingRecordingAsync\(\).*?CreateProcessingTranscript.*?HistoryService\.Instance\.UpdateTranscript\(transcript\).*?ShowCopiedRequested" `
    -Label "normal streaming stop remains the save/paste/success flow"

Assert-Match `
    -Content $MainViewModelSource `
    -Pattern "private async Task StopStreamingRecordingAsync\(\).*?finally\s*\{.*?try\s*\{.*?await CleanupStreamingSessionAsync\(\);.*?finally\s*\{.*?_hotkeyBlocked = false;.*?IsTranscribing = false;.*?_pasteService\?\.EndRecordingSession\(\);.*?_isStoppingStreaming = false;" `
    -Label "streaming stop guard clears even when cleanup is unwinding"

Assert-Match `
    -Content $MacToggleSource `
    -Pattern "if isStreamingActive.*?handleStopRecordingWithTranscription\(mode: appState\.currentSessionModeName, cancelled: true\)" `
    -Label "macOS cancel shortcut routes active streaming through a cancelled stop path"

Assert-Match `
    -Content $MacStreamingSource `
    -Pattern "cancelRecordingWithError|cancelled: true|KeyboardShortcuts\.disable\(\.cancelRecording\)" `
    -Label "macOS streaming flow has explicit cancel/error cleanup anchors"

Write-Host "Streaming cancel wiring verifier passed."
