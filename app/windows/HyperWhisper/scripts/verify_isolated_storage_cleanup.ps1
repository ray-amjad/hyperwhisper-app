param()

$ErrorActionPreference = "Stop"

function Assert-True {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

$ProjectRoot = Split-Path -Parent $PSScriptRoot
$RepoRoot = Resolve-Path (Join-Path $ProjectRoot "..\..\..")
$RunId = [guid]::NewGuid().ToString("N")
$HarnessRoot = Join-Path $env:TEMP "hyperwhisper-storage-harness-$RunId"
$ProfileRoot = Join-Path $env:TEMP "hyperwhisper-storage-profile-$RunId"
$OutsideRoot = Join-Path $env:TEMP "hyperwhisper-storage-outside-$RunId"

New-Item -ItemType Directory -Path $HarnessRoot, $ProfileRoot, $OutsideRoot | Out-Null

try {
    $HarnessProject = Join-Path $HarnessRoot "StorageCleanupHarness.csproj"
    $HarnessProgram = Join-Path $HarnessRoot "Program.cs"
    $AppProject = Join-Path $ProjectRoot "HyperWhisper.csproj"

    Set-Content -LiteralPath $HarnessProject -Encoding UTF8 -Value @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <UseWPF>true</UseWPF>
    <UseWindowsForms>true</UseWindowsForms>
    <EnableWindowsTargeting>true</EnableWindowsTargeting>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$AppProject" />
  </ItemGroup>
</Project>
"@

    Set-Content -LiteralPath $HarnessProgram -Encoding UTF8 -Value @"
using HyperWhisper.Data;
using HyperWhisper.Data.Entities;
using HyperWhisper.Services;
using Microsoft.EntityFrameworkCore;
using System.IO;

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new Exception(message);
    }
}

static void WriteAudio(string path)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, "disposable audio");
}

var profileRoot = Environment.GetEnvironmentVariable(AppPaths.AppDataRootOverrideEnvironmentVariable);
if (string.IsNullOrWhiteSpace(profileRoot))
{
    throw new Exception("Missing isolated profile environment variable.");
}

profileRoot = Path.GetFullPath(profileRoot);
Assert(string.Equals(AppPaths.AppDataRoot, profileRoot, StringComparison.OrdinalIgnoreCase),
    "AppPaths did not honor HYPERWHISPER_WINDOWS_APPDATA_ROOT.");

var outsideRoot = Environment.GetEnvironmentVariable("HW_STORAGE_OUTSIDE_ROOT");
if (string.IsNullOrWhiteSpace(outsideRoot))
{
    throw new Exception("Missing outside-root environment variable.");
}

outsideRoot = Path.GetFullPath(outsideRoot);
var activeRecordings = Path.Combine(profileRoot, "recordings-active");
var oldTrustedAudio = Path.Combine(activeRecordings, "old-trusted.wav");
var oldTrustedTrimmed = Path.Combine(activeRecordings, "old-trusted.trimmed.wav");
var oldLegacyAudio = Path.Combine(AppPaths.LegacyAudioDirectory, "old-legacy.wav");
var oldTempAudio = Path.Combine(AppPaths.ProfileTempRecordingsDirectory, "old-temp.wav");
var oldTopLevelFallback = Path.Combine(Path.GetTempPath(), $"hyperwhisper_{Guid.NewGuid():N}.wav");
var newTrustedAudio = Path.Combine(activeRecordings, "new-trusted.wav");
var untrustedOutsideAudio = Path.Combine(outsideRoot, "old-outside.wav");

foreach (var path in new[]
{
    oldTrustedAudio,
    oldTrustedTrimmed,
    oldLegacyAudio,
    oldTempAudio,
    oldTopLevelFallback,
    newTrustedAudio,
    untrustedOutsideAudio
})
{
    WriteAudio(path);
}

var settings = SettingsService.Instance;
settings.EnableErrorLogging = false;
settings.AutoDeleteEnabled = true;
settings.AutoDeleteDaysOld = 30;

Assert(StorageService.Instance.TryChangeRecordingsFolder(activeRecordings, out var changeError),
    $"Failed to set isolated recordings folder: {changeError}");

using (var context = new HyperWhisperDbContext())
{
    context.Database.EnsureDeleted();
    context.Database.EnsureCreated();

    var oldDate = DateTime.UtcNow.AddDays(-45);
    var newDate = DateTime.UtcNow.AddDays(-2);

    context.Transcripts.AddRange(
        new Transcript
        {
            Id = Guid.NewGuid(),
            Text = "old trusted",
            Date = oldDate,
            Status = TranscriptStatus.Completed,
            AudioFilePath = oldTrustedAudio,
            TrimmedAudioFilePath = oldTrustedTrimmed
        },
        new Transcript
        {
            Id = Guid.NewGuid(),
            Text = "old legacy",
            Date = oldDate,
            Status = TranscriptStatus.Completed,
            AudioFilePath = oldLegacyAudio
        },
        new Transcript
        {
            Id = Guid.NewGuid(),
            Text = "old temp isolated",
            Date = oldDate,
            Status = TranscriptStatus.Completed,
            AudioFilePath = oldTempAudio
        },
        new Transcript
        {
            Id = Guid.NewGuid(),
            Text = "old top-level fallback",
            Date = oldDate,
            Status = TranscriptStatus.Completed,
            AudioFilePath = oldTopLevelFallback
        },
        new Transcript
        {
            Id = Guid.NewGuid(),
            Text = "old untrusted outside",
            Date = oldDate,
            Status = TranscriptStatus.Completed,
            AudioFilePath = untrustedOutsideAudio
        },
        new Transcript
        {
            Id = Guid.NewGuid(),
            Text = "new trusted",
            Date = newDate,
            Status = TranscriptStatus.Completed,
            AudioFilePath = newTrustedAudio
        });

    context.SaveChanges();
}

Assert(HistoryService.IsDeletableAudioPath(oldTrustedAudio), "Active recordings path was not trusted.");
Assert(HistoryService.IsDeletableAudioPath(oldLegacyAudio), "Legacy audio path was not trusted.");
Assert(HistoryService.IsDeletableAudioPath(oldTempAudio), "Isolated temp recordings path was not trusted.");
Assert(HistoryService.IsDeletableAudioPath(oldTopLevelFallback), "Top-level recorder fallback WAV was not trusted.");
Assert(!HistoryService.IsDeletableAudioPath(untrustedOutsideAudio), "Untrusted outside path was trusted.");

var deleted = AutoDeleteService.Instance.PerformManualCleanup();
Assert(deleted == 5, $"Expected 5 old transcripts deleted, got {deleted}.");
Assert(AutoDeleteService.Instance.LastCleanupFilesDeleted == 5,
    $"Expected 5 trusted files counted, got {AutoDeleteService.Instance.LastCleanupFilesDeleted}.");

using (var context = new HyperWhisperDbContext())
{
    var remainingTexts = context.Transcripts.Select(t => t.Text).ToList();
    Assert(remainingTexts.Count == 1 && remainingTexts[0] == "new trusted",
        $"Unexpected remaining transcripts: {string.Join(", ", remainingTexts)}");
}

Assert(!File.Exists(oldTrustedAudio), "Old trusted audio was not deleted.");
Assert(!File.Exists(oldTrustedTrimmed), "Old trusted trimmed audio was not deleted.");
Assert(!File.Exists(oldLegacyAudio), "Old legacy audio was not deleted.");
Assert(!File.Exists(oldTempAudio), "Old isolated temp audio was not deleted.");
Assert(!File.Exists(oldTopLevelFallback), "Old top-level fallback audio was not deleted.");
Assert(File.Exists(newTrustedAudio), "New trusted audio should remain.");
Assert(File.Exists(untrustedOutsideAudio), "Untrusted outside audio should remain even when transcript row is deleted.");

var logDirectory = AppPaths.LogsDirectory;
Assert(logDirectory.StartsWith(profileRoot, StringComparison.OrdinalIgnoreCase),
    "Logs were not scoped under the isolated profile.");

Console.WriteLine("Isolated storage cleanup harness passed.");
"@

    $env:HYPERWHISPER_WINDOWS_APPDATA_ROOT = $ProfileRoot
    $env:HW_STORAGE_OUTSIDE_ROOT = $OutsideRoot
    dotnet run --project $HarnessProject --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "Isolated storage cleanup harness failed with exit code $LASTEXITCODE."
    }

    $LogRoot = Join-Path $ProfileRoot "Logs"
    Assert-True -Condition (Test-Path -LiteralPath $LogRoot) -Message "Expected isolated log directory was not created."
    Write-Host "Isolated storage cleanup verifier passed."
}
finally {
    Remove-Item Env:\HYPERWHISPER_WINDOWS_APPDATA_ROOT -ErrorAction SilentlyContinue
    Remove-Item Env:\HW_STORAGE_OUTSIDE_ROOT -ErrorAction SilentlyContinue
    foreach ($PathToRemove in @($HarnessRoot, $ProfileRoot, $OutsideRoot)) {
        if ([string]::IsNullOrWhiteSpace($PathToRemove)) {
            continue
        }

        $FullPath = [System.IO.Path]::GetFullPath($PathToRemove)
        $TempRoot = [System.IO.Path]::GetFullPath($env:TEMP)
        Assert-True -Condition ($FullPath.StartsWith($TempRoot, [System.StringComparison]::OrdinalIgnoreCase)) `
            -Message "Refusing to remove non-temp verifier path: $FullPath"
        Remove-Item -LiteralPath $FullPath -Recurse -Force -ErrorAction SilentlyContinue
    }
}
