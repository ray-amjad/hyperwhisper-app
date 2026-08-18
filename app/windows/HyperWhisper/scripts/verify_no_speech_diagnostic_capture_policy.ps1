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
    string? analysisError = null,
    long? decodedSampleCount = 16000)
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
            analysisError,
            decodedSampleCount
        },
        culture: null)
        ?? throw new InvalidOperationException("Could not create AudioAnalysisDiagnostics.");
}

// True only when the input is reported AS a no-speech diagnostic. An empty
// recording is also reported - under its own name - and is false here; use
// Classify() for "is anything reported at all".
static bool ShouldCaptureAsNoSpeech(object audioDiagnostics, TranscriptionProviderDiagnostics? providerDiagnostics = null)
{
    var method = typeof(TranscriptionDiagnosticsService).GetMethod(
        "ShouldCaptureAsNoSpeech",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(typeof(TranscriptionDiagnosticsService).FullName, "ShouldCaptureAsNoSpeech");

    return (bool)(method.Invoke(null, new object?[] { audioDiagnostics, providerDiagnostics })
        ?? throw new InvalidOperationException("ShouldCaptureAsNoSpeech returned null."));
}

// The classification itself: "Skip" (nothing reported), "EmptyRecording" or
// "NoSpeech". Returned as a string so the harness does not need the enum type.
static string Classify(object audioDiagnostics, TranscriptionProviderDiagnostics? providerDiagnostics = null)
{
    var method = typeof(TranscriptionDiagnosticsService).GetMethod(
        "ClassifyNoSpeechDiagnostic",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(typeof(TranscriptionDiagnosticsService).FullName, "ClassifyNoSpeechDiagnostic");

    return (method.Invoke(null, new object?[] { audioDiagnostics, providerDiagnostics })
        ?? throw new InvalidOperationException("ClassifyNoSpeechDiagnostic returned null.")).ToString()!;
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
    ShouldCaptureAsNoSpeech(AudioDiagnostics(analysisSucceeded: false, analysisError: "decoder failed")),
    "Analysis failure must capture diagnostics because the audio signal could not be classified.");

// An empty recording is still reported - it just stopped being reported as a
// no-speech result. The discriminator is the decoded sample count, not the
// duration: the live-recording call site substitutes the wall-clock recording
// length whenever the container reports no duration, so a header-only file from
// a 5-second recording arrives here with durationSeconds 5.0.
Assert(
    Classify(AudioDiagnostics(durationSeconds: 5.0, fileSizeBytes: 44, decodedSampleCount: 0)) == "EmptyRecording",
    "A recording the decoder produced no frames for must classify as an empty recording.");

Assert(
    Classify(
        AudioDiagnostics(
            durationSeconds: 5.0,
            fileSizeBytes: 44,
            peakDbfs: -120.0,
            rmsDbfs: -120.0,
            nonSilentRatio: 0.0,
            decodedSampleCount: 0),
        ProviderDiagnostics(backendNoSpeechDetected: true)) != "Skip",
    "Zero-frame analyzed files must capture diagnostics instead of being treated as expected silence.");

Assert(
    !ShouldCaptureAsNoSpeech(AudioDiagnostics(durationSeconds: 5.0, fileSizeBytes: 44, decodedSampleCount: 0)),
    "An empty recording must be reported under its own name, not as a no-speech diagnostic.");

// The mirror image: a decodable file whose container reports no duration is not
// a recorder failure - nothing was recorded on that path in the first place.
Assert(
    Classify(AudioDiagnostics(durationSeconds: 0, decodedSampleCount: 16000)) == "NoSpeech",
    "A file with decoded frames but no container duration must not be called an empty recording.");

Assert(
    ShouldCaptureAsNoSpeech(
        AudioDiagnostics(peakDbfs: -80.0, rmsDbfs: -120.0, nonSilentRatio: 0.0),
        ProviderDiagnostics(emptyTranscriptWithoutFlag: true)),
    "EmptyTranscriptWithoutFlag must capture diagnostics even when the audio looks silent.");

Assert(
    !ShouldCaptureAsNoSpeech(AudioDiagnostics(peakDbfs: -80.0, rmsDbfs: -120.0, nonSilentRatio: 0.0)),
    "Confirmed silence should skip noisy diagnostics.");

Assert(
    !ShouldCaptureAsNoSpeech(
        AudioDiagnostics(peakDbfs: -20.0, rmsDbfs: -55.0, nonSilentRatio: 0.02),
        ProviderDiagnostics(backendNoSpeechDetected: true)),
    "Backend-confirmed no speech on low-signal audio should skip noisy diagnostics.");

// The two "enough signal" samples below sit either side of the low-signal
// thresholds as they ship today (-38.0dBFS / 0.06, widened in #160). Their
// original values (-45.0dBFS and 0.03) were written against the older, stricter
// -50.0dBFS / 0.02 pair and have been inside the skip window - i.e. asserting
// the opposite of the shipped policy - since that change. What they assert is
// unchanged; only the sample values move back outside the window.
Assert(
    ShouldCaptureAsNoSpeech(
        AudioDiagnostics(peakDbfs: -20.0, rmsDbfs: -30.0, nonSilentRatio: 0.02),
        ProviderDiagnostics(backendNoSpeechDetected: true)),
    "Backend no-speech with enough RMS signal must still capture diagnostics.");

Assert(
    ShouldCaptureAsNoSpeech(
        AudioDiagnostics(peakDbfs: -20.0, rmsDbfs: -55.0, nonSilentRatio: 0.1),
        ProviderDiagnostics(backendNoSpeechDetected: true)),
    "Backend no-speech with enough non-silent samples must still capture diagnostics.");

Assert(
    ShouldCaptureAsNoSpeech(AudioDiagnostics(peakDbfs: -20.0, rmsDbfs: -55.0, nonSilentRatio: 0.02)),
    "Low-signal audio without backend no-speech confirmation must still capture diagnostics.");

Console.WriteLine("No-speech diagnostic capture policy verification passed.");
'@ | Set-Content -LiteralPath $HarnessProgram -Encoding UTF8

dotnet run --project $HarnessProject --nologo
if ($LASTEXITCODE -ne 0) {
    throw "No-speech diagnostic capture policy harness failed with exit code $LASTEXITCODE."
}
