param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..\..\..")).Path
)

$ErrorActionPreference = "Stop"

function Read-Text([string]$Path) {
    return Get-Content -LiteralPath $Path -Raw
}

function Assert-Contains([string]$Text, [string]$Pattern, [string]$Message) {
    if ($Text -notmatch $Pattern) {
        throw $Message
    }
}

function Assert-NotContains([string]$Text, [string]$Pattern, [string]$Message) {
    if ($Text -match $Pattern) {
        throw $Message
    }
}

$appRoot = Join-Path $RepoRoot "app\windows\HyperWhisper"
$appPaths = Read-Text (Join-Path $appRoot "Services\AppPaths.cs")
$appSource = Read-Text (Join-Path $appRoot "App.xaml.cs")

Assert-Contains $appPaths 'HYPERWHISPER_WINDOWS_APPDATA_ROOT' "AppPaths must expose the isolated app-data root environment variable."
Assert-Contains $appPaths 'IsAppDataRootOverridden' "AppPaths must expose whether an isolated root is active."
Assert-Contains $appPaths 'CredentialResource' "AppPaths must namespace Credential Manager resources for isolated profiles."
Assert-Contains $appPaths '\.Test\.' "Isolated Credential Manager resources must not reuse the production HyperWhisper resource."
Assert-Contains $appPaths 'ProfileRecordingsDirectory' "AppPaths must provide a profile-scoped recordings directory."
Assert-Contains $appSource 'IsFirstLaunch && !AppPaths\.IsAppDataRootOverridden' "Isolated first launches must not register the app in the real Windows startup Run key."
Assert-Contains $appSource 'skipping launch at startup registration' "Isolated first-launch startup skip must be logged."

$csFiles = Get-ChildItem -LiteralPath $appRoot -Recurse -Filter *.cs |
    Where-Object { $_.FullName -notlike "*\obj\*" -and $_.Name -ne "AppPaths.cs" }

foreach ($file in $csFiles) {
    $text = Read-Text $file.FullName
    Assert-NotContains $text 'Environment\.GetFolderPath\(Environment\.SpecialFolder\.LocalApplicationData\)' `
        "Only AppPaths.cs may read SpecialFolder.LocalApplicationData directly. Found in $($file.FullName)"
}

$settings = Read-Text (Join-Path $appRoot "Services\SettingsService.cs")
Assert-Contains $settings 'AppPaths\.AppDataRoot' "SettingsService must store settings under AppPaths.AppDataRoot."
Assert-Contains $settings 'AppPaths\.IsAppDataRootOverridden' "SettingsService default recordings folder must branch for isolated profiles."
Assert-Contains $settings 'AppPaths\.ProfileRecordingsDirectory' "SettingsService isolated default recordings folder must stay under the profile root."
Assert-Contains $settings 'AppPaths\.LegacyAudioDirectory' "SettingsService legacy audio folder must be profile-aware."

$storage = Read-Text (Join-Path $appRoot "Services\StorageService.cs")
Assert-Contains $storage 'AppPaths\.IsAppDataRootOverridden' "StorageService fallbacks must branch for isolated profiles."
Assert-Contains $storage 'AppPaths\.ProfileDownloadsRecordingsDirectory' "StorageService downloads fallback must stay profile-scoped under isolation."
Assert-Contains $storage 'AppPaths\.ProfileTempRecordingsDirectory' "StorageService temp fallback must stay profile-scoped under isolation."

$db = Read-Text (Join-Path $appRoot "Data\HyperWhisperDbContext.cs")
Assert-Contains $db 'AppPaths\.AppDataRoot' "HyperWhisperDbContext must place SQLite data under AppPaths.AppDataRoot."

$expectedPathUsers = @(
    "Services\ConfigService.cs",
    "Services\DeviceIdService.cs",
    "Services\LicenseNetworkService.cs",
    "Services\LicenseUsageTracker.cs",
    "Services\LocalApi\LocalApiAuth.cs",
    "Services\LocalApi\LocalApiDiscoveryFile.cs",
    "Services\LocalLlmModelService.cs",
    "Services\LoggingService.cs",
    "Services\ParakeetModelService.cs",
    "Services\WhisperModelService.cs"
)

foreach ($relativePath in $expectedPathUsers) {
    $text = Read-Text (Join-Path $appRoot $relativePath)
    Assert-Contains $text 'AppPaths\.' "$relativePath must route persistent paths through AppPaths."
}

$apiKeys = Read-Text (Join-Path $appRoot "Services\ApiKeyService.cs")
$license = Read-Text (Join-Path $appRoot "Services\LicenseNetworkService.cs")
Assert-Contains $apiKeys 'AppPaths\.CredentialResource' "ApiKeyService must use the profile-aware Credential Manager resource."
Assert-Contains $license 'AppPaths\.CredentialResource' "LicenseNetworkService must use the profile-aware Credential Manager resource."
Assert-NotContains $apiKeys 'private const string VaultResource = "HyperWhisper"' "ApiKeyService must not hard-code the production credential resource."
Assert-NotContains $license 'private const string VaultResource = "HyperWhisper"' "LicenseNetworkService must not hard-code the production credential resource."

Write-Host "Isolated app profile path verifier passed."
