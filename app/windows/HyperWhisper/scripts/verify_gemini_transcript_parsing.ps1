param()

$ErrorActionPreference = "Stop"

$ProjectRoot = Split-Path -Parent $PSScriptRoot
$RepoRoot = Resolve-Path (Join-Path $ProjectRoot "..\..\..")
$HarnessRoot = Join-Path $RepoRoot "artifacts\windows-runtime-validation\gemini-transcript-parser-verifier"
New-Item -ItemType Directory -Force -Path $HarnessRoot | Out-Null

$HarnessProject = Join-Path $HarnessRoot "GeminiTranscriptParserVerifier.csproj"
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
using System.Reflection;
using HyperWhisper.Models;
using HyperWhisper.Services;

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static string Parse(GeminiTranscriptionService service, MethodInfo method, string json)
{
    try
    {
        return (string)(method.Invoke(service, new object[] { json })
            ?? throw new InvalidOperationException("Parser returned null."));
    }
    catch (TargetInvocationException ex) when (ex.InnerException is not null)
    {
        throw ex.InnerException;
    }
}

static void AssertNoSpeech(GeminiTranscriptionService service, MethodInfo method, string json, string label)
{
    try
    {
        _ = Parse(service, method, json);
        throw new InvalidOperationException($"{label}: expected NoSpeechDetected exception.");
    }
    catch (TranscriptionException ex)
    {
        Assert(ex.Code == TranscriptionErrorCode.NoSpeechDetected, $"{label}: expected NoSpeechDetected, got {ex.Code}.");
        Assert(ex.ProviderName == "Gemini", $"{label}: expected Gemini provider name.");
    }
}

var service = new GeminiTranscriptionService();
var parseMethod = typeof(GeminiTranscriptionService).GetMethod(
    "ParseTranscriptFromResponseJson",
    BindingFlags.Instance | BindingFlags.NonPublic)
    ?? throw new MissingMethodException(typeof(GeminiTranscriptionService).FullName, "ParseTranscriptFromResponseJson");

var multipart = """
{
  "candidates": [
    {
      "content": {
        "parts": [
          { "text": " first segment " },
          { "thought": true, "text": " internal reasoning must be ignored " },
          { "text": "second segment" },
          { "inlineData": { "mimeType": "text/plain", "data": "ignored" } },
          { "text": "\nthird segment " }
        ]
      }
    }
  ]
}
""";

var parsed = Parse(service, parseMethod, multipart);
Assert(parsed == "first segment second segment\nthird segment", $"Multipart parse mismatch: '{parsed}'");

var secondCandidateIgnored = """
{
  "candidates": [
    { "content": { "parts": [ { "text": "primary candidate" } ] } },
    { "content": { "parts": [ { "text": "secondary candidate" } ] } }
  ]
}
""";
Assert(Parse(service, parseMethod, secondCandidateIgnored) == "primary candidate", "Parser should use the first candidate.");

AssertNoSpeech(service, parseMethod, """{ "candidates": [] }""", "empty candidates");
AssertNoSpeech(service, parseMethod, """{ "candidates": [ { "content": { "parts": [ { "thought": true, "text": "hidden" } ] } } ] }""", "thought-only parts");
AssertNoSpeech(service, parseMethod, """{ "candidates": [ { "content": { "parts": [ { "text": "   " } ] } } ] }""", "blank text");

service.Dispose();
Console.WriteLine("Gemini transcript parser verification passed.");
'@ | Set-Content -LiteralPath $HarnessProgram -Encoding UTF8

dotnet run --project $HarnessProject --nologo
