param()

$ErrorActionPreference = "Stop"

function Assert-Match {
    param(
        [Parameter(Mandatory = $true)][string]$Content,
        [Parameter(Mandatory = $true)][string]$Pattern,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if (-not [regex]::IsMatch($Content, $Pattern, [System.Text.RegularExpressions.RegexOptions]::Singleline)) {
        throw "Missing expected transcription cancellation wiring: $Label"
    }
}

$ProjectRoot = Split-Path -Parent $PSScriptRoot
$RepoRoot = Resolve-Path (Join-Path $ProjectRoot "..\..\..")

$MainViewModelSource = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "ViewModels\MainViewModel.cs")
$FileTranscriptionSource = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "Services\FileTranscriptionService.cs")
$HistorySource = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "Services\HistoryService.cs")
$MainWindowSource = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "Views\Windows\MainWindow.xaml.cs")
$MacStopSource = Get-Content -Raw -LiteralPath (Join-Path $RepoRoot "app\macos\hyperwhisper\Managers\AudioRecording\RecordingFlow\RecordingTranscriptionFlow+StopRecording.swift")

Assert-Match `
    -Content $MacStopSource `
    -Pattern "AccessibilityHelper\.shared\.endRecordingSession\(\)" `
    -Label "macOS cancel/stop flow ends the captured recording session"

Assert-Match `
    -Content $MainViewModelSource `
    -Pattern "private async Task CancelRecordingAsync\(\).*?_recorderService\.StopRecording\(\).*?_recorderService\.RestoreMicVolume\(\).*?RestoreAudioEnvironment\(\).*?ResumeMicrophoneKeepWarm\(\).*?_shortcutService\.ResetKeyboardState\(\).*?_pasteService\?\.EndRecordingSession\(\)" `
    -Label "standard recording cancel restores audio/input state and ends the paste session"

Assert-Match `
    -Content $MainViewModelSource `
    -Pattern "private bool CancelActiveTranscription\(\).*?_activeTranscriptionCts.*?cts\.Cancel\(\)" `
    -Label "active transcription cancellation uses the shared CTS"

Assert-Match `
    -Content $MainViewModelSource `
    -Pattern "if \(IsTranscribing\).*?CancelActiveTranscription\(\).*?HideOverlayRequested\?\.Invoke\(this, EventArgs\.Empty\).*?HideFileProgressRequested\?\.Invoke\(this, EventArgs\.Empty\)" `
    -Label "Escape cancellation hides overlay and file progress immediately while transcription is active"

Assert-Match `
    -Content $MainViewModelSource `
    -Pattern "_activeTranscriptionCts = transcriptionCts;.*?catch \(OperationCanceledException\) when \(transcriptionCts\.IsCancellationRequested\).*?HideOverlayRequested\?\.Invoke\(this, EventArgs\.Empty\).*?DeleteTranscript\(transcript\.Id\).*?DeleteAudioFile\(permanentAudioPath\).*?if \(!transcriptDeleted\).*?EnsureTranscriptTerminalStatus\(transcript\).*?ReferenceEquals\(_activeTranscriptionCts, transcriptionCts\)" `
    -Label "recording transcription cancellation deletes history/audio, enforces terminal status, and clears only the owned CTS"

Assert-Match `
    -Content $MainViewModelSource `
    -Pattern "private bool CanStartFileTranscription\(\).*?IsRecording \|\| IsTranscribing \|\| _activeTranscriptionCts != null.*?return false;" `
    -Label "file transcription refuses to start while another recording/transcription owns cancellation state"

Assert-Match `
    -Content $MainViewModelSource `
    -Pattern "public async Task TranscribeFileWithModeAsync\(Mode mode\).*?!CanStartFileTranscription\(\).*?return;.*?OpenFileDialog" `
    -Label "tray/menu file transcription guard runs before opening the file dialog"

Assert-Match `
    -Content $MainViewModelSource `
    -Pattern "public async Task TranscribeFileAsync\(string filePath\).*?!CanStartFileTranscription\(\).*?return;.*?await TranscribeFileAsync\(filePath, SelectedMode\)" `
    -Label "direct public file transcription guard runs before starting the private worker"

Assert-Match `
    -Content $MainViewModelSource `
    -Pattern "private async Task TranscribeFileAsync\(string filePath, Mode mode\).*?!CanStartFileTranscription\(\).*?return;" `
    -Label "private file transcription worker has a final overlap guard"

Assert-Match `
    -Content $MainViewModelSource `
    -Pattern "ConvertToWhisperFormatAsync\(\s*filePath,\s*transcriptionCts\.Token\)" `
    -Label "local file conversion receives the active cancellation token"

Assert-Match `
    -Content $FileTranscriptionSource `
    -Pattern "ConvertToWhisperFormatAsync\(\s*string inputPath,\s*CancellationToken cancellationToken = default\).*?cancellationToken\.ThrowIfCancellationRequested\(\).*?Task\.Run\(.*?WriteWaveFile16Cancellable\(tempPath, sampleProvider, cancellationToken\).*?catch \(OperationCanceledException\).*?throw;" `
    -Label "file conversion observes cancellation and preserves OperationCanceledException"

Assert-Match `
    -Content $FileTranscriptionSource `
    -Pattern "private static void WriteWaveFile16Cancellable\(.*?SampleToWaveProvider16\(sampleProvider\).*?while \(\(bytesRead = waveProvider\.Read\(buffer, 0, buffer\.Length\)\) > 0\).*?cancellationToken\.ThrowIfCancellationRequested\(\).*?writer\.Write\(buffer, 0, bytesRead\)" `
    -Label "local WAV conversion writes in cancellable chunks"

Assert-Match `
    -Content $MainViewModelSource `
    -Pattern "_activeTranscriptionCts = transcriptionCts;.*?catch \(OperationCanceledException\) when \(transcriptionCts\.IsCancellationRequested \|\| isCancelled\).*?HideFileProgressRequested\?\.Invoke\(this, EventArgs\.Empty\).*?DeleteTranscript\(transcript\.Id\).*?DeleteAudioFile\(permanentPath\).*?if \(!transcriptDeleted\).*?EnsureTranscriptTerminalStatus\(transcript\).*?convertedTempPath != null.*?File\.Delete\(convertedTempPath\).*?ReferenceEquals\(_activeTranscriptionCts, transcriptionCts\)" `
    -Label "file transcription cancellation deletes history/audio/temp files, enforces terminal status, and clears only the owned CTS"

Assert-Match `
    -Content $HistorySource `
    -Pattern "public bool DeleteTranscript\(Guid id\).*?context\.Transcripts\.Remove\(transcript\).*?context\.SaveChanges\(\).*?DeleteTranscriptAudioFiles\(transcript\)" `
    -Label "deleted cancelled transcripts also delete associated audio files"

Assert-Match `
    -Content $HistorySource `
    -Pattern "private void DeleteTranscriptAudioFiles\(Transcript transcript\).*?DeleteAudioFile\(path\)" `
    -Label "history audio-file deletion is centralized through DeleteAudioFile"

Assert-Match `
    -Content $MainWindowSource `
    -Pattern "nameof\(MainViewModel\.IsRecording\).*?nameof\(MainViewModel\.IsTranscribing\).*?Dispatcher\.Invoke\(RefreshFileTranscriptionMenu\)" `
    -Label "tray file transcription menu refreshes when busy state changes"

Assert-Match `
    -Content $MainWindowSource `
    -Pattern "RefreshFileTranscriptionMenu\(\).*?Enabled = !_viewModel\.IsRecording && !_viewModel\.IsTranscribing && !_viewModel\.IsModelLoading" `
    -Label "tray file transcription mode items are disabled while busy"

Write-Host "Transcription cancellation wiring verifier passed."
