param()

$ErrorActionPreference = "Stop"

$ProjectRoot = Split-Path -Parent $PSScriptRoot
$HarnessRoot = Join-Path $env:TEMP "hyperwhisper-no-speech-diagnostic-policy-verifier"
Remove-Item -LiteralPath $HarnessRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $HarnessRoot | Out-Null

$HarnessProject = Join-Path $HarnessRoot "NoSpeechDiagnosticPolicyVerifier.csproj"
$HarnessProgram = Join-Path $HarnessRoot "Program.cs"
$HarnessStubs = Join-Path $HarnessRoot "HyperWhisperStubs.cs"
$DiagnosticsSource = [System.Security.SecurityElement]::Escape((Join-Path $ProjectRoot "Services\TranscriptionDiagnosticsService.cs"))
$ProviderDiagnosticsSource = [System.Security.SecurityElement]::Escape((Join-Path $ProjectRoot "Services\Transcription\TranscriptionProviderDiagnostics.cs"))

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
    <Compile Include="$DiagnosticsSource" Link="TranscriptionDiagnosticsService.cs" />
    <Compile Include="$ProviderDiagnosticsSource" Link="TranscriptionProviderDiagnostics.cs" />
  </ItemGroup>
</Project>
"@ | Set-Content -LiteralPath $HarnessProject -Encoding UTF8

@'
namespace HyperWhisper.Data.Entities
{
    public sealed class Mode
    {
        public string? ProviderType { get; set; }
        public string? CloudProvider { get; set; }
        public string? CloudAccuracyTier { get; set; }
        public string? LocalEngine { get; set; }
        public string? Name { get; set; }
        public string? Preset { get; set; }
    }
}

namespace HyperWhisper.Models
{
    public enum TranscriptionErrorCode
    {
        Unknown = 0,
        NoSpeechDetected = 17
    }

    public sealed class TranscriptionException : Exception
    {
        public TranscriptionErrorCode Code { get; }
        public string? ProviderName { get; }
        public int? HttpStatusCode { get; }

        public TranscriptionException(TranscriptionErrorCode code, string message, string? providerName = null, int? httpStatusCode = null)
            : base(message)
        {
            Code = code;
            ProviderName = providerName;
            HttpStatusCode = httpStatusCode;
        }
    }
}

namespace HyperWhisper.Services
{
    public static class LoggingService
    {
        public static void Debug(string message) { }
    }

    public static class SentryService
    {
        public static void CaptureDiagnosticEvent(
            string message,
            Dictionary<string, object>? extras = null,
            Dictionary<string, string>? tags = null,
            string[]? fingerprint = null,
            string? dedupeKey = null)
        {
        }
    }
}
'@ | Set-Content -LiteralPath $HarnessStubs -Encoding UTF8

@'
using System.Reflection;
using HyperWhisper.Services;
using HyperWhisper.Services.Transcription;

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static object AudioDiagnostics(
    bool analysisSucceeded = true,
    double durationSeconds = 1.0,
    long fileSizeBytes = 4096,
    double peakDbfs = -10.0,
    double rmsDbfs = -20.0,
    double nonSilentRatio = 0.5,
    string? analysisError = null)
{
    var diagnosticsType = typeof(TranscriptionDiagnosticsService).GetNestedType(
        "AudioAnalysisDiagnostics",
        BindingFlags.NonPublic)
        ?? throw new MissingMemberException(typeof(TranscriptionDiagnosticsService).FullName, "AudioAnalysisDiagnostics");

    return Activator.CreateInstance(
        diagnosticsType,
        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
        binder: null,
        args: new object?[]
        {
            analysisSucceeded,
            durationSeconds,
            fileSizeBytes,
            16000,
            1,
            peakDbfs,
            rmsDbfs,
            nonSilentRatio,
            analysisError
        },
        culture: null)
        ?? throw new InvalidOperationException("Could not create AudioAnalysisDiagnostics.");
}

static bool ShouldCapture(object audioDiagnostics, TranscriptionProviderDiagnostics? providerDiagnostics = null)
{
    var method = typeof(TranscriptionDiagnosticsService).GetMethod(
        "ShouldCaptureNoSpeechDiagnostic",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(typeof(TranscriptionDiagnosticsService).FullName, "ShouldCaptureNoSpeechDiagnostic");

    return (bool)(method.Invoke(null, new object?[] { audioDiagnostics, providerDiagnostics })
        ?? throw new InvalidOperationException("ShouldCaptureNoSpeechDiagnostic returned null."));
}

static TranscriptionProviderDiagnostics ProviderDiagnostics(
    bool? backendNoSpeechDetected = null,
    bool? emptyTranscriptWithoutFlag = null)
{
    return new TranscriptionProviderDiagnostics(
        ProviderDisplayName: "Verifier Provider",
        BackendNoSpeechDetected: backendNoSpeechDetected,
        EmptyTranscriptWithoutFlag: emptyTranscriptWithoutFlag);
}

Assert(
    ShouldCapture(AudioDiagnostics(analysisSucceeded: false, analysisError: "decoder failed")),
    "Analysis failure must capture diagnostics because the audio signal could not be classified.");

Assert(
    ShouldCapture(AudioDiagnostics(durationSeconds: 0)),
    "Zero-duration analyzed files must capture diagnostics instead of being treated as expected silence.");

Assert(
    ShouldCapture(AudioDiagnostics(fileSizeBytes: 0)),
    "Empty analyzed files must capture diagnostics instead of being treated as expected silence.");

Assert(
    ShouldCapture(
        AudioDiagnostics(peakDbfs: -80.0, rmsDbfs: -120.0, nonSilentRatio: 0.0),
        ProviderDiagnostics(emptyTranscriptWithoutFlag: true)),
    "EmptyTranscriptWithoutFlag must capture diagnostics even when the audio looks silent.");

Assert(
    !ShouldCapture(AudioDiagnostics(peakDbfs: -80.0, rmsDbfs: -120.0, nonSilentRatio: 0.0)),
    "Confirmed silence should skip noisy diagnostics.");

Assert(
    !ShouldCapture(
        AudioDiagnostics(peakDbfs: -20.0, rmsDbfs: -55.0, nonSilentRatio: 0.02),
        ProviderDiagnostics(backendNoSpeechDetected: true)),
    "Backend-confirmed no speech on low-signal audio should skip noisy diagnostics.");

Assert(
    ShouldCapture(
        AudioDiagnostics(peakDbfs: -20.0, rmsDbfs: -45.0, nonSilentRatio: 0.02),
        ProviderDiagnostics(backendNoSpeechDetected: true)),
    "Backend no-speech with enough RMS signal must still capture diagnostics.");

Assert(
    ShouldCapture(
        AudioDiagnostics(peakDbfs: -20.0, rmsDbfs: -55.0, nonSilentRatio: 0.03),
        ProviderDiagnostics(backendNoSpeechDetected: true)),
    "Backend no-speech with enough non-silent samples must still capture diagnostics.");

Assert(
    ShouldCapture(AudioDiagnostics(peakDbfs: -20.0, rmsDbfs: -55.0, nonSilentRatio: 0.02)),
    "Low-signal audio without backend no-speech confirmation must still capture diagnostics.");

Console.WriteLine("No-speech diagnostic capture policy verification passed.");
'@ | Set-Content -LiteralPath $HarnessProgram -Encoding UTF8

dotnet run --project $HarnessProject --nologo
if ($LASTEXITCODE -ne 0) {
    throw "No-speech diagnostic capture policy harness failed with exit code $LASTEXITCODE."
}
