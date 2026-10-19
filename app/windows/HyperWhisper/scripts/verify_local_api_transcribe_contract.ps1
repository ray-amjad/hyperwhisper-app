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

# Issue #356 item 6: Windows hosts the shared Local API server
# (app/shared-dotnet/HyperWhisper.LocalApi/PortableLocalApi.cs). The route, the
# request parse, the source xor, the size caps and the `file` path policy are
# the shared server's; this head's half is the backend and the path hooks.
$EndpointSource = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "Services\LocalApi\Backend\WindowsLocalApiBackend.Transcription.cs")
$FilePolicySource = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "Services\LocalApi\Backend\LocalApiFilePolicy.cs")
$ServerSource = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "Services\LocalApi\LocalApiServer.cs")
$SharedServerSource = Get-Content -Raw -LiteralPath (Join-Path $RepoRoot "app\shared-dotnet\HyperWhisper.LocalApi\PortableLocalApi.cs")
$OrchestratorSource = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "Services\Transcription\TranscriptionOrchestrator.cs")
$ResponderSource = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "Services\LocalApi\LocalApiErrors.cs")
$MacEndpointSource = Get-Content -Raw -LiteralPath (Join-Path $RepoRoot "app\macos\hyperwhisper\Managers\LocalAPI\Endpoints\TranscribeEndpoint.swift")
$MacRouterSource = Get-Content -Raw -LiteralPath (Join-Path $RepoRoot "app\macos\hyperwhisper\Managers\Transcription\Coordinators\TranscriptionProviderRouter.swift")

Assert-Match `
    -Content $SharedServerSource `
    -Pattern 'app\.MapPost\("/transcribe", Transcribe\).*?ReadFromJsonAsync<TranscribeJsonRequest>.*?backend\.TranscribeAsync\(' `
    -Label "the shared /transcribe route reads the JSON request and hands it to the head's backend"

Assert-Match `
    -Content $EndpointSource `
    -Pattern 'TranscribeAsync\(\s*SharedApi\.AudioUpload upload.*?ResolveMode\(upload\).*?WriteScratchAudio\(upload\).*?orchestrator\.TranscribeAsync\(' `
    -Label "Windows /transcribe maps the upload through mode, scratch audio, and orchestrator resolution"

Assert-Match `
    -Content $EndpointSource `
    -Pattern "applyAiPostProcessing: false" `
    -Label "Windows /transcribe declines the AI rewrite (/post-process is the formatting endpoint)"

# Issues #495 and #498. These three assertions used to read
# `applyPostProcessing: false`, `Text = result.RawText` and
# `else if (applyPostProcessing)`, which described the defect rather than the
# contract: the flag gated the deterministic text passes as well as the LLM
# rewrite, and the response then read the pre-STEP-3 field anyway. The route
# declines the AI rewrite ONLY; filler-word removal, dictated break commands
# and vocabulary replacements are the user's own settings and always run.
Assert-Match `
    -Content $EndpointSource `
    -Pattern "new SharedApi\.TranscriptionResult\(\s*result\.FinalText," `
    -Label "Windows /transcribe returns the text after the deterministic passes, not the provider's raw string"

Assert-Match `
    -Content $OrchestratorSource `
    -Pattern "bool applyAiPostProcessing = true.*?if \(applyAiPostProcessing && mode\.PostProcessingMode != 0\).*?else\s*\{.*?RemoveFillerWords.*?ProcessVoiceCommands.*?ApplyReplacements" `
    -Label "orchestrator gates only the AI rewrite; the deterministic passes run on every path"

Assert-Match `
    -Content $SharedServerSource `
    -Pattern "hasFile == hasBase64.*?Pass exactly one of 'file' or 'audio_base64'\..*?Convert\.FromBase64String\(encoded\).*?ReadAllowedFileAsync\(request\.File!" `
    -Label "the shared /transcribe enforces source xor and decodes base64 itself"

Assert-Match `
    -Content $EndpointSource `
    -Pattern "WriteScratchAudio\(SharedApi\.AudioUpload upload\).*?FileMode\.CreateNew.*?output\.Write\(upload\.Content\.Span\).*?FileShare\.Read" `
    -Label "Windows /transcribe writes the upload to a private, read-locked scratch file"

Assert-Match `
    -Content $SharedServerSource `
    -Pattern "Path\.GetFullPath\(requestedPath\.Trim\(\)\).*?Path\.IsPathFullyQualified.*?IsAllowedPath\(path, options\.AllowedFileRoots\).*?options\.IsAdditionalAllowedFile.*?new FileStream\(path,.*?FileShare\.Read.*?options\.VerifyOpenedFile" `
    -Label "the shared /transcribe canonicalizes the file path, contains it to the allowed roots, and asks the head about the opened handle"

Assert-Match `
    -Content $ServerSource `
    -Pattern "AllowedFileRoots: LocalApiFilePolicy\.AllowedRoots.*?IsAdditionalAllowedFile = LocalApiFilePolicy\.IsAdditionalAllowedFile.*?VerifyOpenedFile = LocalApiFilePolicy\.VerifyOpenedFile" `
    -Label "Windows hands the shared server its trusted recording roots and opened-handle check (issue #740)"

Assert-Match `
    -Content $FilePolicySource `
    -Pattern "VerifyOpenedFile\(FileStream stream\).*?GetFinalDosPath\(stream\.SafeFileHandle\).*?HistoryService\.IsTrustedAudioPath\(openedPath, ResolveRealPath\)" `
    -Label "Windows contains the opened handle's target to trusted recording roots (issue #740)"

Assert-Match `
    -Content $FilePolicySource `
    -Pattern "ResolveRealPath\(string canonicalPath\).*?File\.ResolveLinkTarget\(canonicalPath, returnFinalTarget: true\).*?Directory\.ResolveLinkTarget\(canonicalPath, returnFinalTarget: true\).*?ResolveRealPath\(parent\)" `
    -Label "Windows resolves leaf and ancestor reparse points to the real on-disk target (issue #740 reparse-point bypass)"

# The #740 snapshot is now an in-memory copy: the shared server reads the
# validated handle into a buffer, so the caller cannot swap the file under the
# provider.
Assert-Match `
    -Content $SharedServerSource `
    -Pattern "options\.VerifyOpenedFile.*?input\.CopyToAsync\(output, cancellationToken\).*?output\.ToArray\(\)" `
    -Label "the shared /transcribe copies the validated file handle before provider dispatch"

Assert-Match `
    -Content $FilePolicySource `
    -Pattern "GetFinalDosPath\(SafeFileHandle handle\).*?GetFinalPathNameByHandle\(.*?StripExtendedPathPrefix\(buffer\.ToString\(\)\)" `
    -Label "Windows derives the opened handle's real DOS path from the handle itself, not from the caller's string (issue #740)"

Assert-Match `
    -Content $EndpointSource `
    -Pattern "ExtensionForMime\(string\? mime\).*?mime\.Split\(';', 2\)\[0\]\.Trim\(\)\.ToLowerInvariant\(\).*?`"audio/flac`" or `"audio/x-flac`" => `"flac`".*?`"audio/ogg`" or `"audio/x-ogg`" or `"audio/vorbis`" => `"ogg`".*?`"audio/webm`" => `"webm`".*?`"audio/aac`" => `"aac`"" `
    -Label "Windows /transcribe preserves supported base64 MIME extensions instead of mislabeling them as wav"

Assert-Match `
    -Content $EndpointSource `
    -Pattern "finally\s*\{.*?readLock\.Dispose\(\);.*?File\.Delete\(audioPath\)" `
    -Label "Windows /transcribe deletes per-request scratch files on every exit"

Assert-Match `
    -Content $SharedServerSource `
    -Pattern "catch \(LocalApiFailureException failure\).*?context\.Response\.StatusCode = failure\.HttpStatus;.*?WriteAsync\(failure\.Json\)" `
    -Label "the shared server writes the backend's structured Local API failures verbatim"

Assert-Match `
    -Content $EndpointSource `
    -Pattern "case HwLocalApiEngineId\.WhisperLocal:.*?string\.IsNullOrWhiteSpace\(model\).*?LocalApiErrorCode\.InvalidRequest,\s*WhisperModelRequiredMessage,\s*WhisperModelRequiredHint\);.*?mode\.LocalEngine = `"whisper`";.*?mode\.ModelType = model;" `
    -Label "Windows /transcribe rejects missing Whisper model as INVALID_REQUEST instead of defaulting silently (#1493)"

Assert-Match `
    -Content $EndpointSource `
    -Pattern "WhisperModelRequiredMessage = `"A Whisper model is required\.`";.*?WhisperModelRequiredHint = `"Send model, for example base\.en\.`";" `
    -Label "Windows /transcribe words the missing Whisper model refusal like macOS and the portable head (#1493)"

Assert-Match `
    -Content $EndpointSource `
    -Pattern "default:\s*throw WindowsLocalApiFailure\.Business\(\s*LocalApiErrorCode\.EngineUnavailable,\s*\$`"Unknown engine '\{engine\}'`"\)" `
    -Label "Windows /transcribe rejects unknown engines instead of falling back"

Assert-Match `
    -Content $EndpointSource `
    -Pattern "case `"parakeet`":.*?mode\.ProviderType = `"local`";.*?mode\.LocalEngine = `"parakeet`";.*?mode\.LocalParakeetModel = model \?\? `"parakeet-v3`";" `
    -Label "Windows /transcribe still resolves Parakeet local engine explicitly"

Assert-Match `
    -Content $EndpointSource `
    -Pattern "case `"qwen3`":.*?case `"qwen3asr`":.*?case `"qwen3_asr`":.*?case `"qwen3-asr`":.*?case `"qwen`":.*?mode\.ProviderType = `"local`";.*?mode\.LocalEngine = `"parakeet`";.*?mode\.LocalParakeetModel = model \?\? `"qwen3-asr-0\.6b`";" `
    -Label "Windows /transcribe resolves Qwen3 ASR aliases with the default Qwen model"

# The engine string is no longer matched against the raw `normalized` value:
# `CloudSttCatalog.NormalizeCloudProvider` resolves it first, so an accuracy
# tier such as `elevenLabsScribeV2` also names its provider. The assertion had
# been pinned to the pre-catalog spelling. Open PR #425 rewrites the LOCAL
# `switch` in this method through `resolve_engine_alias` and touches neither
# this cloud arm nor this script, so repairing it here does not collide.
Assert-Match `
    -Content $EndpointSource `
    -Pattern "CloudTranscriptionProviderExtensions\.FromIdentifier\(providerNormalization\.Provider\).*?mode\.ProviderType = `"cloud`";.*?mode\.CloudProvider = cloudProvider\.GetIdentifier\(\)" `
    -Label "Windows /transcribe still resolves cloud provider identifiers explicitly"

Assert-Match `
    -Content $EndpointSource `
    -Pattern "orchestrator\.TranscribeAsync\(.*?cancellationToken: cancellationToken," `
    -Label "Windows /transcribe passes HTTP cancellation into transcription"

Assert-Match `
    -Content $EndpointSource `
    -Pattern "LocalApiApplicationContext\.From\(upload\.ApplicationContext\)\?\.ToApplicationContext\(\)" `
    -Label "Windows /transcribe accepts caller-supplied app context without foreground capture"

Assert-Match `
    -Content $SharedServerSource `
    -Pattern "record TranscribeJsonRequest\(\s*string\? File,.*?JsonPropertyName\(`"audio_base64`"\).*?JsonPropertyName\(`"mime_type`"\).*?JsonPropertyName\(`"mode_id`"\).*?string\? Engine,.*?string\? Model,.*?string\? Language,.*?LocalApiApplicationContext\? ApplicationContext\)" `
    -Label "the /transcribe request fields match the public Local API surface"

Assert-Match `
    -Content $ResponderSource `
    -Pattern "TranscriptionErrorCode\.ModelNotLoaded.*?LocalApiErrorCode\.ModelNotInstalled.*?TranscriptionErrorCode\.ApiKeyMissing.*?LocalApiErrorCode\.MissingApiKey.*?TranscriptionErrorCode\.UnsupportedFormat.*?LocalApiErrorCode\.AudioDecodeFailed" `
    -Label "Windows /transcribe maps typed transcription failures to stable API codes"

# Issue #530. This used to assert `text: text` — the provider's string on the
# wire, with none of the deterministic passes applied — which described the
# defect rather than the contract, exactly as three Windows assertions did
# before #519. Both heads now run filler-word removal, dictated break commands
# and the user's vocabulary replacements, and neither runs the AI rewrite.
Assert-Match `
    -Content $MacEndpointSource `
    -Pattern "resolution\.provider\.transcribe\(.*?let finalText = Self\.applyDeterministicTextPasses\(.*?let response = TranscribeResponse\(.*?text: finalText" `
    -Label "macOS /transcribe returns the text after the deterministic passes, not the provider's raw string"

Assert-Match `
    -Content $MacEndpointSource `
    -Pattern "removeFillerWords\s*\r?\n?\s*\? TranscriptionTextProcessing\.removeFillerWords\(text, language: language\).*?TranscriptionTextProcessing\.processVoiceCommands\(withoutFillers\).*?vocabularyProcessor\.applyVocabularyReplacements\(withCommands, mode: mode\)" `
    -Label "macOS /transcribe runs the same three deterministic passes, in the same order, as the Windows orchestrator"

Assert-Match `
    -Content $MacEndpointSource `
    -Pattern "let vocabulary = PersistenceController\.shared\.fetchAllVocabularyItems\(\)" `
    -Label "macOS /transcribe hands the provider the user's real vocabulary, not an empty array"

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
    -Pattern "throw TranscriptionError\.providerNotAvailable\(provider: engine, reason: `"Unknown engine '\\\(engine\)'`"\).*?case \.whisperLocal:.*?throw TranscribeEndpoint\.APIInputError\.whisperModelRequired" `
    -Label "macOS /transcribe rejects missing Whisper models (#1493) and unknown engines"

Assert-Match `
    -Content $MacEndpointSource `
    -Pattern "static let whisperModelRequired = APIInputError\(\s*code: \.invalidRequest,\s*message: `"A Whisper model is required\.`",\s*hint: `"Send model, for example base\.en\.`"" `
    -Label "macOS /transcribe words the missing Whisper model refusal like Windows and the portable head (#1493)"

Write-Host "Local API /transcribe contract verifier passed."
