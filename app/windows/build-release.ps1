<#
.SYNOPSIS
    Build HyperWhisper Windows release packages.

.DESCRIPTION
    This script builds the HyperWhisper Windows app for x64 and/or ARM64,
    creates Inno Setup installers, and optionally signs them for NetSparkle updates.

.PARAMETER Architecture
    Target architecture: 'x64', 'arm64', or 'both'. Default: 'both'

.PARAMETER Version
    Version number for the release (e.g., '1.0.0'). Required.

.PARAMETER Sign
    If specified, signs the installer(s) with NetSparkle Ed25519.

.PARAMETER SkipBuild
    If specified, skips the dotnet publish step (use existing build).

.EXAMPLE
    # Build ARM64 only
    .\build-release.ps1 -Architecture arm64 -Version 1.0.0

.EXAMPLE
    # Build both architectures and sign
    .\build-release.ps1 -Architecture both -Version 1.0.0 -Sign

.EXAMPLE
    # Just create installer from existing build
    .\build-release.ps1 -Architecture arm64 -Version 1.0.0 -SkipBuild
#>

param(
    [ValidateSet('x64', 'arm64', 'both')]
    [string]$Architecture = 'both',

    [Parameter(Mandatory=$true)]
    [string]$Version,

    [switch]$Sign,

    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'

# Configuration
$ProjectDir = "$PSScriptRoot\HyperWhisper"
$OutputDir = "$PSScriptRoot\..\windows-installers"
$ProjectFile = "$ProjectDir\HyperWhisper.csproj"

# Ensure output directory exists
if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir | Out-Null
}

function Write-Header {
    param([string]$Message)
    Write-Host ""
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host $Message -ForegroundColor Cyan
    Write-Host "========================================" -ForegroundColor Cyan
}

function Load-EnvFile {
    param([string]$Path)

    if (Test-Path $Path) {
        Write-Host "Loading environment from $Path" -ForegroundColor Green
        Get-Content $Path | ForEach-Object {
            if ($_ -match '^\s*([^#][^=]+)=(.*)$') {
                $name = $matches[1].Trim()
                $value = $matches[2].Trim()
                # Skip placeholder values to prevent Sentry upload errors
                if ($value -match 'your-|placeholder|example|REPLACE') {
                    Write-Host "  Skipping $name (placeholder value)" -ForegroundColor Gray
                } else {
                    [Environment]::SetEnvironmentVariable($name, $value, 'Process')
                }
            }
        }
        return $true
    }
    return $false
}

function Build-App {
    param(
        [string]$Arch,
        [string]$Ver
    )

    Write-Header "Building HyperWhisper for $Arch"

    $rid = "win-$Arch"
    $publishDir = "$ProjectDir\bin\Release\net10.0-windows10.0.19041.0\$rid\publish"

    # Clean previous build
    if (Test-Path $publishDir) {
        Write-Host "Cleaning previous build..." -ForegroundColor Yellow
        Remove-Item -Recurse -Force $publishDir
    }

    # Build with dotnet publish
    # IMPORTANT: publish self-contained so the .NET 10 Desktop + ASP.NET Core
    # runtimes are bundled in the installer, so users never need a separate .NET
    # install (avoids the post-update "install .NET" prompt). Keep in sync with
    # setup-x64.iss / setup-arm64.iss. See the windows-release skill.
    # Disable Sentry symbol upload if credentials aren't configured
    $sentryEnabled = $env:SENTRY_AUTH_TOKEN -and $env:SENTRY_ORG -and $env:SENTRY_PROJECT
    $publishArgs = @("-c", "Release", "-r", $rid, "--self-contained", "true")

    if (-not $sentryEnabled) {
        $publishArgs += @("-p:SentryUploadSymbols=false", "-p:SentryUploadSources=false")
    }

    Write-Host "Running: dotnet publish $($publishArgs -join ' ')" -ForegroundColor Gray

    Push-Location $ProjectDir
    try {
        & dotnet publish @publishArgs

        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish failed with exit code $LASTEXITCODE"
        }
    }
    finally {
        Pop-Location
    }

    Write-Host "Build complete: $publishDir" -ForegroundColor Green

    # List output files
    $files = Get-ChildItem $publishDir -File | Select-Object -First 10
    Write-Host "Published files (first 10):" -ForegroundColor Gray
    foreach ($file in $files) {
        Write-Host "  $($file.Name)" -ForegroundColor Gray
    }

    return $publishDir
}

function Update-CsprojVersion {
    param(
        [string]$Ver
    )

    $csprojPath = "$PSScriptRoot\HyperWhisper\HyperWhisper.csproj"
    Write-Host "Updating version in HyperWhisper.csproj to $Ver" -ForegroundColor Yellow

    $content = Get-Content $csprojPath -Raw
    $content = $content -replace '<Version>[^<]*</Version>', "<Version>$Ver</Version>"
    $content = $content -replace '<AssemblyVersion>[^<]*</AssemblyVersion>', "<AssemblyVersion>$Ver.0</AssemblyVersion>"
    $content = $content -replace '<FileVersion>[^<]*</FileVersion>', "<FileVersion>$Ver.0</FileVersion>"
    Set-Content $csprojPath -Value $content -NoNewline
}

function Update-InnoSetupVersion {
    param(
        [string]$IssFile,
        [string]$Ver
    )

    Write-Host "Updating version in $IssFile to $Ver" -ForegroundColor Yellow

    $content = Get-Content $IssFile -Raw
    $content = $content -replace '#define MyAppVersion "[^"]*"', "#define MyAppVersion `"$Ver`""
    Set-Content $IssFile -Value $content -NoNewline
}

function Build-Installer {
    param(
        [string]$Arch,
        [string]$Ver
    )

    Write-Header "Creating Installer for $Arch"

    $issFile = "$PSScriptRoot\setup-$Arch.iss"

    if (-not (Test-Path $issFile)) {
        throw "Inno Setup script not found: $issFile"
    }

    # Update version in .iss file
    Update-InnoSetupVersion -IssFile $issFile -Ver $Ver

    # Find Inno Setup compiler
    $iscc = $null
    $possiblePaths = @(
        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
        "C:\Program Files\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        (Get-Command iscc -ErrorAction SilentlyContinue).Source
    )

    foreach ($path in $possiblePaths) {
        if ($path -and (Test-Path $path)) {
            $iscc = $path
            break
        }
    }

    if (-not $iscc) {
        throw "Inno Setup compiler (ISCC.exe) not found. Please install Inno Setup 6 from https://jrsoftware.org/isinfo.php"
    }

    Write-Host "Using Inno Setup: $iscc" -ForegroundColor Gray
    Write-Host "Running: $iscc $issFile" -ForegroundColor Gray

    & $iscc $issFile

    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup compilation failed with exit code $LASTEXITCODE"
    }

    $installerName = "HyperWhisper-$Ver-$Arch-Setup.exe"
    $installerPath = "$OutputDir\$installerName"

    if (-not (Test-Path $installerPath)) {
        throw "Expected installer not found: $installerPath"
    }

    $fileSize = (Get-Item $installerPath).Length
    Write-Host "Installer created: $installerName" -ForegroundColor Green
    Write-Host "  Size: $($fileSize.ToString('N0')) bytes" -ForegroundColor Gray

    return @{
        Path = $installerPath
        Name = $installerName
        Size = $fileSize
    }
}

function Sign-Installer {
    param(
        [string]$InstallerPath
    )

    Write-Header "Signing Installer"

    # Check if netsparkle-generate-appcast is available
    $tool = Get-Command netsparkle-generate-appcast -ErrorAction SilentlyContinue

    if (-not $tool) {
        Write-Host "NetSparkle CLI not found. Install with:" -ForegroundColor Yellow
        Write-Host "  dotnet tool install --global NetSparkleUpdater.Tools.AppCastGenerator" -ForegroundColor Gray
        Write-Host "Skipping signing." -ForegroundColor Yellow
        return $null
    }

    Write-Host "Signing: $InstallerPath" -ForegroundColor Gray

    $output = & netsparkle-generate-appcast --sign-file $InstallerPath 2>&1

    if ($LASTEXITCODE -ne 0) {
        Write-Host "Signing failed. Make sure Ed25519 keys are generated:" -ForegroundColor Yellow
        Write-Host "  netsparkle-generate-appcast --generate-keys" -ForegroundColor Gray
        return $null
    }

    # Extract signature from output
    $signature = ($output | Select-String -Pattern "Signature:\s*(.+)").Matches.Groups[1].Value

    if ($signature) {
        Write-Host "Signature: $signature" -ForegroundColor Green
        return $signature
    }

    Write-Host "Could not extract signature from output" -ForegroundColor Yellow
    return $null
}

function Show-Summary {
    param(
        [hashtable[]]$Installers
    )

    Write-Header "Build Summary"

    foreach ($installer in $Installers) {
        Write-Host ""
        Write-Host "Architecture: $($installer.Arch)" -ForegroundColor White
        Write-Host "  File: $($installer.Name)" -ForegroundColor Gray
        Write-Host "  Size: $($installer.Size.ToString('N0')) bytes" -ForegroundColor Gray
        Write-Host "  Path: $($installer.Path)" -ForegroundColor Gray

        if ($installer.Signature) {
            Write-Host "  Signature: $($installer.Signature)" -ForegroundColor Green
        }
    }

    Write-Host ""
    Write-Host "Appcast XML snippet:" -ForegroundColor Yellow
    Write-Host "Download URLs use the same root R2 key convention as .github/workflows/windows-release.yml and nextjs/public/appcast-windows.xml." -ForegroundColor Gray
    foreach ($installer in $Installers) {
        Write-Host @"
<item>
  <title>$Version</title>
  <sparkle:version>$Version</sparkle:version>
  <sparkle:os>windows-$($installer.Arch)</sparkle:os>
  <enclosure
    url="https://builds.hyperwhisper.com/$($installer.Name)"
    length="$($installer.Size)"
    type="application/octet-stream"
    sparkle:edSignature="$(if ($installer.Signature) { $installer.Signature } else { 'REPLACE_WITH_SIGNATURE' })"
  />
</item>
"@ -ForegroundColor Gray
    }
}

# =============================================================================
# MAIN
# =============================================================================

Write-Host "HyperWhisper Windows Release Builder" -ForegroundColor Magenta
Write-Host "Version: $Version" -ForegroundColor Gray
Write-Host "Architecture: $Architecture" -ForegroundColor Gray
Write-Host "Sign: $Sign" -ForegroundColor Gray
Write-Host ""

# Load Sentry credentials for symbol upload
$envPath = Join-Path $PSScriptRoot "HyperWhisper\.env"
if (-not (Load-EnvFile -Path $envPath)) {
    Write-Host "Note: No .env file found. Sentry symbol upload will be skipped." -ForegroundColor Yellow
    Write-Host "      Copy HyperWhisper\.env.example to HyperWhisper\.env to enable." -ForegroundColor Yellow
}

# Show Sentry status
$sentryConfigured = $env:SENTRY_AUTH_TOKEN -and $env:SENTRY_ORG -and $env:SENTRY_PROJECT
if ($sentryConfigured) {
    Write-Host "Sentry: Enabled (org: $env:SENTRY_ORG, project: $env:SENTRY_PROJECT)" -ForegroundColor Green
} else {
    Write-Host "Sentry: Disabled (missing credentials)" -ForegroundColor Gray
}
Write-Host ""

# Update version in .csproj (affects app's About dialog and assembly info)
Update-CsprojVersion -Ver $Version

$architectures = @()
if ($Architecture -eq 'both') {
    $architectures = @('x64', 'arm64')
} else {
    $architectures = @($Architecture)
}

$results = @()

foreach ($arch in $architectures) {
    try {
        # Step 1: Build the app
        if (-not $SkipBuild) {
            Build-App -Arch $arch -Ver $Version
        } else {
            Write-Host "Skipping build for $arch (using existing)" -ForegroundColor Yellow
        }

        # Step 2: Create installer
        $installer = Build-Installer -Arch $arch -Ver $Version

        # Step 3: Sign (optional)
        $signature = $null
        if ($Sign) {
            $signature = Sign-Installer -InstallerPath $installer.Path
        }

        $results += @{
            Arch = $arch
            Name = $installer.Name
            Path = $installer.Path
            Size = $installer.Size
            Signature = $signature
        }
    }
    catch {
        Write-Host "ERROR building $arch : $_" -ForegroundColor Red
        exit 1
    }
}

# Show summary
Show-Summary -Installers $results

Write-Host ""
Write-Host "Done!" -ForegroundColor Green
