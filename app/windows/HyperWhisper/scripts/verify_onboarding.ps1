# verify_onboarding.ps1
#
# Reusable gate for the Windows onboarding flow. The port of
# app/macos/scripts/verify-onboarding.sh, in the shape the 25 other verify_*.ps1
# scripts in this folder use.
#
#   1. Repo hygiene: no client-side entitlement bypass, no command-line trigger.
#   2. Static design conformance over the onboarding XAML.
#   3. Localization: every referenced key exists, all 40 .resx agree, no em dash.
#   4. Trigger and lifecycle wiring (Windows only; macOS has no equivalent).
#   5. Builds the head and runs the smoke suite.
#
# Behavioural regression guards are deliberately NOT pattern matches here. They
# live in HyperWhisper.SmokeTests, which asserts on behaviour, and section 5 is
# what runs them.
#
# NOTE: none of the scripts in this folder are wired into CI, so every check that
# has to hold on every PR also exists as a smoke case.
#
# Exit codes:
#   0   every hard check passed and the build + smoke suite passed
#   1   a hard check failed, or the build / smoke suite failed
#   2   the static checks passed but the build or the suite was skipped
#
# Usage:
#   pwsh app\windows\HyperWhisper\scripts\verify_onboarding.ps1
#   pwsh app\windows\HyperWhisper\scripts\verify_onboarding.ps1 -StaticOnly

param(
    [switch] $StaticOnly,
    [switch] $NoTests
)

$ErrorActionPreference = "Stop"

$ProjectRoot = Split-Path -Parent $PSScriptRoot
$WindowsRoot = Split-Path -Parent $ProjectRoot
$RepoRoot = Resolve-Path (Join-Path $ProjectRoot "..\..\..")

$ProjectFile = Join-Path $ProjectRoot "HyperWhisper.csproj"
$SmokeProject = Join-Path $WindowsRoot "HyperWhisper.SmokeTests\HyperWhisper.SmokeTests.csproj"
$ResourcesDir = Join-Path $ProjectRoot "Resources"
$BaseResx = Join-Path $ResourcesDir "Strings.resx"

$AppPath = Join-Path $ProjectRoot "App.xaml.cs"
$MainWindowPath = Join-Path $ProjectRoot "Views\Windows\MainWindow.xaml.cs"
$OnboardingWindowXaml = Join-Path $ProjectRoot "Views\Windows\OnboardingWindow.xaml"
$OnboardingWindowCode = Join-Path $ProjectRoot "Views\Windows\OnboardingWindow.xaml.cs"
$LaunchPolicyPath = Join-Path $ProjectRoot "Services\Onboarding\OnboardingLaunchPolicy.cs"
$SettingsServicePath = Join-Path $ProjectRoot "Services\SettingsService.cs"
$SmokeProgramPath = Join-Path $WindowsRoot "HyperWhisper.SmokeTests\Program.cs"

$StepPagesDir = Join-Path $ProjectRoot "Views\Pages\Onboarding"
$ControlsDir = Join-Path $ProjectRoot "Views\Controls\Onboarding"
$FlowDir = Join-Path $ProjectRoot "ViewModels\Onboarding"
$AdaptersDir = Join-Path $ProjectRoot "Services\Onboarding"

$EmDash = [string][char]0x2014

# The eight steps, in order. Load-bearing: Advance() is step+1 and 8 is the
# progress segment count, so a step added or dropped is a flow change.
$Steps = @("Welcome", "Permissions", "Source", "Configure", "Setup", "Microphone", "TryIt", "Done")

# CornerRadius literals allowed in onboarding XAML. 9/7/8 are the design tokens
# (CardCornerRadius / ButtonCornerRadius / InputCornerRadius in Themes\Generic.xaml);
# 0 is a deliberate square edge; 3 is a fully rounded 6px progress track, the same
# literal UpdateDownloadProgressWindow.xaml:54 already uses for the same shape.
$AllowedCornerRadii = @("0", "3", "7", "8", "9")

# ---------------------------------------------------------------------------
# Reporting
# ---------------------------------------------------------------------------

$script:HardFails = 0
$script:Warns = 0
$script:Passes = 0
$script:Blocked = $false
$script:FailLines = New-Object System.Collections.Generic.List[string]

function Write-Pass { param([string] $Message) $script:Passes++; Write-Host "[PASS] $Message" }
function Write-Fail {
    param([string] $Message)
    $script:HardFails++
    $script:FailLines.Add($Message) | Out-Null
    Write-Host "[FAIL] $Message"
}
function Write-Warn { param([string] $Message) $script:Warns++; Write-Host "[WARN] $Message" }
function Write-Info { param([string] $Message) Write-Host "[INFO] $Message" }
function Write-Section { param([string] $Title) Write-Host ""; Write-Host "== $Title ==" }
function Write-Detail { param([string] $Message) Write-Host "        $Message" }

function Show-Hits {
    param([object[]] $Hits, [int] $Limit = 8)

    $shown = 0
    foreach ($hit in $Hits) {
        if ($shown -ge $Limit) { Write-Detail "... (more suppressed)"; break }
        $text = "$hit"
        if ($text.Length -gt 200) { $text = $text.Substring(0, 200) }
        Write-Detail $text
        $shown++
    }
}

# Get-Content -Raw returns $null for a zero-byte file, and [regex]::Matches
# throws ArgumentNullException on it. Every read in this script goes through here.
function Read-Text {
    param([string] $Path)

    $text = Get-Content -LiteralPath $Path -Raw -ErrorAction SilentlyContinue
    if ($null -eq $text) { return "" }
    return $text
}

function Get-RelativePath {
    param([string] $Path)
    return $Path.Replace("$RepoRoot", "").TrimStart([char[]]@('\', '/'))
}

function Assert-Wired {
    param([string] $Text, [string] $Needle, [string] $Message)

    if ($Text.Contains($Needle)) { Write-Pass $Message } else { Write-Fail "$Message (missing: $Needle)" }
}

# ---------------------------------------------------------------------------
# File sets
# ---------------------------------------------------------------------------

function Get-FilesUnder {
    param([string] $Directory, [string] $Filter)

    if (-not (Test-Path -LiteralPath $Directory)) { return @() }
    return @(Get-ChildItem -LiteralPath $Directory -Filter $Filter -File -Recurse)
}

# The design surface: everything that draws the flow.
$DesignXaml = @()
$DesignXaml += Get-FilesUnder $StepPagesDir "*.xaml"
$DesignXaml += Get-FilesUnder $ControlsDir "*.xaml"
if (Test-Path -LiteralPath $OnboardingWindowXaml) { $DesignXaml += Get-Item -LiteralPath $OnboardingWindowXaml }

# The wider onboarding surface: everything that decides anything about the flow.
$OnboardingCs = @()
$OnboardingCs += Get-FilesUnder $FlowDir "*.cs"
$OnboardingCs += Get-FilesUnder $AdaptersDir "*.cs"
$OnboardingCs += Get-FilesUnder $StepPagesDir "*.cs"
$OnboardingCs += Get-FilesUnder $ControlsDir "*.cs"
if (Test-Path -LiteralPath $OnboardingWindowCode) { $OnboardingCs += Get-Item -LiteralPath $OnboardingWindowCode }

Write-Host "HyperWhisper Windows onboarding gate"
Write-Info "repo:          $RepoRoot"
Write-Info "design files:  $($DesignXaml.Count) XAML (window + steps + controls)"
Write-Info "onboarding cs: $($OnboardingCs.Count) files scanned"

if ($DesignXaml.Count -eq 0) { Write-Fail "no onboarding XAML found under $(Get-RelativePath $StepPagesDir)" }
if ($OnboardingCs.Count -eq 0) { Write-Fail "no onboarding C# found under $(Get-RelativePath $FlowDir)" }

# ===========================================================================
Write-Section "1. Repo hygiene"
# ===========================================================================

# --- no client-side entitlement bypass -------------------------------------
# Paid moat: the HyperWhisper Cloud entitlement is enforced server side. A debug
# backdoor or a fake license key introduced during onboarding would hand the
# product away, and no unit test would catch it, so it is grepped for here.
$bypassPattern = "bypassLicense|skipLicenseCheck|fakeLicense|testLicenseKey|debugEntitlement|forceEntitled|HYPERWHISPER_DEBUG_LICENSE"
$bypassHits = @($DesignXaml + $OnboardingCs | Select-String -Pattern $bypassPattern)
if ($bypassHits.Count -gt 0) {
    Write-Fail "possible client-side entitlement bypass in onboarding (Cloud entitlement is enforced server side)"
    Show-Hits $bypassHits 6
} else {
    Write-Pass "no client-side entitlement bypass in the onboarding sources"
}

# --- no command-line trigger -----------------------------------------------
# SingleInstanceGuard.TryAcquire() runs as App.OnStartup's first statement and
# kills the second instance before e.Args is ever inspected, so an --onboarding
# switch would silently do nothing whenever the app was already running. The
# supported levers are the tray entry, HYPERWHISPER_WINDOWS_APPDATA_ROOT, and
# hand-editing settings.json with the app closed.
$switchHits = @(Get-ChildItem -LiteralPath $ProjectRoot -Filter "*.cs" -File -Recurse |
    Select-String -Pattern '"--onboarding|--onboarding"|--run-onboarding|--show-onboarding')
if ($switchHits.Count -gt 0) {
    Write-Fail "an onboarding command-line switch was added; SingleInstanceGuard consumes e.Args before it can be read"
    Show-Hits $switchHits 6
} else {
    Write-Pass "onboarding has no command-line switch (the trigger is a settings flag plus one env var)"
}

# --- the first-run flag stays out of the backup snapshot -------------------
# OnboardingPending is machine-local install state, the same class of thing as
# GettingStartedCompletedSteps and LocalApiServerPersistedPort. Restoring a
# backup must not re-run setup, or re-suppress it, on a different machine.
if (Test-Path -LiteralPath $SettingsServicePath) {
    $settingsSource = Read-Text $SettingsServicePath
    $snapshotMatch = [regex]::Match($settingsSource, "BuildBackupSettingsSnapshot\s*\(")
    if (-not $snapshotMatch.Success) {
        Write-Warn "BuildBackupSettingsSnapshot not found in SettingsService.cs; the backup exclusion is unverified"
    } else {
        $tail = $settingsSource.Substring($snapshotMatch.Index)
        $braceStart = $tail.IndexOf("{")
        $depth = 0
        $body = $tail
        for ($i = $braceStart; $i -lt $tail.Length; $i++) {
            if ($tail[$i] -eq "{") { $depth++ }
            elseif ($tail[$i] -eq "}") {
                $depth--
                if ($depth -eq 0) { $body = $tail.Substring($braceStart, $i - $braceStart + 1); break }
            }
        }
        if ($body.Contains("OnboardingPending")) {
            Write-Fail "OnboardingPending is written into BuildBackupSettingsSnapshot(); first-run state must not travel in a .hwbackup.json"
        } else {
            Write-Pass "OnboardingPending stays out of BuildBackupSettingsSnapshot()"
        }
    }
} else {
    Write-Fail "SettingsService.cs not found at $(Get-RelativePath $SettingsServicePath)"
}

# ===========================================================================
Write-Section "2. Static design conformance"
# ===========================================================================

# --- no hardcoded colour literals ------------------------------------------
$colorHits = @($DesignXaml | Select-String -Pattern '"#[0-9A-Fa-f]{3,8}"')
if ($colorHits.Count -gt 0) {
    Write-Fail "hardcoded colour literal in onboarding XAML (semantic brushes only; it has to be correct in light and dark)"
    Show-Hits $colorHits 10
} else {
    Write-Pass "no hardcoded colour literals in the onboarding XAML"
}

# --- brushes are DynamicResource -------------------------------------------
# HARD rule, .ui-consistency-sweep.md:65-68. ThemeService swaps the *colors*
# dictionary at runtime, so a StaticResource brush freezes in the launch theme
# and the flow half-changes colour when the user flips the theme mid-setup.
$staticBrushHits = @($DesignXaml | Select-String -Pattern '\{StaticResource\s+[A-Za-z0-9]*Brush\}')
if ($staticBrushHits.Count -gt 0) {
    Write-Fail "a brush is bound with StaticResource in onboarding XAML (ThemeService swaps colors at runtime; use DynamicResource)"
    Show-Hits $staticBrushHits 10
} else {
    Write-Pass "every brush in the onboarding XAML is a DynamicResource"
}

# --- corner radius scale ---------------------------------------------------
$radiusHits = @()
foreach ($hit in @($DesignXaml | Select-String -Pattern 'CornerRadius="([0-9]+)"' -AllMatches)) {
    foreach ($match in $hit.Matches) {
        if ($AllowedCornerRadii -notcontains $match.Groups[1].Value) {
            $radiusHits += "$($hit.Path):$($hit.LineNumber): $($match.Value)"
        }
    }
}
if ($radiusHits.Count -gt 0) {
    Write-Fail "CornerRadius literal off the $($AllowedCornerRadii -join '/') scale (use CardCornerRadius / ButtonCornerRadius / InputCornerRadius)"
    Show-Hits $radiusHits 10
} else {
    Write-Pass "every CornerRadius literal in the onboarding XAML is on the $($AllowedCornerRadii -join '/') scale"
}

# --- the window is the macOS stage plus the caption row --------------------
# 760 x 624 = macOS's 760 x 580 sheet plus the 44px WPF caption row, so the
# stage itself matches the original exactly.
if (Test-Path -LiteralPath $OnboardingWindowXaml) {
    $windowXaml = Read-Text $OnboardingWindowXaml
    if ($windowXaml -match 'Width="760"' -and $windowXaml -match 'Height="624"') {
        Write-Pass "OnboardingWindow is 760 x 624 (the 760 x 580 macOS stage plus the 44px caption row)"
    } else {
        Write-Fail "OnboardingWindow is not 760 x 624"
    }

    if ($windowXaml -match 'ResizeMode="NoResize"') {
        Write-Pass "OnboardingWindow is non-resizable, like the macOS sheet"
    } else {
        Write-Fail "OnboardingWindow is resizable; the steps are laid out against a fixed stage"
    }
} else {
    Write-Fail "OnboardingWindow.xaml not found at $(Get-RelativePath $OnboardingWindowXaml)"
}

# --- all 8 steps present, rendered, and reachable --------------------------
$stepEnumPath = Join-Path $FlowDir "OnboardingStep.cs"
if (Test-Path -LiteralPath $stepEnumPath) {
    $enumSource = Read-Text $stepEnumPath
    $enumMatch = [regex]::Match($enumSource, "enum\s+OnboardingStep\s*\{(?<body>[^}]*)\}")
    if (-not $enumMatch.Success) {
        Write-Fail "no 'enum OnboardingStep' declaration found in $(Get-RelativePath $stepEnumPath)"
    } else {
        $cases = @([regex]::Matches($enumMatch.Groups["body"].Value, "(?m)^\s*([A-Za-z]+)\s*=\s*[0-9]+") |
            ForEach-Object { $_.Groups[1].Value })
        if ($cases.Count -ne 8) {
            Write-Fail "OnboardingStep declares $($cases.Count) cases, expected 8 (Advance() is step+1 and 8 is the progress segment count)"
        } elseif (@(Compare-Object $cases $Steps -SyncWindow 0).Count -ne 0) {
            Write-Fail "OnboardingStep case order is $($cases -join ', '); expected $($Steps -join ', ')"
        } else {
            Write-Pass "OnboardingStep declares all 8 steps in the macOS order"
        }
    }
} else {
    Write-Fail "OnboardingStep.cs not found at $(Get-RelativePath $stepEnumPath)"
}

$missingPages = @()
foreach ($step in $Steps) {
    $page = Join-Path $StepPagesDir "$($step)StepPage.xaml"
    if (-not (Test-Path -LiteralPath $page)) { $missingPages += "$($step)StepPage.xaml" }
}
if ($missingPages.Count -gt 0) {
    Write-Fail "missing step page(s): $($missingPages -join ', ')"
} else {
    Write-Pass "all 8 step pages exist under $(Get-RelativePath $StepPagesDir)"
}

if (Test-Path -LiteralPath $OnboardingWindowCode) {
    $windowCode = Read-Text $OnboardingWindowCode
    $unrendered = @($Steps | Where-Object { -not $windowCode.Contains("$($_)StepPage()") })
    if ($unrendered.Count -gt 0) {
        Write-Fail "step(s) never navigated to by OnboardingWindow: $($unrendered -join ', ')"
    } else {
        Write-Pass "OnboardingWindow's step switch reaches all 8 pages"
    }
}

# --- every step scrolls ----------------------------------------------------
# macOS wraps each step in GeometryReader { ScrollView { ... } } so a short step
# stays centred and a long one scrolls. A fixed 760 x 624 non-resizable window
# has the same overflow risk with less forgiveness: a step that is not staged
# loses its lower half at 125% scaling with no wheel to reach it.
$unstaged = @()
foreach ($step in $Steps) {
    $page = Join-Path $StepPagesDir "$($step)StepPage.xaml"
    if (-not (Test-Path -LiteralPath $page)) { continue }
    if (-not (Read-Text $page).Contains("OnboardingStage")) { $unstaged += "$($step)StepPage.xaml" }
}
if ($unstaged.Count -gt 0) {
    Write-Fail "step page(s) not wrapped in an OnboardingStage, so their content can be stranded: $($unstaged -join ', ')"
} else {
    Write-Pass "every step page is wrapped in the scrolling OnboardingStage"
}

# ===========================================================================
Write-Section "3. Localization"
# ===========================================================================

function Get-ResxOnboardingKeys {
    param([string] $Path)

    $keys = New-Object System.Collections.Generic.List[string]
    foreach ($match in [regex]::Matches((Read-Text $Path), '<data\s+name="(onboarding\.[^"]+)"')) {
        $keys.Add($match.Groups[1].Value) | Out-Null
    }
    return @($keys | Sort-Object -Unique)
}

if (-not (Test-Path -LiteralPath $BaseResx)) {
    Write-Fail "Strings.resx not found at $(Get-RelativePath $BaseResx)"
    $definedKeys = @()
} else {
    $definedKeys = Get-ResxOnboardingKeys $BaseResx
}

# --- every referenced key exists -------------------------------------------
# Two reference forms: Loc.S(quoted key) in C#, and a {loc:Loc key} markup
# extension in XAML. LocExtension resolves once at PARSE time, so a missing key
# renders as its own identifier on screen rather than throwing.
#
# The extension filter is a Where-Object, not -Include: -Include is silently
# ignored alongside -LiteralPath, which sweeps the .resx files (where every key
# appears as a quoted name= attribute) and this script itself into the "used"
# set, and every key then looks referenced.
$usedKeys = New-Object System.Collections.Generic.List[string]
$keySites = @{}
$sourceFiles = @(Get-ChildItem -LiteralPath $ProjectRoot -File -Recurse |
    Where-Object { $_.Extension -eq ".cs" -or $_.Extension -eq ".xaml" } |
    Where-Object { $_.FullName -notmatch "\\(bin|obj)\\" })
foreach ($file in $sourceFiles) {
    $text = Read-Text $file.FullName
    foreach ($match in [regex]::Matches($text, '(?:"|loc:Loc\s+)(onboarding\.[A-Za-z0-9._]+)')) {
        $key = $match.Groups[1].Value.TrimEnd(".")
        if (-not $keySites.ContainsKey($key)) { $keySites[$key] = Get-RelativePath $file.FullName }
        $usedKeys.Add($key) | Out-Null
    }
}
$usedKeys = @($usedKeys | Sort-Object -Unique)

$missingKeys = @($usedKeys | Where-Object { $definedKeys -notcontains $_ })
if ($missingKeys.Count -gt 0) {
    Write-Fail "$($missingKeys.Count) onboarding key(s) referenced in code but missing from Strings.resx"
    Show-Hits @($missingKeys | ForEach-Object { "$_  ($($keySites[$_]))" }) 12
} else {
    Write-Pass "every onboarding.* key referenced in C#/XAML exists in Strings.resx ($($usedKeys.Count) key(s))"
}

$unreferenced = @($definedKeys | Where-Object { $usedKeys -notcontains $_ })
Write-Info "Strings.resx defines $($definedKeys.Count) onboarding.* keys; $($usedKeys.Count) referenced, $($unreferenced.Count) unreferenced (dead keys are informational)"

# --- all 40 .resx carry an identical onboarding.* key set ------------------
# The repo's practice is English placeholders in every locale file, so the KEY
# SET has to be identical even while the translations lag. A locale missing a
# key falls through to whatever the resource manager finds, which is how a
# German user ends up reading one English line in the middle of a German step.
$resxFiles = @(Get-ChildItem -LiteralPath $ResourcesDir -Filter "*.resx" -File | Sort-Object Name)
$mismatched = @()
foreach ($resx in $resxFiles) {
    if ($resx.FullName -eq $BaseResx) { continue }
    $keys = Get-ResxOnboardingKeys $resx.FullName
    $diff = @(Compare-Object $definedKeys $keys)
    if ($diff.Count -ne 0) {
        $missing = @($diff | Where-Object { $_.SideIndicator -eq "<=" } | ForEach-Object { $_.InputObject })
        $extra = @($diff | Where-Object { $_.SideIndicator -eq "=>" } | ForEach-Object { $_.InputObject })
        $mismatched += "$($resx.Name): $($keys.Count) keys, $($missing.Count) missing, $($extra.Count) extra"
    }
}
if ($resxFiles.Count -lt 40) {
    Write-Warn "only $($resxFiles.Count) .resx files found under $(Get-RelativePath $ResourcesDir); expected 40"
}
if ($mismatched.Count -gt 0) {
    Write-Fail "$($mismatched.Count) locale file(s) do not carry the base onboarding.* key set"
    Show-Hits $mismatched 12
} else {
    Write-Pass "all $($resxFiles.Count) .resx files carry an identical $($definedKeys.Count)-key onboarding.* set"
}

# --- no em dash ------------------------------------------------------------
# Carried from the macOS gate. The copy uses the middle dot and the ellipsis.
$emDashSources = @($DesignXaml + $OnboardingCs | Select-String -Pattern $EmDash -SimpleMatch)
if ($emDashSources.Count -gt 0) {
    Write-Fail "em dash found in onboarding sources ($($emDashSources.Count) line(s))"
    Show-Hits $emDashSources 10
} else {
    Write-Pass "no em dash in the onboarding sources"
}

$emDashValues = @()
foreach ($resx in $resxFiles) {
    $text = Read-Text $resx.FullName
    foreach ($match in [regex]::Matches($text, '<data\s+name="(onboarding\.[^"]+)"[^>]*>\s*<value>(?<value>[^<]*)</value>')) {
        if ($match.Groups["value"].Value.Contains($EmDash)) {
            $emDashValues += "$($resx.Name): $($match.Groups[1].Value)"
        }
    }
}
if ($emDashValues.Count -gt 0) {
    Write-Fail "em dash found in $($emDashValues.Count) onboarding.* value(s)"
    Show-Hits $emDashValues 10
} else {
    Write-Pass "no em dash in any onboarding.* value in any .resx"
}

# --- no unlocalized user-visible literal -----------------------------------
# Allowed: Text="{loc:Loc ...}", Text="{Binding ...}", and a bare Segoe MDL2
# glyph entity. Flagged: anything with two consecutive letters left once the
# entities are stripped, i.e. real copy typed straight into the XAML.
$unlocalized = @()
foreach ($hit in @($DesignXaml | Select-String -Pattern '(?:Text|Content|ToolTip|Header)="(?<literal>[^"{][^"]*)"' -AllMatches)) {
    foreach ($match in $hit.Matches) {
        $probe = [regex]::Replace($match.Groups["literal"].Value, "&#x[0-9A-Fa-f]+;", "")
        if ($probe -match "[A-Za-z]{2}") {
            $unlocalized += "$($hit.Path):$($hit.LineNumber): $($match.Value)"
        }
    }
}
if ($unlocalized.Count -gt 0) {
    Write-Fail "unlocalized literal in onboarding XAML ($($unlocalized.Count) occurrence(s))"
    Show-Hits $unlocalized 12
} else {
    Write-Pass "no unlocalized user-visible literal in the onboarding XAML"
}

# ===========================================================================
Write-Section "4. Trigger and lifecycle"
# ===========================================================================

if (Test-Path -LiteralPath $LaunchPolicyPath) {
    $policy = Read-Text $LaunchPolicyPath
    Assert-Wired $policy "HYPERWHISPER_WINDOWS_SKIP_ONBOARDING" "the opt-out environment variable is named in OnboardingLaunchPolicy"
    if ($policy -match "IsAppDataRootOverridden") {
        Write-Fail "OnboardingLaunchPolicy consults the app-data override; a scratch profile is how a fresh OnboardingPending is produced"
    } else {
        Write-Pass "OnboardingLaunchPolicy does not consult the app-data override"
    }
} else {
    Write-Fail "OnboardingLaunchPolicy.cs not found at $(Get-RelativePath $LaunchPolicyPath)"
}

if (Test-Path -LiteralPath $AppPath) {
    $appSource = Read-Text $AppPath
    Assert-Wired $appSource "ShouldShowOnboarding = OnboardingLaunchPolicy.ShouldShowOnboarding()" `
        "App.OnStartup decides the first-run flow through OnboardingLaunchPolicy"

    # The override guard belongs to the StartupService block ABOVE this one,
    # which registers a machine-wide Run key. Onboarding writes nothing outside
    # AppDataRoot, so a scratch profile must still see it.
    $decisionIndex = $appSource.IndexOf("ShouldShowOnboarding = OnboardingLaunchPolicy")
    if ($decisionIndex -ge 0) {
        $lineStart = $appSource.LastIndexOf("`n", $decisionIndex)
        $window = $appSource.Substring([Math]::Max(0, $lineStart - 400), [Math]::Min(600, $appSource.Length - [Math]::Max(0, $lineStart - 400)))
        if ($window -match "if\s*\([^)]*IsAppDataRootOverridden[^)]*\)[^;]*ShouldShowOnboarding") {
            Write-Fail "the onboarding decision is gated on AppPaths.IsAppDataRootOverridden"
        } else {
            Write-Pass "the onboarding decision is not gated on AppPaths.IsAppDataRootOverridden"
        }
    }
} else {
    Write-Fail "App.xaml.cs not found at $(Get-RelativePath $AppPath)"
}

if (Test-Path -LiteralPath $MainWindowPath) {
    $mainSource = Read-Text $MainWindowPath

    Assert-Wired $mainSource "if (App.ShouldShowOnboarding)" "MainWindow shows the flow from its Loaded handler"
    Assert-Wired $mainSource "new OnboardingWindow(live.Flow) { Owner = this }" "the onboarding window is owned by the main window"
    Assert-Wired $mainSource "OnboardingLiveDependencies.CreateLive(" "the flow is built at the single live composition point"
    Assert-Wired $mainSource "live?.DisposeResources();" "the seams' OS resources are released when the window closes"
    Assert-Wired $mainSource "TextDeliveryGate.SetSuppressed(true);" "text delivery is suppressed while the flow is open"
    Assert-Wired $mainSource "TextDeliveryGate.SetSuppressed(false);" "text delivery is restored when the flow closes"
    Assert-Wired $mainSource 'Loc.S("onboarding.menu.runAgain")' "the tray carries a Run setup again entry"
    Assert-Wired $mainSource "SettingsService.Instance.LaunchMinimized && !App.ShouldShowOnboarding" `
        "the LaunchMinimized hide is gated on the first-run flow"
    Assert-Wired $mainSource "_recordingMenu.Enabled = !_isOnboardingOpen" `
        "the tray cannot start a competing recording while the flow is open"

    # Ordering, not cosmetics. OnNavigatedToAsync is what populates AudioDevices
    # and registers the hotkeys; the Permissions and Microphone steps read both,
    # and KeyboardShortcutService needs this window to own an HWND or the
    # shortcut row reports Unknown forever. And the hide has to come last, or a
    # first-run user with LaunchMinimized on gets an ownerless modal.
    $navIndex = $mainSource.IndexOf("await _viewModel.OnNavigatedToAsync();")
    $showIndex = $mainSource.IndexOf("if (App.ShouldShowOnboarding)")
    $hideIndex = $mainSource.IndexOf("SettingsService.Instance.LaunchMinimized && !App.ShouldShowOnboarding")
    if ($navIndex -ge 0 -and $showIndex -gt $navIndex -and $hideIndex -gt $showIndex) {
        Write-Pass "the flow is shown after OnNavigatedToAsync and before the LaunchMinimized hide"
    } else {
        Write-Fail "startup ordering is wrong: expected OnNavigatedToAsync, then the onboarding show, then the LaunchMinimized hide"
        Write-Detail "OnNavigatedToAsync=$navIndex show=$showIndex hide=$hideIndex"
    }
} else {
    Write-Fail "MainWindow.xaml.cs not found at $(Get-RelativePath $MainWindowPath)"
}

if (Test-Path -LiteralPath $OnboardingWindowCode) {
    $windowCode = Read-Text $OnboardingWindowCode

    Assert-Wired $windowCode "_flow.DeferSetup();" "closing the window is treated as Set Up Later, so every staged write is rolled back"
    Assert-Wired $windowCode "Closing += OnWindowClosing;" "Alt+F4 and the taskbar go through the same Set Up Later path"
    Assert-Wired $windowCode "_flow.Complete();" "the last step commits the staged configuration"
    Assert-Wired $windowCode "e.Key == Key.Escape" "Escape cannot throw away a half-finished setup (macOS interactiveDismissDisabled)"
    Assert-Wired $windowCode "_flow.Cleanup();" "the flow detaches from its seams when the window closes"
} else {
    Write-Fail "OnboardingWindow.xaml.cs not found at $(Get-RelativePath $OnboardingWindowCode)"
}

if (Test-Path -LiteralPath $SmokeProgramPath) {
    $smokeSource = Read-Text $SmokeProgramPath
    Assert-Wired $smokeSource "OnboardingLaunchPolicy.SkipEnvironmentVariable" `
        "the smoke harness opts out of the first-run flow, so a future harness that boots the real App cannot hang on a modal"
} else {
    Write-Fail "the smoke harness was not found at $(Get-RelativePath $SmokeProgramPath)"
}

# ===========================================================================
Write-Section "5. Build and smoke suite"
# ===========================================================================

if ($StaticOnly) {
    $script:Blocked = $true
    Write-Warn "build and smoke suite skipped (-StaticOnly): this run cannot certify the app"
} else {
    Write-Info "building HyperWhisper (Release)"
    dotnet build $ProjectFile -c Release -v minimal --nologo
    if ($LASTEXITCODE -ne 0) {
        Write-Fail "dotnet build FAILED (exit $LASTEXITCODE)"
    } else {
        Write-Pass "the Windows head builds in Release"

        if ($NoTests) {
            $script:Blocked = $true
            Write-Warn "smoke suite skipped (-NoTests): this run cannot certify the app"
        } else {
            Write-Info "running HyperWhisper.SmokeTests (Release)"
            dotnet run --project $SmokeProject -c Release --nologo
            if ($LASTEXITCODE -ne 0) {
                Write-Fail "HyperWhisper.SmokeTests FAILED (exit $LASTEXITCODE)"
            } else {
                Write-Pass "HyperWhisper.SmokeTests is green"
            }
        }
    }
}

# ===========================================================================
Write-Section "Verdict"
# ===========================================================================

if ($script:HardFails -gt 0) {
    Write-Host "FAIL: $($script:HardFails) hard check(s) failed, $($script:Warns) warning(s), $($script:Passes) passed."
    foreach ($line in $script:FailLines) { Write-Detail "- $line" }
    exit 1
}

if ($script:Blocked) {
    Write-Host "INCOMPLETE: the static checks passed but the build or the suite was skipped, so this is NOT a pass."
    exit 2
}

Write-Host "PASS: build green, smoke suite green, $($script:Passes) checks passed, $($script:Warns) warning(s)."
exit 0
