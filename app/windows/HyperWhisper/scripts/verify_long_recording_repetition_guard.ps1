param()

$ErrorActionPreference = "Stop"

function Assert-True {
    param(
        [bool] $Condition,
        [string] $Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Assert-Match {
    param(
        [string] $Content,
        [string] $Pattern,
        [string] $Message
    )

    Assert-True ([regex]::IsMatch($Content, $Pattern, [System.Text.RegularExpressions.RegexOptions]::Singleline)) $Message
}

function Remove-HarnessRoot {
    param([string] $PathToRemove)

    if ([string]::IsNullOrWhiteSpace($PathToRemove) -or -not (Test-Path -LiteralPath $PathToRemove)) {
        return
    }

    $fullPath = [System.IO.Path]::GetFullPath($PathToRemove)
    $tempRoot = [System.IO.Path]::GetFullPath($env:TEMP)
    Assert-True ($fullPath.StartsWith($tempRoot, [System.StringComparison]::OrdinalIgnoreCase)) `
        "Refusing to remove non-temp harness path: $fullPath"

    Remove-Item -LiteralPath $fullPath -Recurse -Force -ErrorAction SilentlyContinue
}

$ProjectRoot = Split-Path -Parent $PSScriptRoot
$RepoRoot = Resolve-Path (Join-Path $ProjectRoot "..\..\..")
$TranscriptionSource = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "Services\TranscriptionService.cs")
$MacWhisperSource = Get-Content -Raw -LiteralPath (Join-Path $RepoRoot "app\macos\hyperwhisper\Whisper\LibWhisper.swift")

Assert-Match `
    -Content $TranscriptionSource `
    -Pattern "var durationSeconds = GetAudioDurationSeconds\(audioPath\).*?bool isLongRecording = durationSeconds > 15\.0" `
    -Message "Windows must select the long-recording regime only for audio longer than 15 seconds."

Assert-Match `
    -Content $TranscriptionSource `
    -Pattern "builder\.WithNoContext\(\).*?if \(isLongRecording\).*?builder\.WithTemperatureInc\(0\.2f\).*?builder\.WithLogProbThreshold\(-1\.0f\).*?else.*?builder\.WithTemperatureInc\(0\.0f\).*?builder\.WithSingleSegment\(\)" `
    -Message "Windows long recordings must disable prior context, enable temperature/logprob fallback, and reserve single-segment decoding for short clips."

Assert-Match `
    -Content $TranscriptionSource `
    -Pattern "var rawText = text\.ToString\(\)\.Trim\(\).*?var finalText = CollapseRepetitionLoops\(rawText\).*?return finalText" `
    -Message "Windows transcription output must pass through the repetition-loop collapse guard."

Assert-Match `
    -Content $TranscriptionSource `
    -Pattern "internal static string CollapseRepetitionLoops\(string text\).*?const int minPhraseTokens = 3.*?const int maxPhraseTokens = 12.*?const int minRepeats = 3.*?HasSmallerRepeatedPeriod\(normalized, idx, phraseLength, maxPeriod: 2\)" `
    -Message "Repetition guard must collapse repeated phrases while preserving one- and two-token emphasis patterns."

Assert-Match `
    -Content $MacWhisperSource `
    -Pattern "params\.no_context = true.*?params\.single_segment = false.*?params\.temperature = temperature" `
    -Message "macOS comparison must keep independent calls, allow multiple segments, and expose temperature control."

$RunId = [guid]::NewGuid().ToString("N")
$HarnessRoot = Join-Path $env:TEMP "hyperwhisper-long-recording-verifier-$RunId"
New-Item -ItemType Directory -Force -Path $HarnessRoot | Out-Null

try {
    $HarnessProject = Join-Path $HarnessRoot "LongRecordingVerifier.csproj"
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
    <UseWPF>true</UseWPF>
    <UseWindowsForms>true</UseWindowsForms>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$ProjectReference\HyperWhisper.csproj" />
  </ItemGroup>
</Project>
"@ | Set-Content -LiteralPath $HarnessProject -Encoding UTF8

    @'
using System.Reflection;
using HyperWhisper.Services;

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static string Collapse(string input)
{
    var method = typeof(TranscriptionService).GetMethod(
        "CollapseRepetitionLoops",
        BindingFlags.Static | BindingFlags.NonPublic);

    if (method == null)
    {
        throw new MissingMethodException(typeof(TranscriptionService).FullName, "CollapseRepetitionLoops");
    }

    return (string)method.Invoke(null, new object[] { input })!;
}

Assert(Collapse("alpha beta gamma alpha beta gamma alpha beta gamma tail") == "alpha beta gamma tail",
    "Three repeated three-token phrases should collapse to one occurrence.");

Assert(Collapse("Alpha beta gamma. alpha beta gamma alpha beta gamma") == "Alpha beta gamma.",
    "Case-insensitive phrase repeats with edge punctuation should collapse while preserving first occurrence casing.");

var ordinary = "yes yes yes yes yes yes yes yes yes";
Assert(Collapse(ordinary) == ordinary,
    "Repeated single-word emphasis should not collapse.");

var shortText = "one two three one two";
Assert(Collapse(shortText) == shortText,
    "Non-loop text shorter than three complete repeated phrases should remain unchanged.");

Console.WriteLine("Long recording repetition guard reflection verification passed.");
'@ | Set-Content -LiteralPath $HarnessProgram -Encoding UTF8

    dotnet run --project $HarnessProject --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "Long recording repetition guard harness failed with exit code $LASTEXITCODE."
    }
}
finally {
    Remove-HarnessRoot $HarnessRoot
}

Write-Host "Long recording repetition guard verifier passed."
