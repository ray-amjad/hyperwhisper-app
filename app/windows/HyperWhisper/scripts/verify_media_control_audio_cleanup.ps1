param()

$ErrorActionPreference = "Stop"

function Assert-Match {
    param(
        [Parameter(Mandatory = $true)][string]$Content,
        [Parameter(Mandatory = $true)][string]$Pattern,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if (-not [regex]::IsMatch($Content, $Pattern, [System.Text.RegularExpressions.RegexOptions]::Singleline)) {
        throw "Missing expected media-control/audio-cleanup wiring: $Label"
    }
}

$ProjectRoot = Split-Path -Parent $PSScriptRoot
$RepoRoot = Resolve-Path (Join-Path $ProjectRoot "..\..\..")

$SoundPageSource = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "Views\Pages\Settings\SoundSettingsPage.xaml.cs")
$SoundPageXaml = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "Views\Pages\Settings\SoundSettingsPage.xaml")
$SettingsSource = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "Services\SettingsService.cs")
$SettingsDefaultsSource = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "Services\SettingsService.Shortcuts.cs")
$BackupMapperSource = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "Services\UniversalBackupMapper.cs")
$RuntimeStateSource = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "Services\LoggingService.cs")
$AudioEnvironmentSource = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "Services\AudioEnvironmentService.cs")
$RecorderSource = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "Services\AudioRecorderService.cs")
$ViewModelSource = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "ViewModels\MainViewModel.cs")
$MacAudioSettingsSource = Get-Content -Raw -LiteralPath (Join-Path $RepoRoot "app\macos\hyperwhisper\Managers\Settings\AudioSettingsManager.swift")
$MacStartSource = Get-Content -Raw -LiteralPath (Join-Path $RepoRoot "app\macos\hyperwhisper\Managers\AudioRecording\RecordingFlow\RecordingTranscriptionFlow+StartRecording.swift")
$MacLifecycleSource = Get-Content -Raw -LiteralPath (Join-Path $RepoRoot "app\macos\hyperwhisper\Managers\AudioRecording\Recording\RecordingLifecycle.swift")

Assert-Match `
    -Content $SoundPageXaml `
    -Pattern "MediaControlModeComboBox.*?Tag=`"off`".*?Tag=`"muteAudio`"" `
    -Label "Sound settings exposes only off and muteAudio media-control choices"

Assert-Match `
    -Content $SoundPageSource `
    -Pattern "SelectMediaControlMode\(SettingsService\.Instance\.MediaControlMode\).*?MediaControlModeComboBox_SelectionChanged.*?SettingsService\.Instance\.MediaControlMode = mode" `
    -Label "Sound settings loads and persists media-control mode"

Assert-Match `
    -Content $SettingsSource `
    -Pattern "public string MediaControlMode.*?get => NormalizeMediaControlMode\(_settings\.MediaControlMode\).*?var normalized = NormalizeMediaControlMode\(value\).*?_settings\.MediaControlMode = normalized.*?NotifySettingsChanged\(\).*?NormalizeMediaControlMode.*?muteAudio.*?off" `
    -Label "MediaControlMode persists through a normalizing setter"

Assert-Match `
    -Content $SettingsDefaultsSource `
    -Pattern "_settings\.MediaControlMode = NormalizeMediaControlMode\(_settings\.MediaControlMode\)" `
    -Label "settings defaults normalize migrated media-control values"

Assert-Match `
    -Content $BackupMapperSource `
    -Pattern "MediaControlMode = settings\.MediaControlMode.*?winSettings\.MediaControlMode.*?settings\.MediaControlMode = winSettings\.MediaControlMode" `
    -Label "backup export/import round-trips media-control mode through the normalizing setter"

Assert-Match `
    -Content $RuntimeStateSource `
    -Pattern "Media control mode: \{settings\.MediaControlMode\}" `
    -Label "diagnostics runtime-state includes media-control mode"

Assert-Match `
    -Content $AudioEnvironmentSource `
    -Pattern "ClaimRestoreOwnershipForRecording.*?_pendingRestoreCts = null.*?_pendingRestoreState = null.*?Cancelled pending restore and transferred audio restore ownership" `
    -Label "audio environment can transfer pending restore ownership to a new recording"

Assert-Match `
    -Content $AudioEnvironmentSource `
    -Pattern "PrepareForRecording.*?MediaControlMode\.Equals\(`"muteAudio`".*?GetDefaultAudioEndpoint\(DataFlow\.Render, Role\.Multimedia\).*?AudioEndpointVolume\.Mute = true.*?MutedByHyperWhisper" `
    -Label "muteAudio mode mutes the default render endpoint and records ownership"

Assert-Match `
    -Content $AudioEnvironmentSource `
    -Pattern "ScheduleRestoreAfterRecording.*?MutedByHyperWhisper.*?RestorePendingMuteState.*?RestoreMuteState.*?if \(device\.AudioEndpointVolume\.Mute\).*?device\.AudioEndpointVolume\.Mute = state\.WasMuted.*?Skipping output restore because audio was already unmuted" `
    -Label "scheduled restore only restores HyperWhisper-owned mute and respects manual unmute"

Assert-Match `
    -Content $AudioEnvironmentSource `
    -Pattern "RestoreAfterRecordingImmediatelyAsync.*?_pendingRestoreCts.*?_pendingRestoreState.*?RestoreMuteState\(restoreState, 0\)" `
    -Label "shutdown cleanup flushes current or pending audio mute restore"

Assert-Match `
    -Content $RecorderSource `
    -Pattern "BoostMicVolume.*?_originalMicVolume = currentVolume.*?RestoreMicVolume.*?_captureDevice\.AudioEndpointVolume\.MasterVolumeLevelScalar = _originalMicVolume\.Value.*?_originalMicVolume = null" `
    -Label "mic boost records and restores owned microphone volume"

Assert-Match `
    -Content $ViewModelSource `
    -Pattern "StartRecordingAsync.*?ClaimRestoreOwnershipForRecording\(\).*?_recorderService\.StartRecording.*?PrepareForRecording\(audioRestoreClaim!\)" `
    -Label "standard recording claims before capture and applies media control after capture starts"

Assert-Match `
    -Content $ViewModelSource `
    -Pattern "StartStreamingRecordingAsync.*?ClaimRestoreOwnershipForRecording\(\).*?_streamingAudioCapture\.Start.*?PrepareForRecording\(audioRestoreClaim!\)" `
    -Label "streaming recording claims before capture and applies media control after capture starts"

Assert-Match `
    -Content $ViewModelSource `
    -Pattern "StopRecordingAndTranscribeAsync.*?_recorderService\.StopRecording\(\).*?_recorderService\.RestoreMicVolume\(\).*?RestoreAudioEnvironment\(\)" `
    -Label "standard stop restores microphone volume and output audio"

Assert-Match `
    -Content $ViewModelSource `
    -Pattern "CancelRecordingAsync.*?_recorderService\.StopRecording\(\).*?_recorderService\.RestoreMicVolume\(\).*?RestoreAudioEnvironment\(\)" `
    -Label "standard cancel restores microphone volume and output audio"

Assert-Match `
    -Content $ViewModelSource `
    -Pattern "StopStreamingRecordingAsync.*?_streamingAudioCapture\?\.Stop\(\).*?_recorderService\.RestoreMicVolume\(\).*?RestoreAudioEnvironment\(\)" `
    -Label "streaming stop restores microphone volume and output audio"

Assert-Match `
    -Content $ViewModelSource `
    -Pattern "CancelStreamingRecordingAsync.*?_streamingAudioCapture\?\.Stop\(\).*?_recorderService\.RestoreMicVolume\(\).*?RestoreAudioEnvironment\(\)" `
    -Label "streaming cancel restores microphone volume and output audio"

Assert-Match `
    -Content $ViewModelSource `
    -Pattern "CleanupFailedRecordingStart\(\).*?_recorderService\.RestoreMicVolume\(\).*?RestoreAudioEnvironment\(\).*?ResumeMicrophoneKeepWarm\(\)" `
    -Label "failed recording starts restore audio state and resume keep-warm"

Assert-Match `
    -Content $ViewModelSource `
    -Pattern "CleanupAsync\(\).*?await CleanupStreamingSessionAsync\(\);.*?_recorderService\.RestoreMicVolume\(\);.*?await RestoreAudioEnvironmentImmediatelyAsync\(\);.*?ResumeMicrophoneKeepWarm\(\);.*?_recorderService\?\.Dispose\(\)" `
    -Label "app cleanup restores mic boost/output mute and resumes keep-warm before recorder disposal"

Assert-Match `
    -Content $MacAudioSettingsSource `
    -Pattern "migratePauseMediaMode.*?stored == `"pauseMedia`".*?mediaControlMode = \.off.*?enum MediaControlMode.*?case off.*?case muteAudio.*?init\(from decoder: Decoder\).*?rawValue == `"pauseMedia`".*?self = \.off" `
    -Label "macOS media-control settings expose off/muteAudio and migrate removed pause-media behavior"

Assert-Match `
    -Content $MacStartSource `
    -Pattern "recordingLifecycle\.applyMediaControl\(\)" `
    -Label "macOS recording start applies media control after recording setup"

Assert-Match `
    -Content $MacLifecycleSource `
    -Pattern "applyMediaControl.*?case \.muteAudio.*?prepareAudioEnvironment.*?restoreAudioEnvironment" `
    -Label "macOS lifecycle mutes and restores audio environment"

Write-Host "Media-control audio cleanup verifier passed."
