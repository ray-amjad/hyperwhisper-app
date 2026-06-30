param()

$ErrorActionPreference = "Stop"

function Assert-Match {
    param(
        [Parameter(Mandatory = $true)][string]$Content,
        [Parameter(Mandatory = $true)][string]$Pattern,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if (-not [regex]::IsMatch($Content, $Pattern, [System.Text.RegularExpressions.RegexOptions]::Singleline)) {
        throw "Missing expected Local LLM delete-guard wiring: $Label"
    }
}

$ProjectRoot = Split-Path -Parent $PSScriptRoot
$RepoRoot = Resolve-Path (Join-Path $ProjectRoot "..\..\..")

$ModelsSettingsSource = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "Views\Pages\Settings\ModelsSettingsPage.xaml.cs")
$LocalLlmModelServiceSource = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "Services\LocalLlmModelService.cs")
$PostProcessingSource = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "Services\PostProcessingService.cs")
$ModesEndpointSource = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "Services\LocalApi\Endpoints\ModesEndpoints.cs")
$ModeEditorSource = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "Views\Windows\ModeEditorWindow.xaml.cs")
$MacModeModelsSource = Get-Content -Raw -LiteralPath (Join-Path $RepoRoot "app\macos\hyperwhisper\Views\Modes\Models\ModeModels.swift")
$MacModeEditorSource = Get-Content -Raw -LiteralPath (Join-Path $RepoRoot "app\macos\hyperwhisper\Views\Modes\ModeEditorView.swift")
$MacModelLibrarySource = Get-Content -Raw -LiteralPath (Join-Path $RepoRoot "app\macos\hyperwhisper\Views\ModelLibrary\ModelLibraryView.swift")
$MacPostProcessorSource = Get-Content -Raw -LiteralPath (Join-Path $RepoRoot "app\macos\hyperwhisper\Managers\Transcription\PostProcessing\AIPostProcessor.swift")

Assert-Match `
    -Content $PostProcessingSource `
    -Pattern "provider == PostProcessingProvider\.LocalLlm\s*\?\s*mode\.LocalPostProcessingModel \?\? mode\.LanguageModel\s*:\s*mode\.LanguageModel" `
    -Label "runtime Local LLM post-processing falls back from LocalPostProcessingModel to LanguageModel"

Assert-Match `
    -Content $ModesEndpointSource `
    -Pattern "provider == PostProcessingProvider\.LocalLlm\s*\?\s*mode\.LocalPostProcessingModel \?\? mode\.LanguageModel\s*:\s*mode\.LanguageModel" `
    -Label "Local API mode validation uses the same Local LLM fallback"

Assert-Match `
    -Content $ModeEditorSource `
    -Pattern "ppProvider == PostProcessingProvider\.LocalLlm\.ToStringValue\(\)\s*\?\s*mode\.LocalPostProcessingModel \?\? mode\.LanguageModel\s*:\s*mode\.LanguageModel" `
    -Label "Mode Editor displays the same effective Local LLM model"

Assert-Match `
    -Content $ModelsSettingsSource `
    -Pattern "private static bool IsLocalLlmModelInUse\(string modelId, out string modeName\).*?var targetModelId = LanguageModelInfo\.MigrateModelId\(modelId\).*?PostProcessingProvider\.LocalLlm\.ToStringValue\(\).*?LanguageModelInfo\.MigrateModelId\(m\.LocalPostProcessingModel \?\? m\.LanguageModel\).*?targetModelId" `
    -Label "Model Library delete guard blocks effective Local LLM model, including legacy LanguageModel fallback"

Assert-Match `
    -Content $ModelsSettingsSource `
    -Pattern "private void DeleteLocalLlm\(LibraryModelViewModel row, LocalLlmModelInfo model\).*?IsLocalLlmModelInUse\(model\.Id, out var modeName\).*?ShowModelInUse\(row\.Model\.DisplayName, modeName\).*?ConfirmDelete\(row\.Model\.DisplayName\).*?var result = _localLlmService\.DeleteModel\(model\).*?if \(result\.IsFailure\).*?ShowDeleteFailed\(row\.Model\.DisplayName, result\.Error \?\? Loc\.S\(`"common\.error`"\)\).*?return;.*?RebuildLibrary\(\);" `
    -Label "Local LLM deletion checks usage before confirmation and handles disk deletion failure before rebuilding"

Assert-Match `
    -Content $LocalLlmModelServiceSource `
    -Pattern "public Result DeleteModel\(LocalLlmModelInfo model\).*?try.*?File\.Delete\(path\).*?return Result\.Success\(\).*?catch \(Exception ex\) when \(ex is IOException or UnauthorizedAccessException or System\.Security\.SecurityException\).*?return Result\.Failure\(ex\.Message, ex\)" `
    -Label "Local LLM delete catches expected Windows file deletion failures and returns Result"

Assert-Match `
    -Content $ModelsSettingsSource `
    -Pattern "private static void ShowDeleteFailed\(string modelName, string error\).*?Loc\.S\(`"settings\.models\.deleteFailed\.message`", modelName, error\).*?Loc\.S\(`"settings\.models\.deleteFailed\.title`".*?MessageBoxImage\.Error" `
    -Label "Local LLM delete failures surface a localized error dialog"

Assert-Match `
    -Content $MacModeModelsSource `
    -Pattern "if processingMode == \.local.*?self\.languageModel = mode\.languageModel \?\? PostProcessingProvider\.localLLM\.defaultModel.*?self\.postProcessingProvider = PostProcessingProvider\.localLLM\.rawValue" `
    -Label "macOS Local LLM post-processing uses the single languageModel field"

Assert-Match `
    -Content $MacModeEditorSource `
    -Pattern "postProcessingProvider = PostProcessingProvider\.localLLM\.rawValue.*?languageModel = PostProcessingProvider\.localLLM\.defaultModel" `
    -Label "macOS Mode Editor stores Local LLM selection in languageModel"

Assert-Match `
    -Content $MacModelLibrarySource `
    -Pattern "matchesProvider = provider == PostProcessingProvider\.localLLM\.rawValue.*?matchesModel = \(mode\.languageModel \?\? `"`"\)\.caseInsensitiveCompare\(modelId\) == \.orderedSame.*?localLLMManager\.deleteModel\(modelId\)" `
    -Label "macOS Model Library delete guard checks the Local LLM languageModel before deletion"

Assert-Match `
    -Content $MacPostProcessorSource `
    -Pattern "var languageModel = mode\.languageModel \?\? `"`".*?if provider == \.localLLM" `
    -Label "macOS Local LLM runtime reads languageModel"

Write-Host "Local LLM delete-guard verifier passed."
