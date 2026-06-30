param()

$ErrorActionPreference = "Stop"

function Assert-Match {
    param(
        [Parameter(Mandatory = $true)][string]$Content,
        [Parameter(Mandatory = $true)][string]$Pattern,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if (-not [regex]::IsMatch($Content, $Pattern, [System.Text.RegularExpressions.RegexOptions]::Singleline)) {
        throw "Missing expected Local API /transcribe contract wiring: $Label"
    }
}

$ProjectRoot = Split-Path -Parent $PSScriptRoot
$RepoRoot = Resolve-Path (Join-Path $ProjectRoot "..\..\..")

$EndpointSource = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "Services\LocalApi\Endpoints\TranscribeEndpoints.cs")
$OrchestratorSource = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "Services\Transcription\TranscriptionOrchestrator.cs")
$TypesSource = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "Services\LocalApi\LocalApiTypes.cs")
$ResponderSource = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "Services\LocalApi\LocalApiErrors.cs")
$MacEndpointSource = Get-Content -Raw -LiteralPath (Join-Path $RepoRoot "app\macos\hyperwhisper\Managers\LocalAPI\Endpoints\TranscribeEndpoint.swift")
$MacRouterSource = Get-Content -Raw -LiteralPath (Join-Path $RepoRoot "app\macos\hyperwhisper\Managers\Transcription\Coordinators\TranscriptionProviderRouter.swift")

Assert-Match `
    -Content $EndpointSource `
    -Pattern 'app\.MapPost\("/transcribe".*?ReadFromJsonAsync<TranscribeRequest>.*?ResolveAudioSource\(req\).*?ResolveMode\(req\).*?orchestrator\.TranscribeAsync\(' `
    -Label "Windows /transcribe maps JSON request through audio, mode, and orchestrator resolution"

Assert-Match `
    -Content $EndpointSource `
    -Pattern "applyPostProcessing: false" `
    -Label "Windows /transcribe skips the GUI post-processing pipeline"

Assert-Match `
    -Content $EndpointSource `
    -Pattern "Text = result\.RawText" `
    -Label "Windows /transcribe returns raw transcription text"

Assert-Match `
    -Content $OrchestratorSource `
    -Pattern "bool applyPostProcessing = true.*?if \(applyPostProcessing && mode\.PostProcessingMode != 0\).*?else if \(applyPostProcessing\)" `
    -Label "orchestrator preserves GUI post-processing by default but allows API callers to skip it"

Assert-Match `
    -Content $EndpointSource `
    -Pattern "ResolveAudioSource\(TranscribeRequest req\).*?Pass either 'file' or 'audio_base64', not both.*?Provide 'file' \(absolute path\) or 'audio_base64' \+ 'mime_type'.*?File\.Exists\(canonicalPath\).*?new FileStream\(.*?resolvedPath.*?FileShare\.Read.*?Convert\.FromBase64String.*?File\.WriteAllBytes\(tempPath, data\)" `
    -Label "Windows /transcribe enforces source xor, readable files, and decoded temp files"

Assert-Match `
    -Content $EndpointSource `
    -Pattern "Path\.GetFullPath\(trimmedFile!\).*?HistoryService\.IsTrustedAudioPath\(canonicalPath\).*?LocalApiErrorCode\.FileNotAllowed.*?ResolveRealPath\(canonicalPath\).*?HistoryService\.IsTrustedAudioPath\(resolvedPath, ResolveRealPath\).*?GetFinalDosPath\(sourceStream\.SafeFileHandle\).*?HistoryService\.IsTrustedAudioPath\(openedPath, ResolveRealPath\)" `
    -Label "Windows /transcribe canonicalizes the file path, resolves reparse points, and contains both the lexical path and opened handle target to trusted recording roots (issue #740)"

Assert-Match `
    -Content $EndpointSource `
    -Pattern "ResolveRealPath\(string canonicalPath\).*?File\.ResolveLinkTarget\(canonicalPath, returnFinalTarget: true\).*?Directory\.ResolveLinkTarget\(canonicalPath, returnFinalTarget: true\).*?ResolveRealPath\(parent\)" `
    -Label "Windows /transcribe resolves leaf and ancestor reparse points to the real on-disk target (issue #740 reparse-point bypass)"

Assert-Match `
    -Content $EndpointSource `
    -Pattern "GetFinalPathNameByHandle\(.*?SafeFileHandle.*?StripExtendedPathPrefix\(buffer\.ToString\(\)\).*?CreateLocalApiSnapshotPath\(openedPath\).*?sourceStream\.CopyTo\(snapshot\).*?return \(snapshotPath, true, readLock\)" `
    -Label "Windows /transcribe snapshots the validated file handle into a locked temp path before provider dispatch"

Assert-Match `
    -Content $EndpointSource `
    -Pattern "ExtensionForMime\(string\? mime\).*?mime\.Split\(';', 2\)\[0\]\.Trim\(\)\.ToLowerInvariant\(\).*?`"audio/flac`" or `"audio/x-flac`" => `"flac`".*?`"audio/ogg`" or `"audio/x-ogg`" or `"audio/vorbis`" => `"ogg`".*?`"audio/webm`" => `"webm`".*?`"audio/aac`" => `"aac`"" `
    -Label "Windows /transcribe preserves supported base64 MIME extensions instead of mislabeling them as wav"

Assert-Match `
    -Content $EndpointSource `
    -Pattern "finally\s*\{.*?if \(tempFileCreated\).*?File\.Delete\(audioPath\)" `
    -Label "Windows /transcribe deletes per-request base64 temp files on every exit"

Assert-Match `
    -Content $EndpointSource `
    -Pattern "catch \(ApiInputException aiex\).*?LocalApiResponder\.Failure\(aiex\.Code, aiex\.Message, aiex\.Hint\)" `
    -Label "Windows /transcribe returns structured Local API failures for resolver input errors"

Assert-Match `
    -Content $EndpointSource `
    -Pattern "case `"whisperlocal`":.*?case `"whisper`":.*?case `"libwhisper`":.*?string\.IsNullOrWhiteSpace\(model\).*?Missing 'model' for whisperLocal engine.*?mode\.LocalEngine = `"whisper`";.*?mode\.ModelType = model;" `
    -Label "Windows /transcribe rejects missing Whisper model instead of defaulting silently"

Assert-Match `
    -Content $EndpointSource `
    -Pattern "default:\s*throw new ApiInputException\(\s*LocalApiErrorCode\.EngineUnavailable,\s*\$`"Unknown engine '\{engine\}'`"\)" `
    -Label "Windows /transcribe rejects unknown engines instead of falling back"

Assert-Match `
    -Content $EndpointSource `
    -Pattern "case `"parakeet`":.*?mode\.ProviderType = `"local`";.*?mode\.LocalEngine = `"parakeet`";.*?mode\.LocalParakeetModel = model \?\? `"parakeet-v3`";" `
    -Label "Windows /transcribe still resolves Parakeet local engine explicitly"

Assert-Match `
    -Content $EndpointSource `
    -Pattern "case `"qwen3`":.*?case `"qwen3asr`":.*?case `"qwen3_asr`":.*?case `"qwen3-asr`":.*?case `"qwen`":.*?mode\.ProviderType = `"local`";.*?mode\.LocalEngine = `"parakeet`";.*?mode\.LocalParakeetModel = model \?\? `"qwen3-asr-0\.6b`";" `
    -Label "Windows /transcribe resolves Qwen3 ASR aliases with the default Qwen model"

Assert-Match `
    -Content $EndpointSource `
    -Pattern "CloudTranscriptionProviderExtensions\.FromIdentifier\(normalized\).*?mode\.ProviderType = `"cloud`";.*?mode\.CloudProvider = cloudProvider\.GetIdentifier\(\)" `
    -Label "Windows /transcribe still resolves cloud provider identifiers explicitly"

Assert-Match `
    -Content $EndpointSource `
    -Pattern "ctx\.RequestAborted" `
    -Label "Windows /transcribe passes HTTP cancellation into transcription"

Assert-Match `
    -Content $EndpointSource `
    -Pattern "ApplicationContext\?\.ToApplicationContext\(\)" `
    -Label "Windows /transcribe accepts caller-supplied app context without foreground capture"

Assert-Match `
    -Content $TypesSource `
    -Pattern "class TranscribeRequest.*?JsonPropertyName\(`"file`"\).*?JsonPropertyName\(`"audio_base64`"\).*?JsonPropertyName\(`"mime_type`"\).*?JsonPropertyName\(`"mode_id`"\).*?JsonPropertyName\(`"engine`"\).*?JsonPropertyName\(`"model`"\).*?JsonPropertyName\(`"language`"\).*?JsonPropertyName\(`"applicationContext`"\)" `
    -Label "Windows /transcribe request fields match the public Local API surface"

Assert-Match `
    -Content $ResponderSource `
    -Pattern "TranscriptionErrorCode\.ModelNotLoaded.*?LocalApiErrorCode\.ModelNotInstalled.*?TranscriptionErrorCode\.ApiKeyMissing.*?LocalApiErrorCode\.MissingApiKey.*?TranscriptionErrorCode\.UnsupportedFormat.*?LocalApiErrorCode\.AudioDecodeFailed" `
    -Label "Windows /transcribe maps typed transcription failures to stable API codes"

Assert-Match `
    -Content $MacEndpointSource `
    -Pattern "resolution\.provider\.transcribe\(.*?let response = TranscribeResponse\(.*?text: text" `
    -Label "macOS /transcribe returns provider transcription text without the GUI post-processing pipeline"

Assert-Match `
    -Content $MacEndpointSource `
    -Pattern "resolveAudioSource\(req: req\).*?defer \{ audioResolution\.cleanup\(\) \}.*?hasFile && hasBase64.*?audio_base64.*?cleanup: \{" `
    -Label "macOS /transcribe has the same audio-source xor and temp cleanup shape"

Assert-Match `
    -Content $MacEndpointSource `
    -Pattern "extensionForMime\(_ mime: String\?\).*?`"audio/flac`", `"audio/x-flac`": return `"flac`".*?`"audio/ogg`", `"audio/x-ogg`", `"audio/vorbis`": return `"ogg`".*?`"audio/webm`": return `"webm`".*?`"audio/aac`": return `"aac`"" `
    -Label "macOS /transcribe preserves the same supported base64 MIME extensions"

Assert-Match `
    -Content $MacRouterSource `
    -Pattern "case `"whisperlocal`", `"whisper`", `"libwhisper`":.*?Missing 'model' for whisperLocal engine.*?default:\s*throw TranscriptionError\.providerNotAvailable\(provider: engine, reason: `"Unknown engine '\\\(engine\)'`"\)" `
    -Label "macOS /transcribe rejects missing Whisper models and unknown engines"

Write-Host "Local API /transcribe contract verifier passed."
