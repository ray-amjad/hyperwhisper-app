param()

$ErrorActionPreference = "Stop"

$ProjectRoot = Split-Path -Parent $PSScriptRoot
$RepoRoot = Resolve-Path (Join-Path $ProjectRoot "..\..\..")
$CatalogPath = Join-Path $RepoRoot "shared-app-classification\app-type-catalog.json"
$ContextualPromptsPath = Join-Path $RepoRoot "shared-prompts\contextual"
$ProjectPath = Join-Path $ProjectRoot "HyperWhisper.csproj"

function Assert-True {
    param(
        [bool] $Condition,
        [string] $Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Normalize-Host {
    param([string] $Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $null
    }

    $trimmed = $Value.Trim().ToLowerInvariant()
    if (-not $trimmed.Contains("://")) {
        $trimmed = "https://$trimmed"
    }

    try {
        $uri = [Uri]::new($trimmed)
        $normalizedUriHost = $uri.Host.ToLowerInvariant()
    }
    catch {
        $normalizedUriHost = $trimmed
    }

    if ($normalizedUriHost.StartsWith("www.")) {
        return $normalizedUriHost.Substring(4)
    }
    return $normalizedUriHost
}

function Test-KeywordMatch {
    param(
        [string] $Keyword,
        [string] $Title
    )

    $normalized = $Keyword.Trim().ToLowerInvariant()
    if ($normalized.Length -eq 0) {
        return $false
    }

    if ($normalized.Contains(".") -or $normalized.Contains("/") -or $normalized.Contains(" ")) {
        return $Title.Contains($normalized)
    }

    return [regex]::IsMatch(
        $Title,
        "(?<![A-Za-z0-9_])$([regex]::Escape($normalized))(?![A-Za-z0-9_])")
}

$catalog = Get-Content -LiteralPath $CatalogPath -Raw | ConvertFrom-Json
$orderedTypes = @(
    "sensitive",
    "email",
    "terminal",
    "code",
    "ai",
    "workMessaging",
    "personalMessaging",
    "document"
)

function Classify-App {
    param(
        [string] $ProcessName = "",
        [string] $BrowserHost = "",
        [string] $BrowserHostConfidence = "strong",
        [string] $WindowTitle = "",
        [string] $BrowserTabTitle = "",
        [string] $FocusedElementType = "",
        [string] $FocusedContent = ""
    )

    $normalizedHost = Normalize-Host $BrowserHost
    if (-not [string]::IsNullOrWhiteSpace($normalizedHost)) {
        foreach ($type in $orderedTypes) {
            $entry = $catalog.types.$type
            foreach ($catalogHost in @($entry.hosts)) {
                if ($normalizedHost -ieq $catalogHost -or $normalizedHost.EndsWith(".$catalogHost", [StringComparison]::OrdinalIgnoreCase)) {
                    return [pscustomobject]@{ AppType = $type; Source = "browserHost"; Confidence = $BrowserHostConfidence; Matched = $catalogHost }
                }
            }
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($ProcessName)) {
        foreach ($type in $orderedTypes) {
            $entry = $catalog.types.$type
            foreach ($process in @($entry.windowsProcesses)) {
                if ($ProcessName -ieq $process) {
                    return [pscustomobject]@{ AppType = $type; Source = "processName"; Confidence = "strong"; Matched = $process }
                }
            }
        }
    }

    $title = (@($BrowserTabTitle, $WindowTitle) |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) -join " "
    $title = $title.ToLowerInvariant()
    if (-not [string]::IsNullOrWhiteSpace($title)) {
        foreach ($type in $orderedTypes) {
            $entry = $catalog.types.$type
            foreach ($keyword in @($entry.titleKeywords)) {
                if (Test-KeywordMatch -Keyword $keyword -Title $title) {
                    return [pscustomobject]@{ AppType = $type; Source = "title"; Confidence = "medium"; Matched = $keyword }
                }
            }
        }
    }

    $focused = (@($FocusedElementType, $FocusedContent) |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) -join " "
    $focused = $focused.ToLowerInvariant()
    if ($focused.Contains("subject") -or $focused.Contains("compose") -or $focused.Contains("to:") -or $focused.Contains("cc:")) {
        return [pscustomobject]@{ AppType = "email"; Source = "focusedElement"; Confidence = "medium"; Matched = $null }
    }
    if ([regex]::IsMatch($focused, "\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b")) {
        return [pscustomobject]@{ AppType = "email"; Source = "focusedElementText"; Confidence = "weak"; Matched = $null }
    }

    return [pscustomobject]@{ AppType = "other"; Source = "default"; Confidence = "unknown"; Matched = $null }
}

$cases = @(
    @{ Name = "Browser Gmail host"; Expected = "email"; Result = Classify-App -ProcessName "chrome" -BrowserHost "mail.google.com" },
    @{ Name = "Cursor process priority"; Expected = "code"; Result = Classify-App -ProcessName "Cursor" },
    @{ Name = "Windows Terminal priority"; Expected = "terminal"; Result = Classify-App -ProcessName "WindowsTerminal" },
    @{ Name = "1Password sensitive priority"; Expected = "sensitive"; Result = Classify-App -ProcessName "1Password" },
    @{ Name = "Title word boundary"; Expected = "other"; Result = Classify-App -ProcessName "chrome" -WindowTitle "They have arrived - Google Chrome" },
    @{ Name = "Focused email compose"; Expected = "email"; Result = Classify-App -FocusedElementType "Edit" -FocusedContent "Subject" }
)

foreach ($case in $cases) {
    Assert-True ($case.Result.AppType -eq $case.Expected) (
        "$($case.Name) expected $($case.Expected), got $($case.Result.AppType) from $($case.Result.Source).")
}

$requiredPromptFiles = @(
    "email.txt",
    "work-message.txt",
    "personal-message.txt",
    "document.txt",
    "code.txt",
    "terminal.txt"
)

foreach ($fileName in $requiredPromptFiles) {
    $path = Join-Path $ContextualPromptsPath $fileName
    Assert-True (Test-Path -LiteralPath $path) "Missing contextual prompt file: $fileName."
    $content = Get-Content -LiteralPath $path -Raw
    Assert-True (-not [string]::IsNullOrWhiteSpace($content)) "Contextual prompt file is empty: $fileName."
}

$projectXml = Get-Content -LiteralPath $ProjectPath -Raw
Assert-True ($projectXml.Contains("shared-app-classification\app-type-catalog.json")) "Windows project must embed app-type-catalog.json."
Assert-True ($projectXml.Contains("shared-prompts\contextual")) "Windows project must embed shared contextual prompts."

$HarnessRoot = Join-Path $RepoRoot "artifacts\windows-runtime-validation\app-aware-formatting-verifier"
New-Item -ItemType Directory -Force -Path $HarnessRoot | Out-Null

$HarnessProject = Join-Path $HarnessRoot "AppAwareFormattingVerifier.csproj"
$HarnessProgram = Join-Path $HarnessRoot "Program.cs"
$ProjectReference = [System.Security.SecurityElement]::Escape($ProjectPath)

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
    <ProjectReference Include="$ProjectReference" />
  </ItemGroup>
</Project>
"@ | Set-Content -LiteralPath $HarnessProject -Encoding UTF8

@'
using HyperWhisper.Data.Entities;
using HyperWhisper.Services;
using HyperWhisper.Services.AppClassification;
using HyperWhisper.Utilities;
using ApplicationContext = HyperWhisper.Services.ApplicationContext;

static void AssertContains(string haystack, string needle, string label)
{
    if (!haystack.Contains(needle, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"{label} missing expected marker {needle}.");
    }
}

static void AssertNotContains(string haystack, string needle, string label)
{
    if (haystack.Contains(needle, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"{label} unexpectedly contained marker {needle}.");
    }
}

static ApplicationContext Context(AppType type, string processName, string textFormat) => new()
{
    ProcessName = processName,
    WindowTitle = $"{processName} test window",
    Category = type.ToCategory(),
    TextFormat = textFormat,
    AppType = type,
    AppTypeConfidence = "strong",
    AppTypeSource = "verifier"
};

var cases = new[]
{
    new { Label = "email", Mode = new Mode { Preset = "hyper" }, Context = Context(AppType.Email, "OUTLOOK", "email"), Marker = "<EMAIL_CONTEXT_DETECTED>", AppType = "email" },
    new { Label = "document", Mode = new Mode { Preset = "hyper" }, Context = Context(AppType.Document, "WINWORD", "markdown"), Marker = "<DOCUMENT_CONTEXT_DETECTED>", AppType = "document" },
    new { Label = "code", Mode = new Mode { Preset = "hyper" }, Context = Context(AppType.Code, "Code", "code"), Marker = "<CODE_CONTEXT_DETECTED>", AppType = "code" },
    new { Label = "terminal", Mode = new Mode { Preset = "hyper" }, Context = Context(AppType.Terminal, "WindowsTerminal", "command"), Marker = "<TERMINAL_CONTEXT_DETECTED>", AppType = "terminal" },
    new { Label = "work-message", Mode = new Mode { Preset = "message" }, Context = Context(AppType.WorkMessaging, "slack", "text"), Marker = "<WORK_MESSAGE_CONTEXT_DETECTED>", AppType = "work_messaging" },
    new { Label = "personal-message", Mode = new Mode { Preset = "message" }, Context = Context(AppType.PersonalMessaging, "Discord", "text"), Marker = "<PERSONAL_MESSAGE_CONTEXT_DETECTED>", AppType = "personal_messaging" },
};

foreach (var c in cases)
{
    var prompt = PromptBuilder.SystemPrompt(c.Mode, c.Context);
    var systemInfo = PromptBuilder.SystemInfo(c.Mode, applicationContext: c.Context);

    AssertContains(prompt, c.Marker, c.Label);
    AssertContains(systemInfo, $"<APP_TYPE>{c.AppType}</APP_TYPE>", c.Label);
    AssertContains(systemInfo, "<APP_TYPE_CONFIDENCE>strong</APP_TYPE_CONFIDENCE>", c.Label);
    AssertContains(systemInfo, "<APP_TYPE_SOURCE>verifier</APP_TYPE_SOURCE>", c.Label);
    AssertNotContains(prompt, "{{CONTEXTUAL_FORMATTING_BLOCK}}", c.Label);
    AssertNotContains(prompt, "{{EMAIL_BLOCK}}", c.Label);
}

Console.WriteLine("App-aware formatting prompt verification passed.");
'@ | Set-Content -LiteralPath $HarnessProgram -Encoding UTF8

dotnet run --project $HarnessProject --nologo
if ($LASTEXITCODE -ne 0) {
    throw "App classification harness failed with exit code $LASTEXITCODE."
}

Write-Host "App classification verification passed."
