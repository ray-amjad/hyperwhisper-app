param(
    [string]$TargetVersion = "",
    [string]$ReleaseNotes = "",
    [switch]$SkipAppcastValidator
)

$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Resolve-Path (Join-Path $ScriptDir "..\..\..\..")

function Fail {
    param([string]$Message)
    throw "Windows release readiness failed: $Message"
}

function Read-File {
    param([string]$RelativePath)
    $path = Join-Path $RepoRoot $RelativePath
    if (-not (Test-Path $path)) {
        Fail "Missing required file: $RelativePath"
    }
    return Get-Content $path -Raw
}

function Require-Match {
    param(
        [string]$Content,
        [string]$Pattern,
        [string]$Description
    )
    $match = [regex]::Match($Content, $Pattern)
    if (-not $match.Success) {
        Fail "Could not read $Description"
    }
    return $match.Groups["value"].Value
}

function Require-Semver {
    param(
        [string]$Version,
        [string]$Description
    )
    if ($Version -notmatch "^\d+\.\d+\.\d+$") {
        Fail "$Description must be semantic major.minor.patch, got '$Version'"
    }
}

function Compare-VersionGreaterThan {
    param(
        [string]$Left,
        [string]$Right,
        [string]$Message
    )
    if ([version]$Left -le [version]$Right) {
        Fail $Message
    }
}

function Compare-VersionGreaterThanOrEqual {
    param(
        [string]$Left,
        [string]$Right,
        [string]$Message
    )
    if ([version]$Left -lt [version]$Right) {
        Fail $Message
    }
}

function Assert-Contains {
    param(
        [string]$Content,
        [string]$Needle,
        [string]$Description
    )
    if (-not $Content.Contains($Needle)) {
        Fail $Description
    }
}

function Assert-NotContains {
    param(
        [string]$Content,
        [string]$Needle,
        [string]$Description
    )
    if ($Content.Contains($Needle)) {
        Fail $Description
    }
}

function Assert-Matches {
    param(
        [string]$Content,
        [string]$Pattern,
        [string]$Description
    )
    if (-not [regex]::IsMatch($Content, $Pattern)) {
        Fail $Description
    }
}

Push-Location $RepoRoot
try {
    $csprojPath = "app\windows\HyperWhisper\HyperWhisper.csproj"
    $csproj = Read-File $csprojPath
    $buildRelease = Read-File "app\windows\build-release.ps1"
    $runtimeBaseline = Read-File "app\windows\HyperWhisper\scripts\validate_runtime_baseline.ps1"
    $projectVersion = Require-Match $csproj "<Version>(?<value>\d+\.\d+\.\d+)</Version>" "$csprojPath <Version>"
    $assemblyVersion = Require-Match $csproj "<AssemblyVersion>(?<value>\d+\.\d+\.\d+\.0)</AssemblyVersion>" "$csprojPath <AssemblyVersion>"
    $fileVersion = Require-Match $csproj "<FileVersion>(?<value>\d+\.\d+\.\d+\.0)</FileVersion>" "$csprojPath <FileVersion>"

    Require-Semver $projectVersion "Project version"
    if ($assemblyVersion -ne "$projectVersion.0") {
        Fail "AssemblyVersion $assemblyVersion must match project version $projectVersion.0"
    }
    if ($fileVersion -ne "$projectVersion.0") {
        Fail "FileVersion $fileVersion must match project version $projectVersion.0"
    }

    $setupByArch = @{
        "x64" = "app\windows\setup-x64.iss"
        "arm64" = "app\windows\setup-arm64.iss"
    }

    foreach ($arch in @("x64", "arm64")) {
        $setupPath = $setupByArch[$arch]
        $setup = Read-File $setupPath
        $setupVersion = Require-Match $setup '#define MyAppVersion "(?<value>[^"]+)"' "$setupPath MyAppVersion"

        if ($setupVersion -ne $projectVersion) {
            Fail "$setupPath MyAppVersion $setupVersion must match project version $projectVersion"
        }

        Assert-Contains $setup "OutputBaseFilename={#MyAppName}-{#MyAppVersion}-{#MyArchitecture}-Setup" "$setupPath must keep versioned installer filenames for appcast/update matching"
        Assert-Contains $setup "AppId={{8F4E9A2B-3C5D-4E6F-A1B2-C3D4E5F6A7B8}" "$setupPath must keep the stable AppId for upgrades from prior Windows releases"
    }

    $hasAspNetFrameworkReference = $csproj.Contains('<FrameworkReference Include="Microsoft.AspNetCore.App" />')
    if ($hasAspNetFrameworkReference) {
        $setupX64 = Read-File $setupByArch["x64"]
        $setupArm64 = Read-File $setupByArch["arm64"]
        $depsX64 = Read-File "app\windows\dependencies\CodeDependencies.iss"

        Assert-Contains $buildRelease '"--self-contained", "true"' "Windows release build must publish self-contained so .NET Desktop and ASP.NET Core runtimes are bundled"
        Assert-Matches $runtimeBaseline '"--self-contained"\s*,\s*"true"' "Windows runtime baseline validator must publish self-contained"
        Assert-Contains $setupX64 '#include "dependencies\CodeDependencies.iss"' "x64 installer must include dependency installer library"
        Assert-NotContains $setupX64 "Dependency_AddDotNet100Desktop;" "x64 installer must not register .NET Desktop runtime prerequisites when the app is self-contained"
        Assert-NotContains $setupX64 "Dependency_AddDotNet100AspNetCore;" "x64 installer must not register ASP.NET Core runtime prerequisites when the app is self-contained"
        Assert-Contains $setupX64 "Dependency_AddVC2015To2022;" "x64 installer must keep the VC++ runtime prerequisite for the native Parakeet engine"
        Assert-Contains $setupArm64 '#include "dependencies\CodeDependencies-arm64.iss"' "ARM64 installer must include dependency installer library"
        Assert-NotContains $setupArm64 "Dependency_AddDotNet100DesktopArm64;" "ARM64 installer must not register .NET Desktop runtime prerequisites when the app is self-contained"
        Assert-NotContains $setupArm64 "Dependency_AddDotNet100AspNetCoreArm64;" "ARM64 installer must not register ASP.NET Core runtime prerequisites when the app is self-contained"

        Assert-Contains $depsX64 "procedure Dependency_AddVC2015To2022;" "x64 dependency library must define the VC++ runtime installer"
    }

    $appcastPath = "nextjs\public\appcast-windows.xml"
    $appcast = Read-File $appcastPath
    $appcastMatches = [regex]::Matches($appcast, '<sparkle:shortVersionString>(?<value>\d+\.\d+\.\d+)</sparkle:shortVersionString>')
    if ($appcastMatches.Count -eq 0) {
        Fail "Could not read Windows appcast versions from $appcastPath"
    }

    $topAppcastVersion = $appcastMatches |
        ForEach-Object { [version]$_.Groups["value"].Value } |
        Sort-Object -Descending |
        Select-Object -First 1

    if (-not $SkipAppcastValidator) {
        $node = Get-Command node -ErrorAction SilentlyContinue
        if (-not $node) {
            Fail "Node.js is required to validate $appcastPath; pass -SkipAppcastValidator only when another gate runs nextjs/scripts/validate-windows-appcast.js"
        }

        & node "nextjs\scripts\validate-windows-appcast.js"
        if ($LASTEXITCODE -ne 0) {
            Fail "Windows appcast validator failed"
        }
    }

    if ($TargetVersion.Trim()) {
        $requestedVersion = $TargetVersion.Trim()
        Require-Semver $requestedVersion "Target version"
        Compare-VersionGreaterThanOrEqual $requestedVersion $projectVersion "Target version $requestedVersion must be greater than or equal to project version $projectVersion"
        Compare-VersionGreaterThan $requestedVersion $topAppcastVersion "Target version $requestedVersion must be greater than appcast top version $topAppcastVersion"

        $notes = $ReleaseNotes.Trim()
        if (-not $notes) {
            Fail "Release notes are required when TargetVersion is supplied"
        }
        if ($notes.Contains("]]>")) {
            Fail "Release notes cannot contain the CDATA terminator ]]>"
        }

        $noteMatches = [regex]::Matches($notes, "<li[\s>][\s\S]*?</li>", [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
        if ($noteMatches.Count -eq 0) {
            Fail "Release notes must include at least one complete HTML <li>...</li> item for the Windows appcast"
        }

        foreach ($noteMatch in $noteMatches) {
            $noteText = [regex]::Replace($noteMatch.Value, "<[^>]+>", "").Trim()
            if (-not $noteText) {
                Fail "Release notes cannot contain an empty <li> item"
            }
        }
    }

    Write-Host "Windows release readiness validation passed."
    Write-Host "Project/setup version: $projectVersion"
    Write-Host "Top appcast version: $topAppcastVersion"
    if ($TargetVersion.Trim()) {
        Write-Host "Target release version: $($TargetVersion.Trim())"
    }
}
finally {
    Pop-Location
}
