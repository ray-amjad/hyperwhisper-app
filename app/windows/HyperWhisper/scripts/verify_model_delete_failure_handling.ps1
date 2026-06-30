param()

$ErrorActionPreference = "Stop"

function Assert-Match {
    param(
        [Parameter(Mandatory = $true)][string]$Content,
        [Parameter(Mandatory = $true)][string]$Pattern,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if (-not [regex]::IsMatch($Content, $Pattern, [System.Text.RegularExpressions.RegexOptions]::Singleline)) {
        throw "Missing expected model delete failure handling: $Label"
    }
}

$ProjectRoot = Split-Path -Parent $PSScriptRoot
$RepoRoot = Resolve-Path (Join-Path $ProjectRoot "..\..\..")

$ModelsSettingsSource = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "Views\Pages\Settings\ModelsSettingsPage.xaml.cs")
$WhisperServiceSource = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "Services\WhisperModelService.cs")
$ParakeetServiceSource = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "Services\ParakeetModelService.cs")
$LocalLlmServiceSource = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "Services\LocalLlmModelService.cs")
$MacWhisperSource = Get-Content -Raw -LiteralPath (Join-Path $RepoRoot "app\macos\hyperwhisper\Managers\Transcription\Models\WhisperModelManager.swift")
$MacParakeetSource = Get-Content -Raw -LiteralPath (Join-Path $RepoRoot "app\macos\hyperwhisper\Managers\Transcription\Models\ParakeetModelManager.swift")
$MacModelLibrarySource = Get-Content -Raw -LiteralPath (Join-Path $RepoRoot "app\macos\hyperwhisper\Views\ModelLibrary\ModelLibraryView.swift")

Assert-Match `
    -Content $WhisperServiceSource `
    -Pattern "public Result DeleteModel\(WhisperModelInfo model\).*?try.*?File\.Delete\(path\).*?return Result\.Success\(\).*?catch \(Exception ex\) when \(ex is IOException or UnauthorizedAccessException or System\.Security\.SecurityException\).*?return Result\.Failure\(ex\.Message, ex\)" `
    -Label "Whisper delete catches expected Windows filesystem failures and returns Result"

Assert-Match `
    -Content $ParakeetServiceSource `
    -Pattern "public Result DeleteModel\(ParakeetModelInfo model\).*?try.*?Directory\.Delete\(modelDir, true\).*?return Result\.Success\(\).*?catch \(Exception ex\) when \(ex is IOException or UnauthorizedAccessException or System\.Security\.SecurityException\).*?return Result\.Failure\(ex\.Message, ex\)" `
    -Label "Parakeet delete catches expected Windows filesystem failures and returns Result"

Assert-Match `
    -Content $LocalLlmServiceSource `
    -Pattern "public Result DeleteModel\(LocalLlmModelInfo model\).*?catch \(Exception ex\) when \(ex is IOException or UnauthorizedAccessException or System\.Security\.SecurityException\).*?return Result\.Failure\(ex\.Message, ex\)" `
    -Label "Local LLM delete keeps the same guarded Result pattern"

Assert-Match `
    -Content $ModelsSettingsSource `
    -Pattern "private void DeleteWhisper\(LibraryModelViewModel row, WhisperModelInfo model\).*?IsWhisperModelInUse\(model\.Type, out var modeName\).*?ConfirmDelete\(row\.Model\.DisplayName\).*?var result = _whisperService\.DeleteModel\(model\).*?if \(result\.IsFailure\).*?ShowDeleteFailed\(row\.Model\.DisplayName, result\.Error \?\? Loc\.S\(`"common\.error`"\)\).*?return;.*?RebuildLibrary\(\);" `
    -Label "Whisper delete caller handles Result failure with localized dialog"

Assert-Match `
    -Content $ModelsSettingsSource `
    -Pattern "private void DeleteParakeet\(LibraryModelViewModel row, ParakeetModelInfo model\).*?IsParakeetModelInUse\(model\.Id, out var modeName\).*?ConfirmDelete\(row\.Model\.DisplayName\).*?var result = _parakeetService\.DeleteModel\(model\).*?if \(result\.IsFailure\).*?ShowDeleteFailed\(row\.Model\.DisplayName, result\.Error \?\? Loc\.S\(`"common\.error`"\)\).*?return;.*?RebuildLibrary\(\);" `
    -Label "Parakeet delete caller handles Result failure with localized dialog"

Assert-Match `
    -Content $ModelsSettingsSource `
    -Pattern "private static void ShowDeleteFailed\(string modelName, string error\).*?Loc\.S\(`"settings\.models\.deleteFailed\.message`", modelName, error\).*?Loc\.S\(`"settings\.models\.deleteFailed\.title`".*?MessageBoxImage\.Error" `
    -Label "model delete failures surface a localized error dialog"

Assert-Match `
    -Content $MacWhisperSource `
    -Pattern "func deleteModel\(_ model: WhisperCppModel\) async.*?do \{.*?FileManager\.default\.removeItem\(at: url\).*?\} catch \{.*?errorMessage = `"Failed to delete" `
    -Label "macOS Whisper delete catches and reports filesystem failures"

Assert-Match `
    -Content $MacParakeetSource `
    -Pattern "func deleteModel\(_ modelId: String\).*?do \{.*?removeItem.*?\} catch \{.*?errorMessage" `
    -Label "macOS Parakeet delete catches and reports filesystem failures"

Assert-Match `
    -Content $MacModelLibrarySource `
    -Pattern "checkAndRemoveModel\(_ modelId: String\).*?showCannotDeleteAlertIfNeeded.*?await whisperManager\.deleteModel\(model\)" `
    -Label "macOS Model Library blocks in-use Whisper models before delete"

Assert-Match `
    -Content $MacModelLibrarySource `
    -Pattern "checkAndRemoveParakeetModel\(_ modelId: String\).*?showCannotDeleteAlertIfNeeded.*?parakeetManager\.deleteModel\(modelId\)" `
    -Label "macOS Model Library blocks in-use Parakeet models before delete"

Write-Host "Model delete failure handling verifier passed."
