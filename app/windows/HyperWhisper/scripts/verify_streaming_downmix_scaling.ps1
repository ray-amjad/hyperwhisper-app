param()

$ErrorActionPreference = "Stop"

function Assert-Match {
    param(
        [Parameter(Mandatory = $true)][string]$Content,
        [Parameter(Mandatory = $true)][string]$Pattern,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if (-not [regex]::IsMatch($Content, $Pattern, [System.Text.RegularExpressions.RegexOptions]::Singleline)) {
        throw "Missing expected streaming downmix wiring: $Label"
    }
}

$ProjectRoot = Split-Path -Parent $PSScriptRoot
$RepoRoot = Resolve-Path (Join-Path $ProjectRoot "..\..\..")
$WindowsSource = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "Services\Streaming\StreamingAudioCapture.cs")
$MacSource = Get-Content -Raw -LiteralPath (Join-Path $RepoRoot "app\macos\hyperwhisper\Managers\AudioRecording\Streaming\AudioCapture\StreamingAudioCapture.swift")

Assert-Match `
    -Content $WindowsSource `
    -Pattern "private static byte\[\] MixToMono\(byte\[\] buffer, int bytesRecorded, int channelCount\).*?var scale = 1\.0 / Math\.Sqrt\(channelCount\);" `
    -Label "Windows MixToMono uses RMS-preserving 1/sqrt(channelCount) scaling"

Assert-Match `
    -Content $WindowsSource `
    -Pattern "Math\.Clamp\(\s*Math\.Round\(mixed\),\s*short\.MinValue,\s*short\.MaxValue\)" `
    -Label "Windows MixToMono clamps mixed output instead of wrapping"

Assert-Match `
    -Content $MacSource `
    -Pattern "let scale = 1\.0 / sqrt\(Float\(channelCount\)\)" `
    -Label "macOS streaming multi-channel path uses matching 1/sqrt(channelCount) scaling"

$HarnessRoot = Join-Path $RepoRoot "artifacts\windows-runtime-validation\streaming-downmix-verifier"
New-Item -ItemType Directory -Force -Path $HarnessRoot | Out-Null

$HarnessProject = Join-Path $HarnessRoot "StreamingDownmixVerifier.csproj"
$HarnessProgram = Join-Path $HarnessRoot "Program.cs"
$HarnessLoggingStub = Join-Path $HarnessRoot "LoggingServiceStub.cs"
$StreamingCaptureSource = [System.Security.SecurityElement]::Escape((Join-Path $ProjectRoot "Services\Streaming\StreamingAudioCapture.cs"))

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
    <PackageReference Include="NAudio" Version="2.2.1" />
    <Compile Include="$StreamingCaptureSource" Link="StreamingAudioCapture.cs" />
  </ItemGroup>
</Project>
"@ | Set-Content -LiteralPath $HarnessProject -Encoding UTF8

@'
namespace HyperWhisper.Services;

public static class LoggingService
{
    public static void Info(string message) { }
    public static void Warn(string message) { }
}
'@ | Set-Content -LiteralPath $HarnessLoggingStub -Encoding UTF8

@'
using System.Reflection;
using HyperWhisper.Services.Streaming;

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static byte[] Interleaved(params short[] samples)
{
    var bytes = new byte[samples.Length * sizeof(short)];
    for (var i = 0; i < samples.Length; i++)
    {
        var sampleBytes = BitConverter.GetBytes(samples[i]);
        bytes[i * 2] = sampleBytes[0];
        bytes[i * 2 + 1] = sampleBytes[1];
    }
    return bytes;
}

static short[] Mix(MethodInfo method, int channelCount, params short[] samples)
{
    var buffer = Interleaved(samples);
    var result = (byte[])(method.Invoke(null, new object[] { buffer, buffer.Length, channelCount })
        ?? throw new InvalidOperationException("MixToMono returned null."));
    var output = new short[result.Length / sizeof(short)];
    for (var i = 0; i < output.Length; i++)
        output[i] = BitConverter.ToInt16(result, i * sizeof(short));
    return output;
}

static void AssertApproximately(short actual, double expected, string label)
{
    var delta = Math.Abs(actual - expected);
    Assert(delta <= 1.0, $"{label}: expected approximately {expected:F2}, got {actual}.");
}

var method = typeof(StreamingAudioCapture).GetMethod(
    "MixToMono",
    BindingFlags.Static | BindingFlags.NonPublic)
    ?? throw new MissingMethodException(typeof(StreamingAudioCapture).FullName, "MixToMono");

var fourChannelSingle = Mix(method, 4, 16000, 0, 0, 0);
Assert(fourChannelSingle.Length == 1, "4-channel single-frame mix should emit one mono sample.");
AssertApproximately(fourChannelSingle[0], 16000 / Math.Sqrt(4), "4-channel one-active-channel scaling");
Assert(fourChannelSingle[0] != 16000 / 4, "4-channel mix must not use old average scaling.");

var eightChannelSingle = Mix(method, 8, 16000, 0, 0, 0, 0, 0, 0, 0);
Assert(eightChannelSingle.Length == 1, "8-channel single-frame mix should emit one mono sample.");
AssertApproximately(eightChannelSingle[0], 16000 / Math.Sqrt(8), "8-channel one-active-channel scaling");
Assert(eightChannelSingle[0] != 16000 / 8, "8-channel mix must not use old average scaling.");

var twoFrames = Mix(
    method,
    4,
    12000, 0, 0, 0,
    0, -12000, 0, 0);
Assert(twoFrames.Length == 2, "4-channel two-frame mix should emit two mono samples.");
AssertApproximately(twoFrames[0], 12000 / Math.Sqrt(4), "first frame scaling");
AssertApproximately(twoFrames[1], -12000 / Math.Sqrt(4), "second frame scaling");

var clippedPositive = Mix(method, 4, short.MaxValue, short.MaxValue, short.MaxValue, short.MaxValue);
Assert(clippedPositive[0] == short.MaxValue, $"Positive correlated channels should clamp to Int16 max, got {clippedPositive[0]}.");

var clippedNegative = Mix(method, 4, short.MinValue, short.MinValue, short.MinValue, short.MinValue);
Assert(clippedNegative[0] == short.MinValue, $"Negative correlated channels should clamp to Int16 min, got {clippedNegative[0]}.");

Console.WriteLine("Streaming downmix scaling verification passed.");
'@ | Set-Content -LiteralPath $HarnessProgram -Encoding UTF8

dotnet run --project $HarnessProject --nologo
if ($LASTEXITCODE -ne 0) {
    throw "Streaming downmix harness failed with exit code $LASTEXITCODE"
}

Write-Host "Streaming downmix scaling verifier passed."
