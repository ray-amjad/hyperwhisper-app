param()

$ErrorActionPreference = "Stop"

function Assert-True {
    param(
        [bool] $Condition,
        [string] $Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Assert-Match {
    param(
        [string] $Content,
        [string] $Pattern,
        [string] $Message
    )

    Assert-True ([regex]::IsMatch($Content, $Pattern, [System.Text.RegularExpressions.RegexOptions]::Singleline)) $Message
}

$ProjectRoot = Split-Path -Parent $PSScriptRoot
$RepoRoot = Resolve-Path (Join-Path $ProjectRoot "..\..\..")
$ServiceSource = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "Services\MicrophoneKeepWarmService.cs")
$ViewModelSource = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "ViewModels\MainViewModel.cs")
$SettingsSource = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "Services\SettingsService.cs")
$SettingsDefaultsSource = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "Services\SettingsService.Shortcuts.cs")
$SoundPageSource = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "Views\Pages\Settings\SoundSettingsPage.xaml.cs")
$MacKeepWarmSource = Get-Content -Raw -LiteralPath (Join-Path $RepoRoot "app\macos\hyperwhisper\Managers\AudioRecording\KeepWarm\MicrophoneKeepWarmManager.swift")
$MacRecordingManagerSource = Get-Content -Raw -LiteralPath (Join-Path $RepoRoot "app\macos\hyperwhisper\Managers\AudioRecording\AudioRecordingManager.swift")

Assert-Match `
    -Content $SettingsSource `
    -Pattern "public bool KeepMicrophoneWarm.*?get => _settings\.KeepMicrophoneWarm \?\? false.*?_settings\.KeepMicrophoneWarm = value.*?NotifySettingsChanged\(\)" `
    -Message "KeepMicrophoneWarm must persist, default false, and notify listeners."

Assert-Match `
    -Content $SettingsDefaultsSource `
    -Pattern "_settings\.KeepMicrophoneWarm \?\?= false" `
    -Message "KeepMicrophoneWarm default must be false for migrated settings."

Assert-Match `
    -Content $SoundPageSource `
    -Pattern "KeepMicrophoneWarmCheckbox\.IsChecked = SettingsService\.Instance\.KeepMicrophoneWarm.*?KeepMicrophoneWarmCheckbox_Checked.*?SettingsService\.Instance\.KeepMicrophoneWarm = true.*?KeepMicrophoneWarmCheckbox_Unchecked.*?SettingsService\.Instance\.KeepMicrophoneWarm = false" `
    -Message "Sound settings page must load and persist the keep-warm toggle."

Assert-Match `
    -Content $ViewModelSource `
    -Pattern "OnSelectedAudioDeviceChanged.*?UpdateMicrophoneKeepWarm\(\).*?OnSettingsChanged.*?UpdateMicrophoneKeepWarm\(\).*?_settingsService\.SettingsChanged \+= OnSettingsChanged" `
    -Message "MainViewModel must reconfigure keep-warm when settings or selected device change."

Assert-Match `
    -Content $ViewModelSource `
    -Pattern "private void UpdateMicrophoneKeepWarm\(\).*?MicrophoneKeepWarmService\.Instance\.Configure\(.*?_settingsService\.KeepMicrophoneWarm.*?SelectedAudioDevice\?\.DeviceNumber" `
    -Message "MainViewModel must configure keep-warm with the current setting and selected device."

Assert-Match `
    -Content $ViewModelSource `
    -Pattern "StartRecordingAsync.*?SuspendMicrophoneKeepWarm\(\).*?_recorderService\.StartRecording\(SelectedAudioDevice\.DeviceNumber\).*?CleanupFailedRecordingStart" `
    -Message "Standard recording start must suspend keep-warm before opening the real recorder and clean up on failure."

Assert-Match `
    -Content $ViewModelSource `
    -Pattern "StopRecordingAndTranscribeAsync.*?_recorderService\.StopRecording\(\).*?ResumeMicrophoneKeepWarm\(\)" `
    -Message "Standard recording stop/transcribe must resume keep-warm after stopping capture."

Assert-Match `
    -Content $ViewModelSource `
    -Pattern "CancelRecordingAsync.*?_recorderService\.StopRecording\(\).*?ResumeMicrophoneKeepWarm\(\)" `
    -Message "Standard recording cancel must resume keep-warm after stopping capture."

Assert-Match `
    -Content $ViewModelSource `
    -Pattern "StartStreamingRecordingAsync.*?SuspendMicrophoneKeepWarm\(\).*?_streamingAudioCapture\.Start\(SelectedAudioDevice\.DeviceNumber" `
    -Message "Streaming start must suspend keep-warm before opening streaming capture."

Assert-Match `
    -Content $ViewModelSource `
    -Pattern "StopStreamingRecordingAsync.*?_streamingAudioCapture\?\.Stop\(\).*?ResumeMicrophoneKeepWarm\(\)" `
    -Message "Streaming stop must resume keep-warm after stopping streaming capture."

Assert-Match `
    -Content $ViewModelSource `
    -Pattern "CancelStreamingRecordingAsync.*?_streamingAudioCapture\?\.Stop\(\).*?ResumeMicrophoneKeepWarm\(\)" `
    -Message "Streaming cancel must resume keep-warm after stopping streaming capture."

Assert-Match `
    -Content $ViewModelSource `
    -Pattern "CleanupAsync.*?_settingsService\.SettingsChanged -= OnSettingsChanged.*?MicrophoneKeepWarmService\.Instance\.Dispose\(\).*?_deviceService\.DevicesChanged -= OnAudioDevicesChanged" `
    -Message "Cleanup must unsubscribe settings/device handlers and dispose keep-warm."

Assert-Match `
    -Content $ServiceSource `
    -Pattern "public void Configure\(bool enabled, int\? deviceNumber\).*?_enabled = enabled.*?!enabled \|\| _suspended \|\| !deviceNumber\.HasValue.*?StopLocked.*?_waveIn != null && _activeDeviceNumber == deviceNumber\.Value.*?StopLocked\(\""device-changed\""\).*?StartLocked\(deviceNumber\.Value\)" `
    -Message "Keep-warm service must stop when disabled/suspended/no-device, avoid same-device restarts, and restart on device change."

Assert-Match `
    -Content $ServiceSource `
    -Pattern "SuspendForRecording.*?_suspended = true.*?StopLocked\(\""recording-started\""\).*?ResumeAfterRecording.*?_suspended = false.*?Configure\(_enabled, deviceNumber\)" `
    -Message "Keep-warm service must suspend for real recording and resume with the selected device."

Assert-Match `
    -Content $ServiceSource `
    -Pattern "WaveFormat = new WaveFormat\(16000, 16, 1\).*?OnDataAvailable.*?discard all audio" `
    -Message "Keep-warm capture must be low-format and discard all audio."

Assert-Match `
    -Content $ServiceSource `
    -Pattern "OnRecordingStopped.*?e\.Exception != null.*?ReferenceEquals\(sender, _waveIn\).*?deviceNumber = _activeDeviceNumber.*?CleanupWaveInLocked\(\).*?_enabled && !_suspended && !_disposed && deviceNumber\.HasValue.*?StartLocked\(deviceNumber\.Value\)" `
    -Message "Unexpected keep-warm recording stops must clean stale capture state and restart when still enabled."

Assert-Match `
    -Content $MacKeepWarmSource `
    -Pattern "activeInputUID.*?setEnabled\(_ enabled: Bool\).*?suspendForActiveRecording.*?resumeAfterRecording.*?captureOutput" `
    -Message "macOS keep-warm comparison must track active input identity, suspend/resume around recording, and discard captured samples."

Assert-Match `
    -Content $MacRecordingManagerSource `
    -Pattern "setupKeepWarmObserver\(\).*?syncKeepWarmConfiguration\(\).*?keepWarmManager\.suspendForActiveRecording\(\).*?keepWarmManager\.resumeAfterRecording\(\)" `
    -Message "macOS recording manager comparison must wire setting changes and recording suspend/resume."

Write-Host "Microphone keep-warm wiring verifier passed."
