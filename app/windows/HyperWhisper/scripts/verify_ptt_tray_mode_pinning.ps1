param()

$ErrorActionPreference = "Stop"

function Assert-Match {
    param(
        [Parameter(Mandatory = $true)][string]$Content,
        [Parameter(Mandatory = $true)][string]$Pattern,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if (-not [regex]::IsMatch($Content, $Pattern, [System.Text.RegularExpressions.RegexOptions]::Singleline)) {
        throw "Missing expected PTT/tray mode-pinning wiring: $Label"
    }
}

$ProjectRoot = Split-Path -Parent $PSScriptRoot
$RepoRoot = Resolve-Path (Join-Path $ProjectRoot "..\..\..")

$MainViewModelSource = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "ViewModels\MainViewModel.cs")
$PushToTalkSource = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "Services\PushToTalkMonitor.cs")
$MainWindowSource = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "Views\Windows\MainWindow.xaml.cs")
$MacStartSource = Get-Content -Raw -LiteralPath (Join-Path $RepoRoot "app\macos\hyperwhisper\Managers\AudioRecording\RecordingFlow\RecordingTranscriptionFlow+StartRecording.swift")
$MacStopSource = Get-Content -Raw -LiteralPath (Join-Path $RepoRoot "app\macos\hyperwhisper\Managers\AudioRecording\RecordingFlow\RecordingTranscriptionFlow+StopRecording.swift")
$MacPttSource = Get-Content -Raw -LiteralPath (Join-Path $RepoRoot "app\macos\hyperwhisper\Utilities\BareModifierKeyMonitor.swift")

Assert-Match `
    -Content $MainViewModelSource `
    -Pattern "private Mode\? _activeRecordingMode;" `
    -Label "Windows keeps an active recording mode snapshot"

Assert-Match `
    -Content $MainViewModelSource `
    -Pattern "StartRecordingAsync\(\).*?var recordingMode = SelectedMode;.*?_activeRecordingMode = recordingMode;.*?recordingMode\.EnableScreenOCR.*?recordingMode\.ProviderType != `"cloud`".*?IsLocalProviderReady\(recordingMode\).*?IsLocalModelDownloaded\(recordingMode\)" `
    -Label "recording start snapshots the selected mode and uses it for readiness checks"

Assert-Match `
    -Content $MainViewModelSource `
    -Pattern "StopRecordingAndTranscribeAsync\(\).*?var recordingMode = _activeRecordingMode \?\? SelectedMode;.*?CreateProcessingTranscript\(\s*RecordingDuration\.TotalSeconds,\s*recordingMode\.Name,\s*permanentAudioPath\).*?if \(recordingMode\.PostProcessingMode != 0\).*?TranscribeAsync\(\s*permanentAudioPath,\s*recordingMode,\s*vocabulary,\s*localTranscriptionProvider: GetLocalProvider\(recordingMode\).*?string modeLanguage = recordingMode\.Language \?\? `"auto`";.*?if \(recordingMode\.RemoveTrailingPeriod\)" `
    -Label "recording stop/transcribe uses the active mode snapshot for history, routing, enhancement, provider, language, and paste formatting"

Assert-Match `
    -Content $MainViewModelSource `
    -Pattern "CaptureNoSpeechDiagnostic\(.*?mode: recordingMode.*?diagnosticStage: `"live_recording`"" `
    -Label "live no-speech diagnostics use the active recording mode"

Assert-Match `
    -Content $MainViewModelSource `
    -Pattern "finally\s*\{.*?_pasteService\?\.EndRecordingSession\(\);.*?_activeRecordingMode = null;" `
    -Label "recording stop cleanup clears the active mode snapshot"

Assert-Match `
    -Content $MainViewModelSource `
    -Pattern "CleanupFailedRecordingStart\(\).*?_capturedApplicationContext = null;.*?_activeRecordingMode = null;" `
    -Label "failed recording starts clear the active mode snapshot"

Assert-Match `
    -Content $MainViewModelSource `
    -Pattern "TranscribeFileAsync\(string filePath, Mode mode\).*?CaptureNoSpeechDiagnostic\(.*?mode: mode.*?diagnosticStage: `"file_transcription`"" `
    -Label "file transcription no-speech diagnostics use the clicked mode instead of mutable SelectedMode"

Assert-Match `
    -Content $MainViewModelSource `
    -Pattern "IsLocalProviderReady\(Mode mode\).*?mode\.LocalEngine == `"parakeet`".*?IsLocalModelDownloaded\(Mode mode\).*?mode\.LocalEngine == `"parakeet`"" `
    -Label "local readiness/download helpers accept an explicit mode"

Assert-Match `
    -Content $PushToTalkSource `
    -Pattern "IsPhysicallyHeld\(\).*?PushToTalkMode\.Custom && _settings\.CustomShortcut != null.*?shortcut\.Control.*?VK_LCONTROL.*?shortcut\.Alt.*?VK_LMENU.*?shortcut\.Shift.*?VK_LSHIFT.*?shortcut\.Win.*?VK_LWIN.*?shortcut\.Key\.HasValue.*?KeyInterop\.VirtualKeyFromKey.*?return true;" `
    -Label "custom modifier-only PTT chords receive physical-state debounce protection"

Assert-Match `
    -Content $PushToTalkSource `
    -Pattern "StartKeyUpDebounce\(\).*?IsPhysicallyHeld\(\).*?Spurious key-up confirmed via GetAsyncKeyState" `
    -Label "PTT debounce cross-checks physical key state before committing release"

Assert-Match `
    -Content $MainWindowSource `
    -Pattern "RefreshModeMenu\(\).*?_viewModel\.SelectedMode = m;.*?RefreshFileTranscriptionMenu\(\).*?TranscribeFileWithModeAsync\(m\)" `
    -Label "tray mode selection and tray file transcription remain separate paths, so recording mode snapshot protects live recordings"

Assert-Match `
    -Content $MacStartSource `
    -Pattern "selectedModeId.*?setActiveSessionMode\(id: selectedModeId, name: mode\)" `
    -Label "macOS start flow pins session mode identity"

Assert-Match `
    -Content $MacStopSource `
    -Pattern "let sessionModeName = activeSessionModeName.*?let sessionModeId = activeSessionModeId.*?sessionModeName" `
    -Label "macOS stop flow uses pinned session mode identity"

Assert-Match `
    -Content $MacPttSource `
    -Pattern "state.*?doublePressEnabled.*?resetToIdle" `
    -Label "macOS PTT monitor has state/double-press/reset parity anchors"

Write-Host "PTT/tray mode-pinning verifier passed."
