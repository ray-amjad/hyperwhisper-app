param(
    [switch] $SkipSchema
)

$ErrorActionPreference = "Stop"

$SchemaPath = Join-Path $PSScriptRoot "hyperwhisper-backup.schema.json"
$ExamplesPath = Join-Path $PSScriptRoot "examples"
$WindowsExamplePath = Join-Path $ExamplesPath "windows-export.hwbackup.json"

function Assert-True {
    param(
        [bool] $Condition,
        [string] $Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Invoke-AjvValidate {
    param(
        [string] $DataPath,
        [bool] $ShouldPass = $true
    )

    npx --yes ajv-cli@5.0.0 validate `
        --spec=draft2020 `
        --strict=false `
        -s $SchemaPath `
        -d $DataPath

    $exitCode = $LASTEXITCODE
    if ($ShouldPass -and $exitCode -ne 0) {
        throw "Schema validation failed unexpectedly for $DataPath with exit code $exitCode."
    }
    if (-not $ShouldPass -and $exitCode -eq 0) {
        throw "Schema validation passed unexpectedly for $DataPath."
    }
}

if (-not $SkipSchema) {
    $exampleFiles = Get-ChildItem -LiteralPath $ExamplesPath -Filter "*.hwbackup.json"

    foreach ($exampleFile in $exampleFiles) {
        Invoke-AjvValidate -DataPath $exampleFile.FullName
    }

    $schemaTempDir = Join-Path ([System.IO.Path]::GetTempPath()) "hyperwhisper-backup-schema-$([guid]::NewGuid())"
    New-Item -ItemType Directory -Path $schemaTempDir | Out-Null
    try {
        $strictRoot = Get-Content -LiteralPath $WindowsExamplePath -Raw | ConvertFrom-Json
        $strictRoot | Add-Member -MemberType NoteProperty -Name "unexpectedSharedRootField" -Value "reject"
        $strictRootPath = Join-Path $schemaTempDir "reject-unknown-root.hwbackup.json"
        $strictRoot | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath $strictRootPath -Encoding UTF8
        Invoke-AjvValidate -DataPath $strictRootPath -ShouldPass $false

        $allowedExtensions = Get-Content -LiteralPath $WindowsExamplePath -Raw | ConvertFrom-Json
        $allowedExtensions.platformExtensions.windows | Add-Member -MemberType NoteProperty -Name "futureWindowsOnlyField" -Value "preserve"
        if ($null -eq $allowedExtensions.apiKeys) {
            $allowedExtensions | Add-Member -MemberType NoteProperty -Name "apiKeys" -Value ([pscustomobject]@{})
        }
        $allowedExtensions.apiKeys | Add-Member -MemberType NoteProperty -Name "futureprovider" -Value "placeholder"
        $allowedExtensionsPath = Join-Path $schemaTempDir "allow-extension-points.hwbackup.json"
        $allowedExtensions | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath $allowedExtensionsPath -Encoding UTF8
        Invoke-AjvValidate -DataPath $allowedExtensionsPath
    }
    finally {
        Remove-Item -LiteralPath $schemaTempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$windows = Get-Content -LiteralPath $WindowsExamplePath -Raw | ConvertFrom-Json

Assert-True ($windows.platform -eq "windows") "Windows fixture must declare platform=windows."
Assert-True ($null -ne $windows.settings.streaming) "Windows fixture must include settings.streaming."
Assert-True ($windows.settings.streaming.enabled -is [bool]) "settings.streaming.enabled must be boolean."
Assert-True ([string]::IsNullOrWhiteSpace($windows.settings.streaming.provider) -eq $false) "settings.streaming.provider must be populated."
Assert-True ([string]::IsNullOrWhiteSpace($windows.settings.streaming.language) -eq $false) "settings.streaming.language must be populated."
Assert-True ([string]::IsNullOrWhiteSpace($windows.settings.streaming.deepgramModel) -eq $false) "settings.streaming.deepgramModel must be populated."
Assert-True ($windows.settings.streaming.fastFormatting -is [bool]) "settings.streaming.fastFormatting must be boolean."
Assert-True ([string]::IsNullOrWhiteSpace($windows.settings.streaming.shortcut) -eq $false) "settings.streaming.shortcut must be populated."

$windowsPlatformSettings = $windows.platformExtensions.windows.settings
Assert-True ($null -ne $windowsPlatformSettings) "Windows fixture must include platformExtensions.windows.settings."
Assert-True ($windowsPlatformSettings.streamingEnabled -eq $windows.settings.streaming.enabled) "Windows platform streamingEnabled must mirror settings.streaming.enabled."
Assert-True ($windowsPlatformSettings.streamingProvider -eq $windows.settings.streaming.provider) "Windows platform streamingProvider must mirror settings.streaming.provider."
Assert-True ($windowsPlatformSettings.streamingLanguage -eq $windows.settings.streaming.language) "Windows platform streamingLanguage must mirror settings.streaming.language."
Assert-True ($windowsPlatformSettings.streamingDeepgramModel -eq $windows.settings.streaming.deepgramModel) "Windows platform streamingDeepgramModel must mirror settings.streaming.deepgramModel."
Assert-True ($windowsPlatformSettings.streamingFastFormatting -eq $windows.settings.streaming.fastFormatting) "Windows platform streamingFastFormatting must mirror settings.streaming.fastFormatting."
Assert-True ($windowsPlatformSettings.streamingShortcut -eq $windows.settings.streaming.shortcut) "Windows platform streamingShortcut must mirror settings.streaming.shortcut."

$modes = @($windows.modes)
$tiers = @($modes | ForEach-Object { $_.cloudAccuracyTier })
Assert-True ($tiers -contains "deepgramNova3") "Windows fixture must cover deepgramNova3 tier."
Assert-True ($tiers -contains "grokStt") "Windows fixture must cover grokStt tier."
Assert-True ($tiers -contains "azureMaiTranscribe") "Windows fixture must cover azureMaiTranscribe provider/tier alias import shape."

$cloudPostProcessingModels = @($modes | ForEach-Object { $_.cloudPostProcessingModel })
Assert-True ($cloudPostProcessingModels -contains "claudeHaiku") "Windows fixture must cover claudeHaiku cloud post-processing model."
Assert-True ($cloudPostProcessingModels -contains "grokFast") "Windows fixture must cover grokFast cloud post-processing model."
Assert-True ($cloudPostProcessingModels -contains "cerebrasGptOss120B") "Windows fixture must cover cerebrasGptOss120B cloud post-processing model."

$postProcessingProviders = @($modes | ForEach-Object { $_.postProcessingProvider })
Assert-True ($postProcessingProviders -contains "local") "Windows fixture must cover local LLM post-processing provider."
Assert-True (@($modes | Where-Object { $_.localPostProcessingModel -eq "gemma-3-4b-it-q4_0" }).Count -gt 0) "Windows fixture must cover localPostProcessingModel."

foreach ($mode in $modes) {
    $winMode = $mode.platformExtensions.windows
    Assert-True ($null -ne $winMode) "Mode '$($mode.name)' must include platformExtensions.windows."
    Assert-True ($winMode.cloudAccuracyTier -eq $mode.cloudAccuracyTier) "Mode '$($mode.name)' Windows cloudAccuracyTier must mirror the top-level normalized tier."
    Assert-True ($winMode.cloudPostProcessingModel -eq $mode.cloudPostProcessingModel) "Mode '$($mode.name)' Windows cloudPostProcessingModel must mirror the top-level normalized model."
    Assert-True ($winMode.localPostProcessingModel -eq $mode.localPostProcessingModel) "Mode '$($mode.name)' Windows localPostProcessingModel must mirror the top-level value."
}

$sources = @($windows.vocabulary | ForEach-Object { $_.source })
Assert-True ($sources -contains "manual") "Windows fixture must cover manual vocabulary source."
Assert-True ($sources -contains "auto-learn") "Windows fixture must cover non-manual vocabulary source."

Write-Host "Backup fixture validation passed."
