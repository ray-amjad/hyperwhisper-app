param(
    [switch]$SkipPublish,
    [switch]$LaunchDebug,
    [switch]$LaunchRelease,
    [string]$RuntimeIdentifier = "win-x64",
    [string]$LogPath
)

$ErrorActionPreference = "Stop"

function Write-Step {
    param([string]$Message)
    Write-Host ""
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Invoke-LoggedCommand {
    param(
        [string]$Name,
        [string[]]$Command
    )

    Write-Step $Name
    Write-Host ($Command -join " ")
    $executable = $Command[0]
    $arguments = @()
    if ($Command.Length -gt 1) {
        $arguments = $Command[1..($Command.Length - 1)]
    }
    & $executable @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Name failed with exit code $LASTEXITCODE."
    }
}

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = (Resolve-Path (Join-Path $scriptRoot "../../../..")).Path
$projectPath = Join-Path $projectRoot "app/windows/HyperWhisper/HyperWhisper.csproj"
$debugExe = Join-Path $projectRoot "app/windows/HyperWhisper/bin/Debug/net10.0-windows10.0.19041.0/HyperWhisper.exe"
$publishDir = Join-Path $projectRoot "app/windows/HyperWhisper/bin/Release/net10.0-windows10.0.19041.0/$RuntimeIdentifier/publish"
$releaseExe = Join-Path $publishDir "HyperWhisper.exe"

if ([string]::IsNullOrWhiteSpace($LogPath)) {
    $logDir = Join-Path $projectRoot "artifacts/windows-runtime-validation"
    $LogPath = Join-Path $logDir ("baseline-{0}.log" -f (Get-Date -Format "yyyyMMdd-HHmmss"))
}

$logParent = Split-Path -Parent $LogPath
if (-not [string]::IsNullOrWhiteSpace($logParent)) {
    New-Item -ItemType Directory -Force -Path $logParent | Out-Null
}

Set-Location $projectRoot

Start-Transcript -Path $LogPath -Force | Out-Null
try {
    Write-Step "Windows runtime baseline validation"
    Write-Host "Repo root: $projectRoot"
    Write-Host "Project:   $projectPath"
    Write-Host "Runtime:   $RuntimeIdentifier"
    Write-Host "Log path:  $LogPath"

    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw "dotnet was not found on PATH. Install the .NET SDK before running this script."
    }

    Invoke-LoggedCommand "dotnet --info" @("dotnet", "--info")
    Invoke-LoggedCommand "restore" @("dotnet", "restore", $projectPath)
    Invoke-LoggedCommand "debug build" @("dotnet", "build", $projectPath, "--no-restore")

    if (-not (Test-Path $debugExe)) {
        throw "Debug executable was not produced: $debugExe"
    }

    if (-not $SkipPublish) {
        Invoke-LoggedCommand "release publish" @(
            "dotnet",
            "publish",
            $projectPath,
            "-c",
            "Release",
            "-r",
            $RuntimeIdentifier,
            "--self-contained",
            "true"
        )

        if (-not (Test-Path $releaseExe)) {
            throw "Release executable was not produced: $releaseExe"
        }
    }

    if ($LaunchDebug) {
        Write-Step "launch debug app"
        Start-Process -FilePath $debugExe
        Write-Host "Launched: $debugExe"
    }

    if ($LaunchRelease) {
        if ($SkipPublish) {
            throw "-LaunchRelease requires a Release publish. Remove -SkipPublish."
        }

        Write-Step "launch release app"
        Start-Process -FilePath $releaseExe
        Write-Host "Launched: $releaseExe"
    }

    Write-Step "baseline complete"
    Write-Host "Debug executable:   $debugExe"
    if (-not $SkipPublish) {
        Write-Host "Release executable: $releaseExe"
    }
    Write-Host "Transcript log:     $LogPath"
    Write-Host ""
    Write-Host "Next manual gates are listed in tasks/windows/to-do/runtime-validation-checklist.md"
}
finally {
    Stop-Transcript | Out-Null
}
