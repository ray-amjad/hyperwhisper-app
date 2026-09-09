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

# `ReadFromJsonAsync` until the body-size limit landed; the route now reads
# through LocalApiLimits so an over-limit upload is a business failure rather
# than "Invalid JSON body". This assertion had been failing on main since that
# change, which is why it did not catch issues #495 and #498.
Assert-Match `
    -Content $EndpointSource `
    -Pattern 'app\.MapPost\("/transcribe".*?ReadJsonBodyAsync<TranscribeRequest>.*?ResolveAudioSource\(req\).*?ResolveMode\(req\).*?orchestrator\.TranscribeAsync\(' `
    -Label "Windows /transcribe maps JSON request through audio, mode, and orchestrator resolution"

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
    -Pattern "Text = result\.FinalText" `
    -Label "Windows /transcribe returns the text after the deterministic passes, not the provider's raw string"

Assert-Match `
    -Content $OrchestratorSource `
    -Pattern "bool applyAiPostProcessing = true.*?if \(applyAiPostProcessing && mode\.PostProcessingMode != 0\).*?else\s*\{.*?RemoveFillerWords.*?ProcessVoiceCommands.*?ApplyReplacements" `
    -Label "orchestrator gates only the AI rewrite; the deterministic passes run on every path"

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

# One assertion used to span BOTH halves of this wiring in a single
# `.*?`-joined pattern, and it failed for a reason that had nothing to do with
# security: `GetFinalDosPath` — which holds `GetFinalPathNameByHandle` and
# `StripExtendedPathPrefix(buffer.ToString())` — now sits BELOW
# `ResolveAudioSource` in the file, so the ingredients no longer appear in the
# order the pattern demanded. Every ingredient is still present and the
# containment assertion above still passes, so the #740 wiring is intact; only
# a source-order dependency this script should never have had was broken.
# Split into the two units that actually exist, each ordered within itself.
Assert-Match `
    -Content $EndpointSource `
    -Pattern "GetFinalDosPath\(sourceStream\.SafeFileHandle\).*?IsTrustedAudioPath\(openedPath, ResolveRealPath\).*?CreateLocalApiSnapshotPath\(openedPath\).*?sourceStream\.CopyTo\(snapshot\).*?return \(snapshotPath, true, readLock\)" `
    -Label "Windows /transcribe snapshots the validated file handle into a locked temp path before provider dispatch"

Assert-Match `
    -Content $EndpointSource `
    -Pattern "GetFinalDosPath\(SafeFileHandle handle\).*?GetFinalPathNameByHandle\(.*?StripExtendedPathPrefix\(buffer\.ToString\(\)\)" `
    -Label "Windows /transcribe derives the opened handle's real DOS path from the handle itself, not from the caller's string (issue #740)"

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
    -Pattern "case `"whisperlocal`", `"whisper`", `"libwhisper`":.*?Missing 'model' for whisperLocal engine.*?default:\s*throw TranscriptionError\.providerNotAvailable\(provider: engine, reason: `"Unknown engine '\\\(engine\)'`"\)" `
    -Label "macOS /transcribe rejects missing Whisper models and unknown engines"

Write-Host "Local API /transcribe contract verifier passed."
