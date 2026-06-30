param()

$ErrorActionPreference = "Stop"

function Assert-Match {
    param(
        [Parameter(Mandatory = $true)][string]$Content,
        [Parameter(Mandatory = $true)][string]$Pattern,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if (-not [regex]::IsMatch($Content, $Pattern, [System.Text.RegularExpressions.RegexOptions]::Singleline)) {
        throw "Missing expected streaming failure wiring: $Label"
    }
}

function Assert-NoMatch {
    param(
        [Parameter(Mandatory = $true)][string]$Content,
        [Parameter(Mandatory = $true)][string]$Pattern,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if ([regex]::IsMatch($Content, $Pattern, [System.Text.RegularExpressions.RegexOptions]::Singleline)) {
        throw "Unexpected streaming failure wiring: $Label"
    }
}

function Assert-Order {
    param(
        [Parameter(Mandatory = $true)][string]$Content,
        [Parameter(Mandatory = $true)][string]$First,
        [Parameter(Mandatory = $true)][string]$Second,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $firstIndex = $Content.IndexOf($First, [StringComparison]::Ordinal)
    $secondIndex = $Content.IndexOf($Second, [StringComparison]::Ordinal)

    if ($firstIndex -lt 0 -or $secondIndex -lt 0 -or $firstIndex -ge $secondIndex) {
        throw "Unexpected streaming failure order: $Label"
    }
}

$ProjectRoot = Split-Path -Parent $PSScriptRoot
$RepoRoot = Resolve-Path (Join-Path $ProjectRoot "..\..\..")
$FactorySource = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "Services\Streaming\StreamingTranscriptionSessionFactory.cs")
$ClientSource = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "Services\Streaming\StreamingTranscriptionClient.cs")
$HyperWhisperCloudSource = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "Services\Streaming\HyperWhisperCloudStreamingStrategy.cs")
$MainViewModelSource = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "ViewModels\MainViewModel.cs")
$MainWindowSource = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "Views\Windows\MainWindow.xaml.cs")
$OverlaySource = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "Views\Windows\RecordingOverlayWindow.xaml.cs")
$ProviderSource = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "Models\StreamingTranscriptionProvider.cs")
$MacStreamingSource = Get-Content -Raw -LiteralPath (Join-Path $RepoRoot "app\macos\hyperwhisper\Managers\AudioRecording\RecordingFlow\RecordingTranscriptionFlow+Streaming.swift")
$MacStopSource = Get-Content -Raw -LiteralPath (Join-Path $RepoRoot "app\macos\hyperwhisper\Managers\AudioRecording\RecordingFlow\RecordingTranscriptionFlow+StopRecording.swift")

Assert-Match `
    -Content $FactorySource `
    -Pattern 'string\.IsNullOrWhiteSpace\(apiKey\).*?Result<StreamingTranscriptionClient>\.Failure\(\s*\$"API key not configured for \{provider\.DisplayName\(\)\}"' `
    -Label "factory returns a missing-key failure before creating a streaming client"

Assert-Match `
    -Content $FactorySource `
    -Pattern 'ApiKeyService\.IsValidKeyFormat\(apiKeyType\.Value, apiKey\).*?Invalid API key format for \{provider\.DisplayName\(\)\}' `
    -Label "factory rejects invalid direct-provider API-key formats"

Assert-Match `
    -Content $FactorySource `
    -Pattern "StreamingTranscriptionProvider\.OpenAI => PostProcessingProvider\.OpenAI" `
    -Label "OpenAI streaming uses the shared OpenAI key slot"

Assert-Order `
    -Content $MainViewModelSource `
    -First "var clientResult = StreamingTranscriptionSessionFactory.Create(vocabulary);" `
    -Second "_streamingAudioCapture = new StreamingAudioCapture();" `
    -Label "factory missing-key/invalid-key checks happen before microphone streaming capture is constructed"

Assert-Match `
    -Content $MainViewModelSource `
    -Pattern "if \(clientResult\.IsFailure\).*?ShowErrorToastRequested\?\.Invoke\(this, new ErrorToastEventArgs\(.*?openApiKeysManager: true\)\).*?return;" `
    -Label "factory failures show API-key guidance and return before capture/session state"

Assert-Match `
    -Content $MainViewModelSource `
    -Pattern "catch \(OperationCanceledException\).*?await CleanupStreamingSessionAsync\(\);.*?CleanupFailedRecordingStart\(\);" `
    -Label "streaming connection timeout/cancel cleans the streaming session and failed-start state"

Assert-Match `
    -Content $MainViewModelSource `
    -Pattern "StartStreamingRecordingAsync.*?ShowStreamingOverlayRequested\?\.Invoke\(this, providerName\).*?_streamingClient\.StartAsync\(_streamingStartCts\.Token\)" `
    -Label "streaming overlay is shown before connecting so connecting/ready states are visible"

Assert-Match `
    -Content $MainViewModelSource `
    -Pattern "catch \(OperationCanceledException\).*?HideOverlayRequested\?\.Invoke\(this, EventArgs\.Empty\).*?await CleanupStreamingSessionAsync\(\);" `
    -Label "streaming connection cancellation/timeout hides the pre-connect overlay"

Assert-Match `
    -Content $MainViewModelSource `
    -Pattern "catch \(Exception ex\).*?await CleanupStreamingSessionAsync\(\);.*?CleanupFailedRecordingStart\(\);" `
    -Label "streaming start failures clean the streaming session and failed-start state"

Assert-Match `
    -Content $MainViewModelSource `
    -Pattern "catch \(Exception ex\).*?HideOverlayRequested\?\.Invoke\(this, EventArgs\.Empty\).*?await CleanupStreamingSessionAsync\(\);" `
    -Label "streaming start failures hide the pre-connect overlay"

Assert-Match `
    -Content $MainViewModelSource `
    -Pattern "private bool CheckStreamingTargetAvailability\(\).*?_pasteService\?\.IsCapturedTargetAvailable\(\) != false.*?StopStreamingRecordingAsync\(\)" `
    -Label "captured target loss stops streaming"

Assert-Match `
    -Content $MainViewModelSource `
    -Pattern "private async Task CleanupStreamingSessionAsync\(\).*?_streamingAudioCapture\.Dispose\(\).*?_streamingClient\.DisposeAsync\(\)" `
    -Label "streaming cleanup disposes audio capture and websocket client"

Assert-Match `
    -Content $ClientSource `
    -Pattern "ChangeState\(StreamingConnectionState\.Connecting\).*?ConnectAsync.*?ChangeState\(StreamingConnectionState\.(Ready|Streaming)\)" `
    -Label "streaming client reports connecting then ready/streaming states"

Assert-Match `
    -Content $MainWindowSource `
    -Pattern "ShowStreamingOverlayRequested.*?ShowStreaming\(providerName\).*?StreamingConnectionStateChanged.*?UpdateStreamingConnectionState\(state\)" `
    -Label "main window wires streaming overlay creation and connection-state updates"

Assert-Match `
    -Content $OverlaySource `
    -Pattern "ShowStreaming\(string providerName\).*?StreamingDot\.Visibility = Visibility\.Visible.*?UpdateStreamingConnectionState\(StreamingConnectionState\.Streaming\).*?UpdateStreamingConnectionState\(StreamingConnectionState state\).*?StreamingConnectionState\.Connecting" `
    -Label "streaming overlay exposes the connection-state indicator"

Assert-Match `
    -Content $ClientSource `
    -Pattern "catch \(Exception ex\).*?Raise\(ErrorReceived, ex\.Message\).*?ChangeState\(StreamingConnectionState\.Error\).*?CleanupWebSocket\(\)" `
    -Label "streaming connect errors surface an error state and clean websocket state"

Assert-Match `
    -Content $HyperWhisperCloudSource `
    -Pattern "GetStopSequence\(\).*?SendMessage.*?\{\\`"type\\`":\\`"stop\\`"\}.*?WaitForSessionComplete.*?TimeSpan\.FromSeconds\(10\).*?Close" `
    -Label "HyperWhisper Cloud stop waits for session_complete before closing so final text and credits can arrive"

Assert-Match `
    -Content $ClientSource `
    -Pattern "StopAsync\(.*?foreach \(var step in _strategy\.GetStopSequence\(\)\).*?RunStopStepAsync\(step, cancellationToken\).*?WaitForSessionCompleteAsync.*?_sessionCompletedTcs" `
    -Label "streaming client executes provider stop sequence and can wait for session completion"

Assert-Match `
    -Content $ClientSource `
    -Pattern "public async ValueTask DisposeAsync\(\).*?await StopAsync\(disposeCts\.Token\).*?finally\s*\{.*?_disposed = true;.*?_sendLock\.Dispose\(\);" `
    -Label "DisposeAsync attempts StopAsync before marking the client disposed"

Assert-NoMatch `
    -Content $MainViewModelSource `
    -Pattern "SelectedMode\?\.RemoveTrailingPeriod" `
    -Label "streaming text formatting must not depend on mutable selected mode RemoveTrailingPeriod"

Assert-Match `
    -Content $MainViewModelSource `
    -Pattern "StopStreamingRecordingAsync.*?var textToProcess = finalText;.*?SmartSpacing\.AppendTrailingSpace\(textToProcess, _settingsService\.StreamingLanguage\).*?PasteStreamingFinalSegment\(textToProcess\)" `
    -Label "streaming stop/save formats final text with streaming language only"

Assert-Match `
    -Content $MainViewModelSource `
    -Pattern "PasteStreamingFinalSegment\(string segment\).*?SmartSpacing\.AppendTrailingSpace\(segment, _settingsService\.StreamingLanguage\).*?SmartPaste\(spacedText\)" `
    -Label "streaming final-segment paste formats segment with streaming language only"

Assert-Match `
    -Content $MainViewModelSource `
    -Pattern "StopRecordingAndTranscribeAsync.*?var recordingMode = _activeRecordingMode \?\? SelectedMode;.*?if \(recordingMode\.RemoveTrailingPeriod\).*?SmartSpacing\.RemoveTrailingPeriod\(textToProcess\)" `
    -Label "standard recording still applies the pinned mode RemoveTrailingPeriod setting"

Assert-NoMatch `
    -Content $MacStreamingSource `
    -Pattern "removeTrailingPeriod" `
    -Label "macOS streaming path does not apply batch-mode trailing-period removal"

Assert-Match `
    -Content $MacStopSource `
    -Pattern "removeTrailingPeriod" `
    -Label "macOS batch stop path still owns removeTrailingPeriod behavior"

foreach ($provider in @("HyperWhisperCloud", "Deepgram", "ElevenLabs", "OpenAI", "Xai")) {
    Assert-Match `
        -Content $ProviderSource `
        -Pattern "StreamingTranscriptionProvider\.$provider" `
        -Label "streaming provider enum includes $provider"
}

foreach ($storageValue in @("hyperwhisperCloud", "deepgram", "elevenLabs", "openAI", "xai")) {
    $escapedStorageValue = [regex]::Escape($storageValue)
    Assert-Match `
        -Content $ProviderSource `
        -Pattern $escapedStorageValue `
        -Label "streaming provider storage value includes $storageValue"
}

Assert-Match `
    -Content $ProviderSource `
    -Pattern "RequiresApiKey.*?HyperWhisperCloud => false.*?Deepgram => true.*?ElevenLabs => true.*?OpenAI => true.*?Xai => true" `
    -Label "only HyperWhisper Cloud avoids direct-provider API-key gating"

Write-Host "Streaming failure wiring verifier passed."
