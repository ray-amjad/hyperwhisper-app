param()

$ErrorActionPreference = "Stop"

function Assert-Match {
    param(
        [Parameter(Mandatory = $true)][string]$Content,
        [Parameter(Mandatory = $true)][string]$Pattern,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if (-not [regex]::IsMatch($Content, $Pattern, [System.Text.RegularExpressions.RegexOptions]::Singleline)) {
        throw "Missing expected no-speech terminal-status wiring: $Label"
    }
}

$ProjectRoot = Split-Path -Parent $PSScriptRoot
$RepoRoot = Resolve-Path (Join-Path $ProjectRoot "..\..\..")
$HarnessRoot = Join-Path $RepoRoot "artifacts\windows-runtime-validation\no-speech-terminal-status-verifier"
New-Item -ItemType Directory -Force -Path $HarnessRoot | Out-Null

$OrchestratorSource = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "Services\Transcription\TranscriptionOrchestrator.cs")
$MainViewModelSource = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "ViewModels\MainViewModel.cs")
$HistorySource = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "Services\HistoryService.cs")
$AppSource = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "App.xaml.cs")

Assert-Match `
    -Content $OrchestratorSource `
    -Pattern "string\.IsNullOrWhiteSpace\(rawText\).*?TranscriptionErrorCode\.NoSpeechDetected" `
    -Label "orchestrator maps empty provider text to NoSpeechDetected"

Assert-Match `
    -Content $MainViewModelSource `
    -Pattern "if \(txEx\.Code == TranscriptionErrorCode\.NoSpeechDetected\).*?MarkTranscriptAsNoSpeechFailure\(transcript, txEx\.ProviderName\).*?CaptureNoSpeechDiagnostic\(.*?return;" `
    -Label "recording flow catches NoSpeechDetected, fails the transcript, captures diagnostics, then returns"

Assert-Match `
    -Content $MainViewModelSource `
    -Pattern "TranscribeFileAsync failed:.*?TranscriptionErrorCode\.NoSpeechDetected.*?MarkTranscriptAsNoSpeechFailure\(transcript, txEx\.ProviderName\).*?CaptureNoSpeechDiagnostic\(" `
    -Label "file transcription flow catches NoSpeechDetected and fails the transcript"

$FinallyGuardCount = [regex]::Matches(
    $MainViewModelSource,
    "finally\s*\{.*?EnsureTranscriptTerminalStatus\(transcript\);",
    [System.Text.RegularExpressions.RegexOptions]::Singleline).Count
if ($FinallyGuardCount -lt 2) {
    throw "Expected both recording and file transcription finally blocks to call EnsureTranscriptTerminalStatus; found $FinallyGuardCount."
}

Assert-Match `
    -Content $MainViewModelSource `
    -Pattern "private static void EnsureTranscriptTerminalStatus\(Transcript\? transcript\).*?transcript\.Status = TranscriptStatus\.Failed;.*?HistoryService\.Instance\.UpdateTranscript\(transcript\);" `
    -Label "EnsureTranscriptTerminalStatus flips still-Processing transcripts to Failed"

Assert-Match `
    -Content $HistorySource `
    -Pattern "public int RecoverOrphanedProcessingTranscripts\(\).*?t\.Status = TranscriptStatus\.Failed;.*?context\.SaveChanges\(\);" `
    -Label "startup recovery flips orphaned Processing transcripts to Failed"

Assert-Match `
    -Content $AppSource `
    -Pattern "RecoverOrphanedProcessingTranscripts\(\)" `
    -Label "app startup invokes orphaned Processing transcript recovery"

$HarnessProject = Join-Path $HarnessRoot "NoSpeechTerminalStatusVerifier.csproj"
$HarnessProgram = Join-Path $HarnessRoot "Program.cs"
$ProjectReference = [System.Security.SecurityElement]::Escape($ProjectRoot)

@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
    <EnableWindowsTargeting>true</EnableWindowsTargeting>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$ProjectReference\HyperWhisper.csproj" />
  </ItemGroup>
</Project>
"@ | Set-Content -LiteralPath $HarnessProject -Encoding UTF8

@'
using HyperWhisper.Data.Entities;
using HyperWhisper.Models;
using HyperWhisper.Services;
using HyperWhisper.Services.Transcription;

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

var mode = new Mode
{
    Name = "Verifier Local Mode",
    ProviderType = "local",
    Language = "auto",
    PostProcessingMode = 0
};

using var orchestrator = new TranscriptionOrchestrator();
try
{
    _ = await orchestrator.TranscribeAsync(
        audioPath: "silent-verifier.wav",
        mode: mode,
        vocabulary: null,
        localTranscriptionProvider: new SilentLocalProvider(),
        applicationContext: null,
        cancellationToken: CancellationToken.None,
        callSite: TranscriptionCallSite.Gui);

    throw new InvalidOperationException("Expected NoSpeechDetected when the provider returns whitespace.");
}
catch (TranscriptionException ex)
{
    Assert(ex.Code == TranscriptionErrorCode.NoSpeechDetected, $"Expected NoSpeechDetected, got {ex.Code}.");
    Assert(ex.ProviderName == "Fake Silent Provider", $"Expected provider name to be preserved, got '{ex.ProviderName}'.");
    Assert(ex.Message.Contains("No speech", StringComparison.OrdinalIgnoreCase), "Expected user-facing no-speech message.");
}

Console.WriteLine("No-speech terminal-status verifier passed.");

sealed class SilentLocalProvider : ITranscriptionProvider
{
    public bool IsAvailable => true;
    public string Name => "Fake Silent Provider";

    public Task<string> TranscribeAsync(
        string audioPath,
        string? language = null,
        IReadOnlyList<string>? vocabulary = null,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult("   ");
    }
}
'@ | Set-Content -LiteralPath $HarnessProgram -Encoding UTF8

dotnet run --project $HarnessProject --nologo
if ($LASTEXITCODE -ne 0) {
    throw "No-speech terminal status harness failed with exit code $LASTEXITCODE."
}
